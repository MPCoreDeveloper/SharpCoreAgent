using System.Globalization;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>Saves or replaces a persistent project note, approval-gated like every write.</summary>
/// <remarks>
/// The writable half of the project memory. A name is confined to a slug and a body is written verbatim, bounded by
/// <see cref="ProjectMemoryStore"/>. The result echoes the slug and byte count, never the body.
/// </remarks>
public sealed class RememberTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "remember";

    private const string Schema =
        """{"type":"object","properties":{"name":{"type":"string","description":"Note slug; lowercased, spaces to dashes, only [a-z0-9-]."},"body":{"type":"string","description":"The note text (UTF-8, max 16 KiB). Written verbatim; never executed."}},"required":["name","body"],"additionalProperties":false}""";

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Save or replace a persistent project note (a convention, an architecture fact, a decision) so a later session can read it with project_memory. Destructive and approval-gated. Written verbatim; never executed.",
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
        if (!ProjectMemoryStore.TrySlug(arguments.GetOptionalString("name"), out var slug, out var error))
        {
            return ToolResult.Failure(toolCall.Id, Name, error!);
        }

        string body;
        try
        {
            body = arguments.GetRequiredString("body", allowEmpty: true);
        }
        catch (ToolArgumentException ex)
        {
            return ToolResult.Failure(toolCall.Id, Name, ex.Message);
        }

        var write = await ProjectMemoryStore.WriteAsync(context.Workspace, slug, body, cancellationToken)
            .ConfigureAwait(false);
        if (!write.Ok)
        {
            return ToolResult.Failure(toolCall.Id, Name, write.Error!);
        }

        var verb = write.Created ? "saved" : "updated";
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{verb} note '{slug}' ({write.Bytes} bytes)");
        return ToolResult.Success(
            toolCall.Id,
            Name,
            text,
            new ToolEffect(
                write.Created ? ToolEffectKind.Created : ToolEffectKind.Modified,
                string.Concat(".sharpcoreagent/memory/", slug, ProjectMemoryStore.Extension)));
    }
}
