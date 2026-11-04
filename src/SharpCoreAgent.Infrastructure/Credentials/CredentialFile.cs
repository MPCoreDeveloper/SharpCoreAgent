using System.Text;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Infrastructure.Configuration;

namespace SharpCoreAgent.Infrastructure.Credentials;

/// <summary>
/// Reads the <c>NAME=value</c> file that <c>--credential-file</c> names.
/// </summary>
/// <remarks>
/// The grammar is deliberately small: one assignment per line, <c>#</c> comments, an optional
/// <c>export</c> prefix and optional matching quotes. Nothing is expanded or escaped, so a key is
/// handed to the transport exactly as it was written — a value with a <c>#</c> or a space in it
/// survives, and a typo is never turned into a different key by a shell-like rule nobody remembers.
/// <para>
/// Errors name the file and the line number and never the content: a line that failed to parse may be
/// the key itself. Every value stays in the dictionary this method returns and in the request header
/// it is copied to.
/// </para>
/// </remarks>
public static class CredentialFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Gets the file a pasted key is written to when no credential file is named.
    /// </summary>
    /// <param name="readEnvironment">The environment reader; defaults to the process environment.</param>
    /// <returns>
    /// <c>credentials.env</c> beside the settings file — <c>%APPDATA%\sharpcoreagent</c> on Windows, the
    /// <c>XDG_CONFIG_HOME</c> directory elsewhere.
    /// </returns>
    /// <remarks>
    /// Beside the settings file rather than in it, and never inside a workspace: a key in the tree the
    /// agent edits is a key in a diff, a stash and a screenshot. The directory is the developer's own
    /// profile directory on every platform that has one.
    /// </remarks>
    public static string DefaultPath(Func<string, string?>? readEnvironment = null)
    {
        var settings = AgentSettingsFile.PathFromEnvironment(readEnvironment);
        var directory = Path.GetDirectoryName(settings);
        return Path.Combine(string.IsNullOrEmpty(directory) ? "." : directory, "credentials.env");
    }

    /// <summary>
    /// Writes one key into a credential file, replacing a line that already names it.
    /// </summary>
    /// <param name="path">The file to write; missing directories are created.</param>
    /// <param name="name">The variable name, for example <c>DEEPSEEK_API_KEY</c>.</param>
    /// <param name="value">The key. It is never echoed, logged or repeated in a message.</param>
    /// <exception cref="ArgumentException">The name is not a variable name, or the value holds a line break.</exception>
    /// <exception cref="CredentialFileException">The file exists but cannot be read or written.</exception>
    /// <remarks>
    /// Everything that is not the assignment being replaced is kept as it was, so a developer's rotation
    /// note above the keys survives a paste — that is the difference between a file they maintain and one
    /// a tool owns. A file that cannot be parsed is never rewritten: refusing is the only way to write here
    /// without risking the loss of a key nobody can retype from memory.
    /// <para>
    /// On Unix the file is left readable by its owner alone. On Windows the ACL stays the profile's: the
    /// file lives under <c>%APPDATA%</c>, which is already per-user, and tightening it further needs a
    /// package the constitution does not carry.
    /// </para>
    /// </remarks>
    public static void Upsert(string path, string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!IsName(name.Trim()))
        {
            throw new ArgumentException(
                $"'{name}' is not a variable name: letters, digits and underscores, and not starting with a digit.",
                nameof(name));
        }

        if (value.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            throw new ArgumentException(
                $"the value for '{name.Trim()}' holds a line break, which a NAME=value file cannot carry.",
                nameof(value));
        }

        var full = Path.GetFullPath(path);
        var lines = new List<string>(File.Exists(full) ? ReadLines(full) : []);

        // Parsing first is the guard, not a convenience: a line this grammar rejects is a line this
        // method would silently drop on the way back out.
        _ = Parse(lines, full);

        var assignment = $"{name.Trim()}={value}";
        var replaced = lines.FindIndex(line => string.Equals(NameOf(line), name.Trim(), StringComparison.Ordinal));
        if (replaced >= 0)
        {
            lines[replaced] = assignment;
        }
        else
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add(assignment);
        }

        Save(full, lines);
    }

    /// <summary>Reads a credential file from disk.</summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The name/value pairs the file declares, in order, keyed ordinally.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank.</exception>
    /// <exception cref="CredentialFileException">The file is missing, unreadable or malformed.</exception>
    public static IReadOnlyDictionary<string, string> Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ToDictionary(ReadEntries(path));
    }

    /// <summary>Reads a credential file and keeps the line each assignment came from.</summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The entries, in file order, with their source line numbers.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank.</exception>
    /// <exception cref="CredentialFileException">The file is missing, unreadable or malformed.</exception>
    /// <remarks>
    /// The line number is for diagnostics such as <c>credentials --check</c>. The value stays in the entry so the
    /// caller can use it, but a diagnostic must print only the name, path and line.
    /// </remarks>
    public static IReadOnlyList<CredentialEntry> ReadEntries(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ParseEntries(ReadLines(path), path);
    }

    /// <summary>Parses the lines of a credential file.</summary>
    /// <param name="lines">The file's lines, without their line separators.</param>
    /// <param name="source">The file's path, used in error messages only.</param>
    /// <returns>The name/value pairs the lines declare, keyed ordinally.</returns>
    /// <exception cref="CredentialFileException">A line has no <c>=</c>, no name, or repeats one.</exception>
    public static IReadOnlyDictionary<string, string> Parse(IEnumerable<string> lines, string source) =>
        ToDictionary(ParseEntries(lines, source));

    /// <summary>Parses the lines of a credential file and keeps each assignment's line number.</summary>
    /// <param name="lines">The file's lines, without their line separators.</param>
    /// <param name="source">The file's path, used in error messages only.</param>
    /// <returns>The entries, in file order.</returns>
    /// <exception cref="CredentialFileException">A line has no <c>=</c>, no name, or repeats one.</exception>
    public static IReadOnlyList<CredentialEntry> ParseEntries(IEnumerable<string> lines, string source)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var values = new List<CredentialEntry>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var number = 0;

        foreach (var raw in lines)
        {
            number++;
            var line = raw.Trim();

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
            {
                line = line["export ".Length..].TrimStart();
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                throw new CredentialFileException(
                    $"{source}: line {number} has no '='; every line must be NAME=value.");
            }

            var name = line[..separator].Trim();
            if (!IsName(name))
            {
                throw new CredentialFileException(
                    $"{source}: line {number} does not start with a variable name (letters, digits and underscores).");
            }

            var value = Unquote(line[(separator + 1)..].Trim());
            if (!names.Add(name))
            {
                throw new CredentialFileException($"{source}: line {number} repeats '{name}'.");
            }

            values.Add(new CredentialEntry(name, value, number));
        }

        return values;
    }

    private static Dictionary<string, string> ToDictionary(IReadOnlyList<CredentialEntry> entries)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            values.Add(entry.Name, entry.Value);
        }

        return values;
    }

    /// <summary>Removes one pair of matching single or double quotes, and nothing else.</summary>
    private static string Unquote(string value) =>
        value.Length > 1 && value[0] is '"' or '\'' && value[^1] == value[0] ? value[1..^1] : value;

    /// <summary>Determines whether the text is a variable name: a letter or underscore, then letters, digits or underscores.</summary>
    private static bool IsName(string name) =>
        name.Length > 0
        && !char.IsAsciiDigit(name[0])
        && name.All(static character => char.IsAsciiLetterOrDigit(character) || character == '_');

    /// <summary>
    /// Returns the variable name an existing line declares, or <see langword="null"/> when the line is a
    /// comment, a blank line or nothing this grammar reads as an assignment.
    /// </summary>
    /// <param name="raw">One line of a credential file.</param>
    /// <returns>The name as written, without quotes or the <c>export</c> prefix.</returns>
    private static string? NameOf(string raw)
    {
        var line = raw.Trim();
        if (line.Length == 0 || line[0] == '#')
        {
            return null;
        }

        if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
        {
            line = line["export ".Length..].TrimStart();
        }

        var separator = line.IndexOf('=', StringComparison.Ordinal);
        return separator < 0 ? null : line[..separator].Trim();
    }

    /// <summary>Reads every line, reporting a failure as the exception this file's readers expect.</summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The file's lines, without their separators.</returns>
    /// <exception cref="CredentialFileException">The file is missing, unreadable or not a file.</exception>
    private static string[] ReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            throw new CredentialFileException($"The credential file '{path}' could not be read: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Writes the lines atomically, and leaves the file readable by its owner alone where the platform
    /// has that notion.
    /// </summary>
    /// <param name="path">The destination, absolute.</param>
    /// <param name="lines">The lines to write.</param>
    /// <exception cref="CredentialFileException">The file or its directory could not be written.</exception>
    /// <remarks>
    /// A sibling <c>.tmp</c> file and a replace, for the reason the session store does the same: a crash in
    /// the middle leaves the old key file or the new one, never half of either. A mode the filesystem
    /// refuses is not worth failing a write over — the key is on disk either way, and the caller has
    /// already told the developer where it went.
    /// </remarks>
    private static void Save(string path, IReadOnlyList<string> lines)
    {
        var directory = Path.GetDirectoryName(path);
        var temporary = string.Concat(path, ".tmp");
        try
        {
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllLines(temporary, lines, Utf8NoBom);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {

            throw new CredentialFileException($"The credential file '{path}' could not be written: {ex.Message}", ex);
        }
        finally
        {
            TryDelete(temporary);
        }

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or ArgumentException)
        {
            // A filesystem without modes keeps the umask default; the key is still written.
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
            // A leftover temporary file is not worth failing a successful write over.
        }
    }
}

/// <summary>One assignment read from a credential file.</summary>
/// <param name="Name">The variable name.</param>
/// <param name="Value">The secret value.</param>
/// <param name="Line">The one-based line that declared it.</param>
/// <remarks>
/// A diagnostic may print <see cref="Name"/> and <see cref="Line"/>, never <see cref="Value"/>.
/// </remarks>
public sealed record CredentialEntry(string Name, string Value, int Line)
{
    /// <inheritdoc />
    /// <remarks>Deliberately omits the value: a diagnostic or log line must not carry a secret.</remarks>
    public override string ToString() => $"{Name} (line {Line})";
}
