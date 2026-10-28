using SharpCoreAgent.Application;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Reports the primary root and every read-only attached root.
/// </summary>
/// <remarks>
/// This is how a model learns an absolute path it may use, which is the whole reason the feature needs no new
/// path syntax: the model reads the root instead of inventing one. It is the one tool result allowed to print
/// absolute paths, and it is read-only, so no approval mode has anything to ask about it.
/// </remarks>
public sealed class ListRootsTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "list_roots";

    private const string Schema =
        """{"type":"object","properties":{},"required":[],"additionalProperties":false}""";

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "List the workspace root and every read-only attached root. A relative path is resolved against the primary root; an attachment is reached by an absolute path. Use it before reading outside the workspace, and never to guess a path.",
        Schema);

    /// <inheritdoc />
    public ToolSafety Safety => ToolSafety.ReadOnly;

    /// <inheritdoc />
    public ValueTask<ToolResult> ExecuteAsync(
        ToolCall toolCall,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(context);

        // Parsed even though the schema has no arguments: the reader is also what rejects a malformed call, and a
        // tool that skipped that check would be the one place garbage argument text went unnoticed.
        using var arguments = ToolArgumentReader.Parse(Name, toolCall.ArgumentsJson);

        return ValueTask.FromResult(ToolResult.Success(
            toolCall.Id,
            Name,
            RootListing.Describe(context.Workspace),
            new ToolEffect(ToolEffectKind.Read, ".")));
    }
}
