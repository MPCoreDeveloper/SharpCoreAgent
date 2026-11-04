using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Application;

/// <summary>
/// One language profile a verifier tool answers for: a display name and the source extensions it covers.
/// </summary>
/// <param name="Name">The stable profile identifier, used to keep the last edit and last verification in order.</param>
/// <param name="DisplayName">The name a developer reads, such as <c>.NET</c> or <c>Python</c>.</param>
/// <param name="Extensions">
/// The extensions whose creation, modification or deletion this profile covers. A leading dot is required, and matching
/// is case-insensitive.
/// </param>
/// <remarks>
/// A verifier tool owns its profiles. The gate never guesses that a path belongs to a language: a tool that can answer
/// for Python declares the Python extensions, and the same machinery covers C#, F#, a future adapter or a host's own
/// tool. The profile identifier must be unique across the registered verifier tools.
/// </remarks>
public sealed record VerificationProfile(
    string Name,
    string DisplayName,
    IReadOnlyList<string> Extensions)
{
    /// <summary>Determines whether a workspace-relative path belongs to this profile.</summary>
    /// <param name="path">The path an effect named.</param>
    /// <returns><see langword="true"/> when the path ends with one of the profile's extensions.</returns>
    public bool Matches(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Extensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// A tool that can verify a language's source and declares which files it answers for.
/// </summary>
/// <remarks>
/// The tool's own <see cref="ITool.Definition"/> name is the verification tool named in the turn's result and in the
/// nudge. Registering a tool with profiles is the whole opt-in: a host with no such tool is never gated, exactly as a
/// host without <c>dotnet_verify</c> was never gated before this seam existed.
/// </remarks>
public interface IVerificationTool
{
    /// <summary>Gets the language profiles this tool verifies.</summary>
    IReadOnlyList<VerificationProfile> Profiles { get; }
}

/// <summary>One profile whose last edit has no passing verification after it.</summary>
/// <param name="Profile">The language profile that was edited.</param>
/// <param name="ToolName">The tool that must pass for that profile.</param>
public sealed record VerificationGap(VerificationProfile Profile, string ToolName);
