using System.Globalization;
using System.Text;
using System.Text.Json;
using SharpCoreAgent.Domain.Errors;

namespace SharpCoreAgent.Infrastructure.Configuration;

/// <summary>
/// The small JSON document a developer's preferences live in, and the rules for finding it.
/// </summary>
/// <remarks>
/// A document reader, not a settings model: <see cref="Read(string)"/> returns name/value pairs and
/// knows nothing about providers, approval modes or budgets. That split is what lets the CLI repair a
/// file whose <em>values</em> it cannot apply — the JSON is still readable, only one line is wrong.
/// <para>
/// A missing file is not an error. It is the empty document, which is exactly what "no file" means for
/// every setting in it. Everything else — unreadable, not JSON, a directory where a file belongs —
/// becomes a <see cref="ConfigurationFileException"/> naming the path.
/// </para>
/// </remarks>
public static class AgentSettingsFile
{
    /// <summary>Gets the variable that names a settings file for the whole process.</summary>
    public const string EnvironmentVariable = "SHARPCOREAGENT_CONFIG";

    private const string DirectoryName = "sharpcoreagent";

    private const string FileName = "config.json";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Resolves the path this process uses, from the environment and the platform's configuration
    /// directory.
    /// </summary>
    /// <param name="readEnvironment">The environment reader; defaults to the process environment.</param>
    /// <returns>An absolute path. The file need not exist.</returns>
    public static string PathFromEnvironment(Func<string, string?>? readEnvironment = null)
    {
        var read = readEnvironment ?? Environment.GetEnvironmentVariable;
        return ResolvePath(
            read(EnvironmentVariable),
            read("XDG_CONFIG_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            OperatingSystem.IsWindows());
    }

    /// <summary>
    /// Resolves the path from explicit pieces, so the rule can be tested on either platform.
    /// </summary>
    /// <param name="configuredPath">The value of <see cref="EnvironmentVariable"/>, if any.</param>
    /// <param name="configHome">The value of <c>XDG_CONFIG_HOME</c>, if any.</param>
    /// <param name="appData">The roaming application-data directory, if the platform has one.</param>
    /// <param name="userProfile">The home directory, if the platform knows it.</param>
    /// <param name="isWindows">Whether the platform is Windows.</param>
    /// <returns>An absolute path. The file need not exist.</returns>
    /// <remarks>
    /// A named file wins outright: the variable exists so a project, a test or a wrapper can pin the
    /// document without touching the user's own. Windows keeps its preferences in <c>%APPDATA%</c>,
    /// everywhere else honours <c>XDG_CONFIG_HOME</c> and falls back to <c>~/.config</c>.
    /// </remarks>
    public static string ResolvePath(
        string? configuredPath,
        string? configHome,
        string? appData,
        string? userProfile,
        bool isWindows)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath.Trim());
        }

        if (isWindows && !string.IsNullOrWhiteSpace(appData))
        {
            return Path.GetFullPath(Path.Combine(appData, DirectoryName, FileName));
        }

        if (!isWindows && !string.IsNullOrWhiteSpace(configHome))
        {
            return Path.GetFullPath(Path.Combine(configHome.Trim(), DirectoryName, FileName));
        }

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            return Path.GetFullPath(Path.Combine(userProfile, ".config", DirectoryName, FileName));
        }

        // No home directory at all: a service account or a sandbox. The current directory is the only
        // place left that is guaranteed writable, and `config path` will say so.
        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".config", DirectoryName, FileName));
    }

    /// <summary>
    /// Reads the document.
    /// </summary>
    /// <param name="path">The file to read; it may be missing.</param>
    /// <returns>The settings in it, empty when there is no file.</returns>
    /// <exception cref="ConfigurationFileException">The file exists but cannot be read or parsed.</exception>
    public static IReadOnlyDictionary<string, string> Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);
        if (Directory.Exists(full))
        {
            throw new ConfigurationFileException($"the settings path '{full}' is a directory, not a file.");
        }

        if (!File.Exists(full))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        string text;
        try
        {
            text = File.ReadAllText(full, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigurationFileException(
                $"the settings file '{full}' could not be read: {ex.Message}",
                ex);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var document = JsonSerializer.Deserialize(
                text,
                AgentSettingsJsonContext.Default.SortedDictionaryStringString);
            return document is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(document, StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            // The line number is the whole value of this message: a settings file is edited by hand.
            var line = ex.LineNumber is { } number
                ? string.Create(CultureInfo.InvariantCulture, $" (line {number + 1})")
                : string.Empty;
            throw new ConfigurationFileException(
                $"the settings file '{full}' is not a JSON object of string values{line}: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Writes the document.
    /// </summary>
    /// <param name="path">The destination; missing directories are created.</param>
    /// <param name="values">The settings to write, in any order; they are sorted on the way out.</param>
    /// <exception cref="ConfigurationFileException">The file or its directory could not be written.</exception>
    /// <remarks>
    /// The write is atomic — a sibling <c>.tmp</c> file, then a replace — for the same reason the
    /// session store's is: a crash in the middle leaves the old document or the new one, never half of
    /// either. Sorted keys and a fixed indent make two saves of the same settings identical, so a
    /// version-control diff of this file says only what changed.
    /// </remarks>
    public static void Save(string path, IReadOnlyDictionary<string, string> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(values);

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            sorted[name] = value;
        }

        var json = JsonSerializer.Serialize(sorted, AgentSettingsJsonContext.Default.SortedDictionaryStringString)
            + "\n";
        var temporary = string.Concat(full, ".tmp");

        try
        {
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(temporary, json, Utf8NoBom);
            File.Move(temporary, full, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigurationFileException(
                $"the settings file '{full}' could not be written: {ex.Message}",
                ex);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is not worth failing a successful save over.
        }
    }
}
