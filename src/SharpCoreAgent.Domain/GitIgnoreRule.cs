using System.Text;
using System.Text.RegularExpressions;

namespace SharpCoreAgent.Domain;

/// <summary>One parsed line of a <c>.gitignore</c> file.</summary>
internal sealed class GitIgnoreRule
{
    private readonly string _base;
    private readonly Regex _pattern;

    private GitIgnoreRule(string @base, Regex pattern, bool negated, bool directoryOnly)
    {
        _base = @base;
        _pattern = pattern;
        Negated = negated;
        DirectoryOnly = directoryOnly;
    }

    public bool Negated { get; }

    public bool DirectoryOnly { get; }

    public static bool TryParse(string line, string baseDirectory, out GitIgnoreRule rule)
    {
        rule = null!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var text = line.TrimEnd();
        if (text.Length == 0 || text[0] == '#')
        {
            return false;
        }

        var negated = text[0] == '!';
        if (negated)
        {
            text = text[1..];
            if (text.Length == 0)
            {
                return false;
            }
        }
        else if (text.StartsWith(@"\#", StringComparison.Ordinal))
        {
            text = text[1..];
        }

        var directoryOnly = text.EndsWith('/');
        if (directoryOnly)
        {
            text = text[..^1];
        }

        if (text.Length == 0)
        {
            return false;
        }

        var anchored = text.StartsWith('/');
        if (anchored)
        {
            text = text[1..];
        }

        var matchNameOnly = !anchored && !text.Contains('/');
        try
        {
            rule = new GitIgnoreRule(
                baseDirectory is "." ? string.Empty : baseDirectory.Replace('\\', '/'),
                Compile(text, matchNameOnly),
                negated,
                directoryOnly);
            return true;
        }
        catch (ArgumentException)
        {
            // A malformed class such as [z-a] is ignored rather than making a read-only scan fail.
            return false;
        }
    }

    public bool Matches(string relative, bool isDirectory)
    {
        if (DirectoryOnly && !isDirectory)
        {
            return false;
        }

        if (_base.Length > 0)
        {
            if (!relative.StartsWith(_base + "/", StringComparison.Ordinal))
            {
                return false;
            }

            relative = relative[(_base.Length + 1)..];
        }

        return _pattern.IsMatch(relative);
    }

    private static Regex Compile(string pattern, bool matchNameOnly)
    {
        var expression = new StringBuilder("^");
        var index = 0;
        while (index < pattern.Length)
        {
            var character = pattern[index];
            switch (character)
            {
                case '*':
                    if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                    {
                        index += 2;
                        if (index < pattern.Length && pattern[index] == '/')
                        {
                            index++;
                            expression.Append("(?:.*/)?");
                        }
                        else
                        {
                            expression.Append(".*");
                        }
                    }
                    else
                    {
                        index++;
                        expression.Append("[^/]*");
                    }

                    break;

                case '?':
                    index++;
                    expression.Append("[^/]");
                    break;

                case '[':
                    var close = pattern.IndexOf(']', index + 1);
                    if (close > index + 1)
                    {
                        var content = pattern[(index + 1)..close];
                        expression.Append('[');
                        if (content.StartsWith('!'))
                        {
                            expression.Append('^').Append(content[1..]);
                        }
                        else
                        {
                            expression.Append(content);
                        }

                        expression.Append(']');
                        index = close + 1;
                    }
                    else
                    {
                        index++;
                        expression.Append(@"\[");
                    }

                    break;

                default:
                    index++;
                    expression.Append(Regex.Escape(character.ToString()));
                    break;
            }
        }

        expression.Append('$');
        var body = expression.ToString();
        return new Regex(
            matchNameOnly ? $"(?:^|/){body[1..]}" : body,
            RegexOptions.Compiled | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }
}
