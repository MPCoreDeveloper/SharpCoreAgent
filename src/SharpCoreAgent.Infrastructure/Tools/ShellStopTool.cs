using System.Globalization;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Ends a command that <c>run_shell</c> left running.
/// </summary>
/// <remarks>
/// Stopping is a destructive act even though it creates nothing: it kills a process tree the developer
/// started, and a process tree can do anything. It is therefore refused in plan mode and in read-only
/// approval mode like any other destructive tool, and the model is told which handle it failed to stop so a
/// wrong one is corrected rather than retried.
/// <para>
/// The tool is idempotent. Stopping a process that already exited reports the exit code instead of failing,
/// because that is the answer the caller was looking for either way.
/// </para>
/// </remarks>
public sealed class ShellStopTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "shell_stop";

    private const string Schema =
        """{"type":"object","properties":{"process_id":{"type":"string","description":"The handle run_shell returned, such as sh-1."}},"required":["process_id"],"additionalProperties":false}""";

    /// <summary>Gets how long the tool waits for the kill to take effect.</summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(2);

    private readonly ShellProcessRegistry _processes;

    /// <summary>
    /// Creates the tool.
    /// </summary>
    /// <param name="processes">The registry that owns the background commands.</param>
    /// <exception cref="ArgumentNullException"><paramref name="processes"/> is null.</exception>
    public ShellStopTool(ShellProcessRegistry processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        _processes = processes;
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Stop a command that run_shell left running in the background, killing its whole process tree. Reports the exit code when the process had already finished.",
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
        var id = arguments.GetRequiredString("process_id");

        if (!_processes.TryGet(id, out var process))
        {
            return ToolResult.Failure(toolCall.Id, Name, Unknown(id));
        }

        if (process.HasExited)
        {
            return ToolResult.Success(toolCall.Id, Name, AlreadyFinished(process));
        }

        // Whatever it printed on the way out is the most useful part of stopping it, so it is kept.
        var trailing = await process.DrainAsync(StopGrace).ConfigureAwait(false);

        if (!_processes.Stop(id))
        {
            return ToolResult.Success(toolCall.Id, Name, AlreadyFinished(process));
        }

        await process.WaitAsync(StopGrace, cancellationToken).ConfigureAwait(false);

        var header = string.Create(
            CultureInfo.InvariantCulture,
            $"stopped {process.Id}: {process.Command}");
        var body = TextLimits.Bound(trailing.TrimEnd('\n', '\r'), TextLimits.MaxCommandOutputChars, out var truncated);
        var text = body.Length == 0 ? header : string.Concat(header, "\n", body);

        return ToolResult.Success(toolCall.Id, Name, text) with { IsTruncated = truncated };
    }

    private static string AlreadyFinished(ShellProcess process) =>
        process.ExitCode is { } code
            ? string.Create(CultureInfo.InvariantCulture, $"{process.Id} had already exited with code {code}: {process.Command}")
            : string.Create(CultureInfo.InvariantCulture, $"{process.Id} had already exited: {process.Command}");

    private string Unknown(string id)
    {
        var running = _processes.Running;
        var known = running.Count == 0
            ? "No command is running."
            : $"Running commands: {string.Join(", ", running)}.";

        return $"no process with id '{id}'. {known}";
    }
}
