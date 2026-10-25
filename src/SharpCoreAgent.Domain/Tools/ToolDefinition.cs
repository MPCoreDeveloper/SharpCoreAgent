namespace SharpCoreAgent.Domain.Tools;

/// <summary>
/// How much damage a tool can do if the model calls it with hostile arguments.
/// </summary>
/// <remarks>
/// This is the only input to the approval decision that the developer does not have to read. A
/// read-only tool needs no prompt in any mode; anything else is gated by <see cref="Agents.ApprovalMode"/>.
/// </remarks>
public enum ToolSafety
{
    /// <summary>Reads the workspace and returns text. No mutation, no external process.</summary>
    ReadOnly,

    /// <summary>Creates, modifies or deletes workspace content, or starts a process.</summary>
    Destructive,
}

/// <summary>
/// The declaration of one tool as a model sees it.
/// </summary>
/// <param name="Name">The stable tool name used in prompts, transcripts and logs.</param>
/// <param name="Description">What the tool does and when to use it.</param>
/// <param name="ParametersSchemaJson">A JSON Schema object describing the arguments.</param>
/// <remarks>
/// The schema is carried as text because the agent never validates against it itself: the tool
/// validates its own arguments and returns a readable error. The schema exists for the provider,
/// which uses it to constrain generation.
/// </remarks>
public sealed record ToolDefinition(string Name, string Description, string ParametersSchemaJson)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}
