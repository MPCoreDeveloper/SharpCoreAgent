using System.Globalization;
using System.Text;

namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// The rule that turns a server and a server tool into one name the model can call.
/// </summary>
/// <remarks>
/// A provider accepts tool names made of letters, digits, underscores and dashes, and the transcript is
/// read by a person, so the folded form is lower-case and underscore-separated: <c>postgres_run_query</c>
/// says which server answered at a glance. The prefix is not decoration — three servers can each offer a
/// tool called <c>search</c>, and without it the second one would collide with the first.
/// </remarks>
internal static class McpToolName
{
    /// <summary>Gets the longest name a provider is asked to accept.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// Folds arbitrary text into a tool-name fragment.
    /// </summary>
    /// <param name="value">The text, which may be empty or contain anything.</param>
    /// <returns>Lower-case letters, digits and single underscores, with no leading or trailing underscore.</returns>
    public static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                // A run of punctuation and spaces is one separator: `my::server` and `my server` are the
                // same server, and `get__x` and `get x` are the same tool.
                builder.Append('_');
            }
        }

        return Trim(builder);
    }

    /// <summary>
    /// Builds the prefix a server's tools share.
    /// </summary>
    /// <param name="executableStem">The command's executable name without its extension, such as <c>npx</c>.</param>
    /// <param name="ordinal">The server's position among the servers named, counting from one.</param>
    /// <returns>The folded stem, with <c>_2</c>, <c>_3</c> … when another server already claimed it.</returns>
    public static string ForServer(string executableStem, int ordinal)
    {
        var folded = Fold(executableStem);
        if (folded.Length == 0)
        {
            folded = "server";
        }

        return ordinal <= 1 ? Cap(folded) : Cap(string.Create(CultureInfo.InvariantCulture, $"{folded}_{ordinal}"));
    }

    /// <summary>
    /// Builds the name a model calls.
    /// </summary>
    /// <param name="serverName">The prefix from <see cref="ForServer"/>.</param>
    /// <param name="toolName">The name the server gave the tool.</param>
    /// <returns><c>&lt;server&gt;_&lt;tool&gt;</c>, folded and capped at <see cref="MaxLength"/>.</returns>
    public static string ForTool(string serverName, string? toolName)
    {
        var tool = Fold(toolName);
        if (tool.Length == 0)
        {
            tool = "tool";
        }

        return Cap(string.Create(CultureInfo.InvariantCulture, $"{serverName}_{tool}"));
    }

    /// <summary>
    /// Adds a numeric suffix, keeping the result inside the provider's limit.
    /// </summary>
    /// <param name="name">The name that is already taken.</param>
    /// <param name="ordinal">The suffix to add, counting from two.</param>
    /// <returns><paramref name="name"/> with <c>_2</c>, <c>_3</c> … appended and the whole folded name capped.</returns>
    internal static string WithSuffix(string name, int ordinal)
    {
        // The suffix is what makes the name unique, so the head is what gives way: cutting the end of a
        // max-length name would drop the "_2" and leave the collision the suffix exists to resolve.
        var suffix = string.Create(CultureInfo.InvariantCulture, $"_{ordinal}");
        var head = name.TrimEnd('_');
        var room = MaxLength - suffix.Length;

        if (head.Length > room)
        {
            head = head[..Math.Max(0, room)].TrimEnd('_');
        }

        return string.Concat(head, suffix);
    }

    /// <summary>
    /// Shortens a name to the length a provider accepts.
    /// </summary>
    /// <param name="name">The folded name.</param>
    /// <returns>The name, cut at <see cref="MaxLength"/> with a trailing separator removed.</returns>
    private static string Cap(string name)
    {
        if (name.Length <= MaxLength)
        {
            return name;
        }

        var cut = name[..MaxLength];
        return cut.EndsWith('_') ? cut[..^1] : cut;
    }

    private static string Trim(StringBuilder builder)
    {
        while (builder.Length > 0 && builder[^1] == '_')
        {
            builder.Length--;
        }

        return builder.ToString();
    }
}
