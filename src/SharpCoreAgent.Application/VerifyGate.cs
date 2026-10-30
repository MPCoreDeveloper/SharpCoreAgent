using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Application;

/// <summary>
/// The rule that a turn editing .NET source cannot end on the model's word alone.
/// </summary>
/// <remarks>
/// The gate is a pure reading of what the turn already recorded — the tool results, their effects and the order
/// they arrived in — so it adds no port, no state and no reference from this layer to the one that runs the
/// compiler. <c>SharpCoreAgent.Domain.Tools.ToolNames</c> carries the single string both sides agree on.
/// <para>
/// "A passing verification" means a <c>dotnet_verify</c> result that is neither an error nor a refusal, recorded
/// after the last .NET edit. Order rather than presence is what stops a verify that ran before the edit from
/// closing the gate, which is the one mistake that would make the whole rule decorative.
/// </para>
/// </remarks>
internal static class VerifyGate
{
    /// <summary>Gets what the report says about a turn the gate could not close.</summary>
    public const string UnverifiedDetail =
        "the turn edited .NET source but dotnet_verify did not pass after that edit, so the change is unverified.";

    /// <summary>Gets the extensions whose creation, modification or deletion can break a .NET build.</summary>
    /// <remarks>
    /// A constant rather than a language lookup: deciding whether a file is part of a .NET build is the build's
    /// job, and the compiler is the one that answers it. A file that turns out not to be compiled costs a nudge
    /// and a build that passes in a second, which is cheaper than a rule that guesses.
    /// </remarks>
    private static readonly string[] DotnetExtensions =
        [".cs", ".vb", ".fs", ".csproj", ".vbproj", ".fsproj", ".props", ".targets", ".sln", ".slnx"];

    /// <summary>
    /// Determines whether a result is a passing verification.
    /// </summary>
    /// <param name="result">One tool result from the turn.</param>
    /// <returns><see langword="true"/> when the verifier ran and passed.</returns>
    public static bool Verifies(ToolResult result) =>
        string.Equals(result.ToolName, ToolNames.DotnetVerify, StringComparison.Ordinal)
        && !result.IsError
        && !result.IsRefused;

    /// <summary>
    /// Determines whether a result changed .NET source or a .NET build file.
    /// </summary>
    /// <param name="result">One tool result from the turn.</param>
    /// <returns><see langword="true"/> when an effect names a .NET path that was written to.</returns>
    public static bool EditsDotnetSource(ToolResult result) =>
        result.Effects.Any(EditsDotnetSource);

    /// <summary>
    /// Determines whether one effect changed .NET source or a .NET build file.
    /// </summary>
    /// <param name="effect">One reported effect.</param>
    /// <returns><see langword="true"/> when the effect is a write to a path a .NET build reads.</returns>
    public static bool EditsDotnetSource(ToolEffect effect) =>
        effect.Kind is ToolEffectKind.Created or ToolEffectKind.Modified or ToolEffectKind.Deleted
        && DotnetExtensions.Any(extension => effect.Target.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Determines whether the turn has to be verified before it may end.
    /// </summary>
    /// <param name="session">The conversation, for its mode.</param>
    /// <param name="tools">The registry, so a host without the verifier is never gated.</param>
    /// <param name="lastEdit">The index of the last .NET edit among the turn's results, or -1.</param>
    /// <param name="lastVerification">The index of the last passing verify among the turn's results, or -1.</param>
    /// <returns><see langword="true"/> when the loop must not accept the model's answer yet.</returns>
    public static bool IsRequired(AgentSession session, IToolRegistry tools, int lastEdit, int lastVerification) =>
        session.Mode == AgentMode.Act
        && lastEdit >= 0
        && lastVerification < lastEdit
        && tools.Find(ToolNames.DotnetVerify) is not null;
}
