using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Runs a command line through the platform shell and reports what it printed.
/// </summary>
/// <remarks>
/// The command is handed to the shell as a single argument — never spliced into a script file and never
/// evaluated by the host — so the only interpreter involved is the one the user already trusts
/// (Principle III). A non-zero exit code is a result, not an exception: a failing build is exactly the kind
/// of information the model asked for.
/// <para>
/// Output is streamed as it arrives while the call is waiting, so a forty-second build is something the
/// developer watches rather than something that looks like a hang. The same text is still returned to the
/// model, bounded, so nothing depends on anybody watching.
/// </para>
/// <para>
/// <c>wait_seconds</c> shorter than <c>timeout_seconds</c> turns the call into a start: the command keeps
/// running past the turn, gets a handle such as <c>sh-1</c>, and is read with <c>shell_output</c> and ended
/// with <c>shell_stop</c>. That is how a dev server or a watch-mode compiler is used. A command that
/// outlives <c>timeout_seconds</c> while the call is still waiting is killed, exactly as before.
/// </para>
/// <para>
/// Provider credentials are removed from the child environment. The shell exists to run builds and tests,
/// and an arbitrary command should not be able to read the key that pays for the conversation.
/// </para>
/// <para>
/// The child gets an empty keyboard. Its standard input is a redirected stream that is closed before the
/// command runs, so a program that asks a question reads end-of-input — the same thing it would get from
/// <c>&lt; /dev/null</c> — instead of competing with the developer's own prompt for the next line typed.
/// Without that, one interactive command started by a turn could swallow the question the developer
/// typed after it, which looks exactly like a frozen session.
/// </para>
/// </remarks>
public sealed class RunShellCommandTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "run_shell";

    /// <summary>Gets how long a command may run before it is killed, in seconds.</summary>
    public const int DefaultTimeoutSeconds = 120;

    /// <summary>Gets the longest timeout a caller may request, in seconds.</summary>
    public const int MaxTimeoutSeconds = 600;

    private const string Schema =
        """{"type":"object","properties":{"command":{"type":"string","description":"Command line to execute in the workspace."},"timeout_seconds":{"type":"integer","minimum":1,"maximum":600},"wait_seconds":{"type":"integer","minimum":0,"maximum":600,"description":"How long to wait before returning. Omit to wait for the command to finish. A value smaller than timeout_seconds leaves the command running and returns a process_id."},"working_directory":{"type":"string","description":"Relative to the workspace root. Defaults to the root."}},"required":["command"],"additionalProperties":false}""";

    /// <summary>Gets how long the call waits for output readers after the process exits.</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromMilliseconds(250);

    private readonly string _shellFileName;
    private readonly string _shellCommandFlag;
    private readonly ShellProcessRegistry _processes;

    /// <summary>
    /// Initializes a new instance of the <see cref="RunShellCommandTool"/> class with the platform's shell.
    /// </summary>
    public RunShellCommandTool()
        : this(
            OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            OperatingSystem.IsWindows() ? "/c" : "-c")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RunShellCommandTool"/> class with an explicit shell.
    /// </summary>
    /// <param name="shellFileName">The executable that provides the shell.</param>
    /// <param name="shellCommandFlag">The flag that makes the shell run its next argument as a command.</param>
    /// <remarks>
    /// The launcher is chosen once, at construction, and never per call: a tool whose interpreter depends on
    /// its arguments is a tool the model can steer. A private registry is created, which is what a test with
    /// one tool wants; the container shares one registry across the tools that talk to each other.
    /// </remarks>
    public RunShellCommandTool(string shellFileName, string shellCommandFlag)
        : this(shellFileName, shellCommandFlag, new ShellProcessRegistry())
    {
    }


    /// <summary>
    /// Initializes a new instance of the <see cref="RunShellCommandTool"/> class with the platform's shell
    /// and a shared process registry.
    /// </summary>
    /// <param name="processes">The registry that owns the commands this tool starts.</param>
    public RunShellCommandTool(ShellProcessRegistry processes)
        : this(
            OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            OperatingSystem.IsWindows() ? "/c" : "-c",
            processes)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RunShellCommandTool"/> class with an explicit shell and
    /// a shared process registry.
    /// </summary>
    /// <param name="shellFileName">The executable that provides the shell.</param>
    /// <param name="shellCommandFlag">The flag that makes the shell run its next argument as a command.</param>
    /// <param name="processes">The registry that owns the commands this tool starts.</param>
    /// <exception cref="ArgumentException">A shell argument is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="processes"/> is null.</exception>
    public RunShellCommandTool(string shellFileName, string shellCommandFlag, ShellProcessRegistry processes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shellFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(shellCommandFlag);
        ArgumentNullException.ThrowIfNull(processes);

        _shellFileName = shellFileName;
        _shellCommandFlag = shellCommandFlag;
        _processes = processes;
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Run a shell command in the workspace and return its exit code plus stdout and stderr. Use it for builds, tests and version control. A non-zero exit code is reported, not thrown. The command's stdin is empty, so a command that waits for input gets end-of-input instead of hanging. Pass wait_seconds smaller than timeout_seconds to leave a long-running command in the background and get a process_id for shell_output and shell_stop.",
        Schema);

    /// <inheritdoc />
    public ToolSafety Safety => ToolSafety.Destructive;

    /// <inheritdoc />
    public async ValueTask<ToolResult> ExecuteAsync(
        ToolCall toolCall,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(context);

        using var arguments = ToolArgumentReader.Parse(Name, toolCall.ArgumentsJson);
        var command = arguments.GetRequiredString("command");
        var timeoutSeconds = arguments.GetOptionalInt(
            "timeout_seconds",
            DefaultTimeoutSeconds,
            minimum: 1,
            maximum: MaxTimeoutSeconds);
        var waitSeconds = arguments.Has("wait_seconds")
            ? arguments.GetOptionalInt("wait_seconds", 0, minimum: 0, maximum: MaxTimeoutSeconds)
            : timeoutSeconds;
        var workingDirectoryArgument = arguments.GetOptionalString("working_directory");

        var workingDirectory = context.Workspace.Root;
        if (workingDirectoryArgument is { Length: > 0 })
        {
            if (!PathSafety.TryResolve(context.Workspace, workingDirectoryArgument, out var resolved, out var error))
            {
                return ToolResult.Refused(toolCall.Id, Name, error!);
            }

            if (!Directory.Exists(resolved))
            {
                return ToolResult.Failure(
                    toolCall.Id,
                    Name,
                    $"no directory at '{context.Workspace.ToRelative(resolved)}'");
            }

            workingDirectory = resolved;
        }

        // A call that returns before the command ends is a start, not a timeout: the timeout only means
        // "kill it" while this call is still the one waiting.
        var background = waitSeconds < timeoutSeconds;
        var lifetime = background ? ShellProcessRegistry.MaxBackgroundLifetime : TimeSpan.FromSeconds(timeoutSeconds);

        ShellProcess process;
        try
        {
            process = _processes.Start(
                command,
                CreateStartInfo(workingDirectory, command),
                context.Output,
                lifetime);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return ToolResult.Failure(toolCall.Id, Name, $"could not start '{_shellFileName}': {ex.Message}");
        }

        var finished = false;
        try
        {
            finished = await process
                .WaitAsync(TimeSpan.FromSeconds(waitSeconds), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The turn was cancelled. Kill the tree before letting the cancellation through, or a Ctrl+C
            // would leave the build running with nobody reading its output.
            process.TryStop();
            throw;
        }

        if (!finished && background)
        {
            process.StopStreaming();
            return await BackgroundAsync(toolCall, process, command, waitSeconds).ConfigureAwait(false);
        }

        if (!finished)
        {
            process.TryStop();
            var (partial, _) = await DrainAsync(process).ConfigureAwait(false);
            var message = string.Create(
                CultureInfo.InvariantCulture,
                $"command timed out after {timeoutSeconds}s and was killed: {command}");

            return ToolResult.Failure(
                toolCall.Id,
                Name,
                partial.Length == 0 ? message : string.Concat(message, "\n", partial));
        }

        var (output, truncated) = await DrainAsync(process).ConfigureAwait(false);
        var header = string.Create(
            CultureInfo.InvariantCulture,
            $"$ {command}\nexit code: {process.ExitCode ?? -1}");

        return ToolResult.Success(
            toolCall.Id,
            Name,
            output.Length == 0 ? string.Concat(header, "\n(no output)") : string.Concat(header, "\n", output),
            new ToolEffect(ToolEffectKind.CommandRun, command)) with
        { IsTruncated = truncated };
    }


    /// <summary>
    /// Reports a command that is still running, with the handle that reaches it later.
    /// </summary>
    /// <param name="toolCall">The call being answered.</param>
    /// <param name="process">The tracked process.</param>
    /// <param name="command">The command line, echoed in the result.</param>
    /// <param name="waitSeconds">How long the call waited before handing back.</param>
    /// <returns>A successful result that names the handle.</returns>
    private static async Task<ToolResult> BackgroundAsync(
        ToolCall toolCall,
        ShellProcess process,
        string command,
        int waitSeconds)
    {
        var (output, truncated) = await DrainAsync(process).ConfigureAwait(false);
        var header = string.Create(
            CultureInfo.InvariantCulture,
            $"$ {command}\nrunning: {process.Id} (still running after {waitSeconds}s; read more with shell_output, stop it with shell_stop)");

        return ToolResult.Success(
            toolCall.Id,
            Name,
            output.Length == 0 ? string.Concat(header, "\n(no output yet)") : string.Concat(header, "\n", output),
            new ToolEffect(ToolEffectKind.CommandRun, command)) with
        { IsTruncated = truncated };
    }

    /// <summary>
    /// Collects what the process printed, bounded and without its final newline.
    /// </summary>
    /// <param name="process">The process to drain.</param>
    /// <returns>The text, and whether it was shortened to fit the budget.</returns>
    private static async Task<(string Text, bool Truncated)> DrainAsync(ShellProcess process)
    {
        var text = await process.DrainAsync(DrainGrace).ConfigureAwait(false);
        var bounded = TextLimits.Bound(text.TrimEnd('\n', '\r'), TextLimits.MaxCommandOutputChars, out var truncated);
        return (bounded, truncated);
    }

    private ProcessStartInfo CreateStartInfo(string workingDirectory, string command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _shellFileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // The developer's keyboard belongs to the CLI, not to the command it asked for: without this,
            // a child started through the console inherits it and can take the line typed at the next
            // prompt, which reads as a session that stopped responding.
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = TextLimits.Utf8NoBom,
            StandardErrorEncoding = TextLimits.Utf8NoBom,
        };

        // The command travels as one argument to the shell. No quoting, escaping or concatenation happens
        // here, so there is no string for a caller to break out of.
        startInfo.ArgumentList.Add(_shellCommandFlag);
        startInfo.ArgumentList.Add(command);

        foreach (var name in startInfo.Environment.Keys.Where(IsSecretName).ToArray())
        {
            startInfo.Environment.Remove(name);
        }

        return startInfo;
    }

    private static bool IsSecretName(string name) =>
        name.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_TOKEN", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_SECRET", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_PASSWORD", StringComparison.OrdinalIgnoreCase);
}

