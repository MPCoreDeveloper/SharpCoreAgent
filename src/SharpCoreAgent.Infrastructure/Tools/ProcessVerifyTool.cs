using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using SharpCoreAgent.Application;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Runs one language's test command through the shared process registry and reports whether it passed.
/// </summary>
/// <remarks>
/// This is the common part of <c>python_verify</c> and <c>node_verify</c>: argument validation, workspace-relative
/// target resolution, the timeout, bounded output and the effect list. It deliberately does not add a third process
/// door — every child is started through <see cref="ShellProcessRegistry"/>, the same host as <c>run_shell</c>.
/// <para>
/// A failing test run is an error result because a verifier answers whether the work passed, not merely whether the
/// command ran. The output still travels in the result so the model can fix what failed.
/// </para>
/// </remarks>
public abstract class ProcessVerifyTool : ITool, IVerificationTool
{
    /// <summary>Gets how long a run may take before it is killed, in seconds.</summary>
    public const int DefaultTimeoutSeconds = 300;

    /// <summary>Gets the longest timeout a caller may request, in seconds.</summary>
    public const int MaxTimeoutSeconds = 900;

    private static readonly TimeSpan DrainGrace = TimeSpan.FromMilliseconds(250);

    private readonly string _fileName;
    private readonly string _missingRuntime;
    private readonly ShellProcessRegistry _processes;

    /// <summary>Initializes a new instance of the <see cref="ProcessVerifyTool"/> class.</summary>
    /// <param name="definition">The tool definition shown to the model.</param>
    /// <param name="profiles">The language profiles this tool verifies.</param>
    /// <param name="fileName">The executable to start.</param>
    /// <param name="missingRuntime">The sentence that explains an executable that could not start.</param>
    /// <param name="processes">The registry that owns the child processes.</param>
    protected ProcessVerifyTool(
        ToolDefinition definition,
        IReadOnlyList<VerificationProfile> profiles,
        string fileName,
        string missingRuntime,
        ShellProcessRegistry processes)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(missingRuntime);
        ArgumentNullException.ThrowIfNull(processes);

        Definition = definition;
        Profiles = profiles;
        _fileName = fileName;
        _missingRuntime = missingRuntime;
        _processes = processes;
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; }

    /// <inheritdoc />
    public ToolSafety Safety => ToolSafety.Destructive;

    /// <inheritdoc />
    public IReadOnlyList<VerificationProfile> Profiles { get; }

    /// <summary>Gets the executable this tool starts.</summary>
    public string FileName => _fileName;

    /// <summary>Builds the command line a run is described by and records as its effect.</summary>
    /// <param name="target">The workspace-relative test target, or null for the workspace root.</param>
    /// <returns>The command as the developer would type it.</returns>
    public string CommandFor(string? target) =>
        string.Join(' ', [_fileName, .. ArgumentsFor(target)]);

    /// <summary>Builds the arguments for one test target.</summary>
    /// <param name="target">The workspace-relative target, or null for the workspace root.</param>
    /// <returns>The argument list, without the executable.</returns>
    protected abstract IReadOnlyList<string> ArgumentsFor(string? target);

    /// <summary>Gets the label the result header uses for the kind of run.</summary>
    protected abstract string Kind { get; }

    /// <inheritdoc />
    public async ValueTask<ToolResult> ExecuteAsync(
        ToolCall toolCall,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(context);

        using var arguments = ToolArgumentReader.Parse(Definition.Name, toolCall.ArgumentsJson);
        var timeoutSeconds = arguments.GetOptionalInt(
            "timeout_seconds",
            DefaultTimeoutSeconds,
            minimum: 1,
            maximum: MaxTimeoutSeconds);
        var targetArgument = arguments.GetOptionalString("target");

        string? target = null;
        if (targetArgument is { Length: > 0 })
        {
            if (!PathSafety.TryResolveWrite(
                context.Workspace,
                context.Approval,
                targetArgument,
                out var resolved,
                out _,
                out var error))
            {
                return ToolResult.Refused(toolCall.Id, Definition.Name, error!);
            }

            if (!File.Exists(resolved) && !Directory.Exists(resolved))
            {
                return ToolResult.Failure(
                    toolCall.Id,
                    Definition.Name,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"no test target at '{context.Workspace.ToRelative(resolved)}'; omit target to run the workspace root."));
            }

            target = context.Workspace.ToRelative(resolved).Replace('\\', '/');
        }

        var command = CommandFor(target);
        var startedAt = Stopwatch.GetTimestamp();
        ShellProcess process;
        try
        {
            process = _processes.Start(
                command,
                CreateStartInfo(target, context.Workspace.Root),
                context.Output,
                TimeSpan.FromSeconds(timeoutSeconds));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return ToolResult.Failure(
                toolCall.Id,
                Definition.Name,
                $"could not start '{_fileName}': {ex.Message}. {_missingRuntime}");
        }

        var finished = false;
        try
        {
            finished = await process
                .WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            process.TryStop();
            throw;
        }

        if (!finished)
        {
            process.TryStop();
            var (partial, _) = await DrainAsync(process).ConfigureAwait(false);
            var message = string.Create(
                CultureInfo.InvariantCulture,
                $"'{command}' timed out after {timeoutSeconds}s and was killed. Raise timeout_seconds, or run the test command with run_shell and poll it with shell_output.");

            return ToolResult.Failure(
                toolCall.Id,
                Definition.Name,
                partial.Length == 0 ? message : string.Concat(message, "\n", partial));
        }

        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        var (output, truncated) = await DrainAsync(process).ConfigureAwait(false);
        var passed = process.ExitCode == 0;
        var content = Render(Kind, target ?? "(workspace root)", command, process.ExitCode ?? -1, passed, elapsed, output, truncated);

        return new ToolResult
        {
            CallId = toolCall.Id,
            ToolName = Definition.Name,
            Content = content,
            IsError = !passed,
            IsTruncated = truncated,
            Effects = [new ToolEffect(ToolEffectKind.CommandRun, command)],
        };
    }

    private static string Render(
        string kind,
        string target,
        string command,
        int exitCode,
        bool passed,
        TimeSpan elapsed,
        string output,
        bool truncated)
    {
        var builder = new StringBuilder();
        builder.Append("kind         ").AppendLine(kind);
        builder.Append("target       ").AppendLine(target);
        builder.Append("command      ").AppendLine(command);
        builder.Append("exit code    ").AppendLine(exitCode.ToString(CultureInfo.InvariantCulture));
        builder.Append("result       ").AppendLine(passed ? "passed" : "failed");
        builder.Append("duration     ").AppendLine(string.Create(CultureInfo.InvariantCulture, $"{elapsed.TotalSeconds:0.0}s"));
        if (truncated)
        {
            builder.AppendLine("truncated    yes");
        }

        builder.AppendLine();
        builder.AppendLine(output.Length == 0 ? "(no output)" : output);
        return builder.ToString();
    }

    private static async Task<(string Text, bool Truncated)> DrainAsync(ShellProcess process)
    {
        var text = await process.DrainAsync(DrainGrace).ConfigureAwait(false);
        var bounded = TextLimits.Bound(text.TrimEnd('\n', '\r'), TextLimits.MaxCommandOutputChars, out var truncated);
        return (bounded, truncated);
    }

    private ProcessStartInfo CreateStartInfo(string? target, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = TextLimits.Utf8NoBom,
            StandardErrorEncoding = TextLimits.Utf8NoBom,
        };

        foreach (var argument in ArgumentsFor(target))
        {
            startInfo.ArgumentList.Add(argument);
        }

        ChildEnvironment.RemoveSecrets(startInfo);
        return startInfo;
    }
}
