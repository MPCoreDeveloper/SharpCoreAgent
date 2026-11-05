namespace SharpCoreAgent.Infrastructure.Web;

/// <summary>
/// The rules one <c>robots.txt</c> states, reduced to the one question the fetch tool asks.
/// </summary>
/// <remarks>
/// Deliberately small and dependency-free: user-agent groups, <c>Allow</c>, <c>Disallow</c>, <c>*</c> prefixes and
/// longest-match wins. A blank <c>Disallow</c> allows everything; a file with no matching group allows everything.
/// </remarks>
internal sealed class RobotsFile
{
    private readonly IReadOnlyList<Rule> _rules;

    private RobotsFile(IReadOnlyList<Rule> rules) => _rules = rules;

    /// <summary>Gets a file that allows every path.</summary>
    public static RobotsFile Empty { get; } = new([]);

    /// <summary>Parses the text of one robots file.</summary>
    /// <param name="text">The file, as served.</param>
    /// <param name="userAgent">The product token the client sends.</param>
    /// <returns>The rules that apply to that token.</returns>
    public static RobotsFile Parse(string text, string userAgent)
    {
        ArgumentNullException.ThrowIfNull(text);

        var product = Product(userAgent);
        var groups = new List<(List<string> Agents, List<Rule> Rules)>();
        List<string>? agents = null;
        List<Rule>? rules = null;

        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            switch (key.ToLowerInvariant())
            {
                case "user-agent":
                    if (agents is null || rules is { Count: > 0 })
                    {
                        agents = [];
                        rules = [];
                        groups.Add((agents, rules));
                    }

                    agents.Add(value);
                    break;
                case "allow":
                case "disallow":
                    if (agents is null || rules is null || value.Length == 0)
                    {
                        break;
                    }

                    rules.Add(new Rule(value, key.Equals("allow", StringComparison.OrdinalIgnoreCase)));
                    break;
            }
        }

        var applicable = groups
            .Where(group => group.Agents.Any(agent =>
                agent == "*" || string.Equals(Product(agent), product, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(group => group.Rules)
            .ToArray();

        return applicable.Length == 0 ? Empty : new RobotsFile(applicable);
    }

    /// <summary>Answers whether one path may be fetched.</summary>
    /// <param name="path">The absolute path and query.</param>
    /// <returns><see langword="false"/> when a Disallow rule wins.</returns>
    public bool Allows(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var matching = _rules.Where(rule => rule.Pattern.Length == 0 || path.StartsWith(rule.Pattern, StringComparison.Ordinal)).ToArray();
        if (matching.Length == 0)
        {
            return true;
        }

        var longest = matching.Max(rule => rule.Pattern.Length);
        return matching.Where(rule => rule.Pattern.Length == longest).Any(rule => rule.Allow);
    }

    private static string Product(string value)
    {
        var token = value.Trim();
        var slash = token.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? token[..slash] : token;
    }

    private sealed record Rule(string Pattern, bool Allow);
}
