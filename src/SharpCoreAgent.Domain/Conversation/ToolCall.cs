namespace SharpCoreAgent.Domain.Conversation;

/// <summary>
/// One tool invocation a model asked for.
/// </summary>
/// <param name="Id">The provider-assigned identifier the matching tool result must echo.</param>
/// <param name="Name">The tool name exactly as the model wrote it.</param>
/// <param name="ArgumentsJson">The raw JSON object the model produced, unvalidated.</param>
/// <remarks>
/// Arguments stay a string in the domain. Parsing and validation belong to the tool that owns the
/// schema, so a malformed call becomes a tool error the model can read and correct instead of an
/// exception that ends the turn.
/// </remarks>
public sealed record ToolCall(string Id, string Name, string ArgumentsJson)
{
    /// <inheritdoc />
    public override string ToString() => ArgumentsJson.Length <= 240
        ? $"{Name}({ArgumentsJson})"
        : $"{Name}({ArgumentsJson[..240]}…)";
}
