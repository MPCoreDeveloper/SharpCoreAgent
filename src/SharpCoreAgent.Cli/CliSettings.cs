using SharpCoreAgent.Infrastructure.Configuration;

namespace SharpCoreAgent.Cli;

/// <summary>
/// The settings document this process reads, captured once so every consumer sees the same bytes.
/// </summary>
/// <param name="Path">The file the values came from, absolute; it need not exist.</param>
/// <param name="Values">The values in it, empty when there is no file.</param>
/// <remarks>
/// The record exists so <c>CliOptions.Parse</c> can stay free of file IO: a caller hands it a loader,
/// and tests hand it a literal. The path travels with the values because every error about them has to
/// name the file the developer must edit.
/// </remarks>
internal sealed record CliSettings(string Path, IReadOnlyDictionary<string, string> Values)
{
    /// <summary>
    /// Reads the document at a path.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The settings, empty when the file does not exist.</returns>
    /// <exception cref="SharpCoreAgent.Domain.Errors.ConfigurationFileException">The file cannot be read or parsed.</exception>
    public static CliSettings Load(string path) =>
        new(System.IO.Path.GetFullPath(path), AgentSettingsFile.Read(path));
}
