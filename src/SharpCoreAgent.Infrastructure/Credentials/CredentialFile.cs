using SharpCoreAgent.Domain.Errors;

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
    /// <summary>Reads a credential file from disk.</summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The name/value pairs the file declares, in order, keyed ordinally.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank.</exception>
    /// <exception cref="CredentialFileException">The file is missing, unreadable or malformed.</exception>
    public static IReadOnlyDictionary<string, string> Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            throw new CredentialFileException($"The credential file '{path}' could not be read: {ex.Message}", ex);
        }

        return Parse(lines, path);
    }

    /// <summary>Parses the lines of a credential file.</summary>
    /// <param name="lines">The file's lines, without their line separators.</param>
    /// <param name="source">The file's path, used in error messages only.</param>
    /// <returns>The name/value pairs the lines declare, keyed ordinally.</returns>
    /// <exception cref="CredentialFileException">A line has no <c>=</c>, no name, or repeats one.</exception>
    public static IReadOnlyDictionary<string, string> Parse(IEnumerable<string> lines, string source)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
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
            if (!values.TryAdd(name, value))
            {
                throw new CredentialFileException($"{source}: line {number} repeats '{name}'.");
            }
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
}
