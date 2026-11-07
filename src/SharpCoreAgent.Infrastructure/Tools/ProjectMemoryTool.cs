using System.Globalization;
using System.Text;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>Reads the project's persistent notes: conventions, architecture facts and prior decisions.</summary>
/// <remarks>
/// Read-only, so Plan and Review can consult the memory without gaining a write path. <c>list</c> names the notes with
/// their timestamps; <c>get</c> returns one. The companion writer is <see cref="RememberTool"/>.
/// </remarks>
public sealed class ProjectMemoryTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "project_memory";

    /// <summary>Gets the operation that lists notes.</summary>
    public const string List = "list";

    /// <summary>Gets the operation that returns one note.</summary>
    public const string Get = "get";

    private const string Schema =
        """{"type":"object","properties":{"op":{"type":"string","enum":["list","get"],"description":"list prints note names and timestamps; get returns one note by name."},"name":{"type":"string","description":"Note slug for get: [a-z0-9-]."}},"required":["op"],"additionalProperties":false}""";

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Read the project's persistent notes: conventions, architecture facts and prior decisions saved with remember. op=list names them; op=get returns one by name. Read-only, so it works in every mode.",
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
        var op = (arguments.GetOptionalString("op") ?? List).Trim().ToLowerInvariant();

        switch (op)
        {
            case List:
                return ListNotes(toolCall, context);
            case Get:
                return await GetNoteAsync(toolCall, arguments.GetOptionalString("name"), context, cancellationToken)
                    .ConfigureAwait(false);
            default:
                return ToolResult.Failure(toolCall.Id, Name, $"op must be '{List}' or '{Get}', not '{op}'.");
        }
    }

    private static ToolResult ListNotes(ToolCall toolCall, ToolInvocationContext context)
    {
        var notes = ProjectMemoryStore.List(context.Workspace);
        var read = new ToolEffect(ToolEffectKind.Read, ".sharpcoreagent/memory");
        if (notes.Count == 0)
        {
            return ToolResult.Success(toolCall.Id, Name, "no notes", read);
        }

        var builder = new StringBuilder();
        builder.Append("project memory · ").Append(notes.Count.ToString(CultureInfo.InvariantCulture)).Append(" note(s)");
        foreach (var note in notes)
        {
            builder.Append('\n')
                .Append(note.Name)
                .Append(" · ")
                .Append(note.ModifiedUtc.ToString("u", CultureInfo.InvariantCulture))
                .Append(" · ")
                .Append(note.Bytes.ToString(CultureInfo.InvariantCulture))
                .Append(" bytes");
        }

        var text = TextLimits.Bound(builder.ToString(), TextLimits.MaxCommandOutputChars, out var truncated);
        return ToolResult.Success(toolCall.Id, Name, text, read) with { IsTruncated = truncated };
    }

    private static async ValueTask<ToolResult> GetNoteAsync(
        ToolCall toolCall,
        string? name,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (!ProjectMemoryStore.TrySlug(name, out var slug, out var error))
        {
            return ToolResult.Failure(toolCall.Id, Name, error!);
        }

        var body = await ProjectMemoryStore.ReadAsync(context.Workspace, slug, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return ToolResult.Failure(toolCall.Id, Name, $"no note named '{slug}'.");
        }

        var text = TextLimits.Bound(body, TextLimits.MaxCommandOutputChars, out var truncated);
        return ToolResult.Success(
            toolCall.Id,
            Name,
            text,
            new ToolEffect(ToolEffectKind.Read, string.Concat(".sharpcoreagent/memory/", slug, ProjectMemoryStore.Extension)))
            with { IsTruncated = truncated };
    }
}
