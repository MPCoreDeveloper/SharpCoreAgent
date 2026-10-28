using System.Globalization;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Reads what a background command printed since the last read.
/// </summary>
/// <remarks>
/// This is the other half of <c>run_shell</c>: a dev server or a test runner that outlived its call is
/// useful only if its output can be read later, and its exit code is the only way to know a watch-mode
/// build started rather than crashed. Draining is deliberate — a read returns new text and empties the
/// buffer, so two reads never pay for the same lines twice in the model's context.
/// <para>
/// The tool is read-only: it cannot start, signal or change a process. Stopping is <c>shell_stop</c>'s job,
/// which is destructive and therefore refused in plan mode.
/// </para>
/// </remarks>
public sealed class ShellOutputTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "shell_output";

    /// <summary>Gets the longest wait a caller may request, in seconds.</summary>
    public const int MaxWaitSeconds = 60;

    private const string Schema =
        """{"type":"object","properties":{"process_id":{"type":"string","description":"The handle run_shell returned, such as sh-1."},"wait_seconds":{"type":"integer","minimum":0,"maximum":60,"description":"How long to wait for more output before returning what is already buffered. Defaults to 0, which returns immediately."}},"required":["process_id"],"additionalProperties":false}""";

    /// <summary>Gets how long the tool waits for a reader to finish after the process exited.</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromMilliseconds(250);

    private readonly ShellProcessRegistry _processes;

    /// <summary>
    /// Creates the tool.
    /// </summary>
    /// <param name="processes">The registry that owns the background commands.</param>
    /// <exception cref="ArgumentNullException"><paramref name="processes"/> is null.</exception>
    public ShellOutputTool(ShellProcessRegistry processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        _processes = processes;
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Read the output a background command produced since the last read, and whether it is still running or has exited. Use it with the process_id run_shell returned.",
        Schema);

    /// <inheritdoc />
    public ToolSafety Safety => ToolSafety.ReadOnly;

    /// <inheritdoc />
    public async ValueTask<ToolResult> ExecuteAsync(
        ToolCall toolCall,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(context);

        using var arguments = ToolArgumentReader.Parse(Name, toolCall.ArgumentsJson);
        var id = arguments.GetRequiredString("process_id");
        var waitSeconds = arguments.GetOptionalInt("wait_seconds", 0, minimum: 0, maximum: MaxWaitSeconds);

        if (!_processes.TryGet(id, out var process))
        {
            return ToolResult.Failure(toolCall.Id, Name, Unknown(id));
        }

        if (waitSeconds > 0 && !process.HasExited)
        {
            await process.WaitAsync(TimeSpan.FromSeconds(waitSeconds), cancellationToken).ConfigureAwait(false);
        }

        var text = await process.DrainAsync(DrainGrace).ConfigureAwait(false);
        var status = "running";
        if (process.HasExited)
        {
            status = process.ExitCode is { } code
                ? string.Create(CultureInfo.InvariantCulture, $"exited {code}")
                : "exited";
        }

        var header = string.Create(
            CultureInfo.InvariantCulture,
            $"{process.Id} ({process.Command}): {status}");
        var body = TextLimits.Bound(text.TrimEnd('\n', '\r'), TextLimits.MaxCommandOutputChars, out var truncated);

        return ToolResult.Success(
            toolCall.Id,
            Name,
            body.Length == 0 ? string.Concat(header, "\n(no new output)") : string.Concat(header, "\n", body)) with
        { IsTruncated = truncated };
    }

    private string Unknown(string id)
    {
        var running = _processes.Running;
        var known = running.Count == 0
            ? "No command is running."
            : $"Running commands: {string.Join(", ", running)}.";

        return $"no process with id '{id}'. {known}";
    }
}
