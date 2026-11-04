using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Application;

/// <summary>
/// The rule that a turn editing a profiled language's source cannot end on the model's word alone.
/// </summary>
/// <remarks>
/// The gate is a pure reading of what the turn already recorded — the tool results, their effects and the order they
/// arrived in — so it adds no port, no state and no reference from this layer to the one that runs a compiler. A verifier
/// tool declares the profiles it answers for through <see cref="IVerificationTool"/>; the .NET tool is the built-in
/// profile, and another tool can add Python, JavaScript or a host's own language without teaching this file their names.
/// <para>
/// "A passing verification" means a successful result from the tool that owns the profile, recorded after the last edit
/// whose path belongs to it. Order rather than presence is what stops a verify that ran before the edit from closing the
/// gate, which is the one mistake that would make the whole rule decorative.
/// </para>
/// </remarks>
internal static class VerifyGate
{
    /// <summary>Gets what the report says about a .NET turn the gate could not close.</summary>
    public const string UnverifiedDetail =
        "the turn edited .NET source but dotnet_verify did not pass after that edit, so the change is unverified.";

    /// <summary>Binds every registered verifier tool to the profiles it declares.</summary>
    /// <param name="tools">The turn's registry.</param>
    /// <returns>The bindings, in registry order; a tool with no profiles contributes nothing.</returns>
    public static IReadOnlyList<VerificationBinding> Bind(IToolRegistry tools) =>
        [.. tools.Tools
            .Select(tool => (Tool: tool, Verifier: tool as IVerificationTool))
            .Where(pair => pair.Verifier is not null)
            .SelectMany(pair => pair.Verifier!.Profiles.Select(
                profile => new VerificationBinding(pair.Tool.Definition.Name, profile)))];

    /// <summary>Determines whether one effect changes a path a profile covers.</summary>
    /// <param name="profile">The profile that might own the path.</param>
    /// <param name="effect">One reported effect.</param>
    /// <returns><see langword="true"/> when the effect is a write to a path in this profile.</returns>
    public static bool Edits(VerificationProfile profile, ToolEffect effect) =>
        effect.Kind is ToolEffectKind.Created or ToolEffectKind.Modified or ToolEffectKind.Deleted
        && profile.Matches(effect.Target);

    /// <summary>
    /// Determines whether the turn has to be verified before it may end.
    /// </summary>
    /// <param name="session">The conversation, for its mode.</param>
    /// <param name="gaps">The profiles whose last edit has no later passing verification.</param>
    /// <returns><see langword="true"/> when the loop must not accept the model's answer yet.</returns>
    /// <remarks>
    /// No gap means no profile-owned file was edited, or every edit has a later verifier result. A registry without a
    /// verifier tool produces no bindings and therefore no gap, exactly as a host without <c>dotnet_verify</c> was never
    /// gated before the profile seam existed.
    /// </remarks>
    public static bool IsRequired(AgentSession session, IReadOnlyList<VerificationGap> gaps) =>
        session.Mode == AgentMode.Act && gaps.Count > 0;

    /// <summary>Builds the explanation for a turn that ended with unverified profiles.</summary>
    /// <param name="gaps">The profiles whose last edit was not verified.</param>
    /// <returns>The report's detail line.</returns>
    public static string Detail(IReadOnlyList<VerificationGap> gaps)
    {
        if (gaps.Count == 1
            && string.Equals(gaps[0].Profile.Name, "dotnet", StringComparison.Ordinal)
            && string.Equals(gaps[0].ToolName, ToolNames.DotnetVerify, StringComparison.Ordinal))
        {
            return UnverifiedDetail;
        }

        var languages = string.Join(
            " and ",
            gaps.Select(gap => gap.Profile.DisplayName).Distinct(StringComparer.Ordinal));
        var tools = string.Join(
            " or ",
            gaps.Select(gap => gap.ToolName).Distinct(StringComparer.Ordinal));
        return $"the turn edited {languages} source but {tools} did not pass after that edit, so the change is unverified.";
    }
}

/// <summary>One verifier tool and one profile it declares.</summary>
/// <param name="ToolName">The tool that must pass.</param>
/// <param name="Profile">The language profile the tool verifies.</param>
internal sealed record VerificationBinding(string ToolName, VerificationProfile Profile);
