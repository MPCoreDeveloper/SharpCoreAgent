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
/// (Principle III). A non-zero exit code is a result, not an exception: a failing build is exactly the
/// kind of information the model asked for.
/// <para>
/// Provider credentials are removed from the child environment. The shell exists to run builds and
/// tests, and an arbitrary command should not be able to read the key that pays for the conversation.
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
        """{"type":"object","properties":{"command":{"type":"string","description":"Command line to execute in the workspace."},"timeout_seconds":{"type":"integer","minimum":1,"maximum":600},"working_directory":{"type":"string","description":"Relative to the workspace root. Defaults to the root."}},"required":["command"],"additionalProperties":false}""";

    private readonly string _shellFileName;
    private readonly string _shellCommandFlag;

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
    /// The launcher is chosen once, at construction, and never per call: a tool whose interpreter
    /// depends on its arguments is a tool the model can steer.
    /// </remarks>
    public RunShellCommandTool(string shellFileName, string shellCommandFlag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shellFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(shellCommandFlag);

        _shellFileName = shellFileName;
        _shellCommandFlag = shellCommandFlag;
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Run a shell command in the workspace and return its exit code plus stdout and stderr. Use it for builds, tests and version control. A non-zero exit code is reported, not thrown.",
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

        using var process = new Process { StartInfo = CreateStartInfo(workingDirectory, command) };
        try
        {
            if (!process.Start())
            {
                return ToolResult.Failure(toolCall.Id, Name, $"could not start '{_shellFileName}'");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return ToolResult.Failure(toolCall.Id, Name, $"could not start '{_shellFileName}': {ex.Message}");
        }

        // Both streams are drained while the process runs; a command that fills a pipe buffer would
        // otherwise deadlock waiting for a reader that only starts after exit.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            cancellationToken.ThrowIfCancellationRequested();
            timedOut = true;
        }

        var stdout = await Drain(stdoutTask).ConfigureAwait(false);
        var stderr = await Drain(stderrTask).ConfigureAwait(false);

        if (timedOut)
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"command timed out after {timeoutSeconds}s and was killed: {command}"));
        }

        var output = Merge(stdout, stderr, out var truncated);
        var header = string.Create(
            CultureInfo.InvariantCulture,
            $"$ {command}\nexit code: {process.ExitCode}");

        return ToolResult.Success(
            toolCall.Id,
            Name,
            output.Length == 0
                ? string.Concat(header, "\n(no output)")
                : string.Concat(header, "\n", output),
            new ToolEffect(ToolEffectKind.CommandRun, command)) with
        { IsTruncated = truncated };
    }

    private ProcessStartInfo CreateStartInfo(string workingDirectory, string command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _shellFileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = TextLimits.Utf8NoBom,
            StandardErrorEncoding = TextLimits.Utf8NoBom,
        };

        // The command travels as one argument to the shell. No quoting, escaping or concatenation
        // happens here, so there is no string for a caller to break out of.
        startInfo.ArgumentList.Add(_shellCommandFlag);
        startInfo.ArgumentList.Add(command);

        foreach (var name in startInfo.Environment.Keys.ToArray())
        {
            if (IsSecretName(name))
            {
                startInfo.Environment.Remove(name);
            }
        }

        return startInfo;
    }

    private static bool IsSecretName(string name) =>
        name.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_TOKEN", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_SECRET", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_PASSWORD", StringComparison.OrdinalIgnoreCase);

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or AggregateException or Win32Exception)
        {
            // The process already exited, or a child it spawned cannot be reached; either way the
            // caller is about to hear about the timeout, which is the fact that matters.
        }
    }

    private static async Task<string> Drain(Task<string> reader)
    {
        try
        {
            return await reader.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static string Merge(string stdout, string stderr, out bool truncated)
    {
        var text = stdout;
        if (stderr.Length > 0)
        {
            if (text.Length > 0 && !text.EndsWith('\n'))
            {
                text = string.Concat(text, "\n");
            }

            text = string.Concat(text, stderr);
        }

        return TextLimits.Bound(text.TrimEnd('\n', '\r'), TextLimits.MaxCommandOutputChars, out truncated);
    }
}
