using System.Text.Json.Serialization;

namespace SharpCoreAgent.Infrastructure.Sessions;

/// <summary>
/// The document a session file contains, exactly as <c>contracts/session-format.md</c> describes it.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="Domain.Sessions.SessionSnapshot"/> because the file has a version, a
/// fixed property order and string-typed enumerations: all three are storage concerns that would leak
/// into the domain if the snapshot were serialised directly.
/// </remarks>
internal sealed record SessionDocument
{
    public required int Version { get; init; }

    public required string WorkspaceRoot { get; init; }

    public required string Provider { get; init; }

    public required string ModelId { get; init; }

    public required string Approval { get; init; }

    public required string CreatedAtUtc { get; init; }

    public required string UpdatedAtUtc { get; init; }

    public List<SessionMessageDocument> Messages { get; init; } = [];

    public List<SessionUsageDocument> Usage { get; init; } = [];
}

/// <summary>
/// One transcript entry in a session file.
/// </summary>
internal sealed record SessionMessageDocument
{
    public required string Role { get; init; }

    public string? Content { get; init; }

    /// <summary>Gets the requested calls; omitted entirely for a message that requested none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SessionToolCallDocument>? ToolCalls { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolName { get; init; }

    /// <summary>Gets the failure flag; written for tool messages only, where it means something.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsError { get; init; }
}

/// <summary>
/// One tool call as the provider asked for it.
/// </summary>
internal sealed record SessionToolCallDocument
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Gets the raw provider arguments, kept verbatim so a replay shows the original request.</summary>
    public required string ArgumentsJson { get; init; }
}

/// <summary>
/// Token usage for one model. Null counts mean <em>unknown</em> and are written as <c>null</c>, never as
/// <c>0</c>, because a provider that omits usage must not be recorded as free.
/// </summary>
internal sealed record SessionUsageDocument
{
    public required string ModelId { get; init; }

    public required int? InputTokens { get; init; }

    public required int? OutputTokens { get; init; }

    public required int Turns { get; init; }
}

/// <summary>
/// Source-generated metadata for the session document.
/// </summary>
/// <remarks>
/// Reflection serialisation is avoided for the same reason as on the wire: trimming and AOT would drop
/// it, and the document shape is small and fixed.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    NewLine = "\n",
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(SessionDocument))]
internal sealed partial class SessionJsonContext : JsonSerializerContext
{
}
