using System.Text.RegularExpressions;

namespace SharpCoreAgent.Languages.CSharp;

/// <summary>Expands the simple path globs a project uses, without walking outside the project directory.</summary>
internal static partial class CsGlob
{
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".sharpcoreagent", "bin", "obj", "node_modules",
    };

    public static IReadOnlyList<string> Expand(string projectDirectory, string pattern)
    {
        var normalized = Normalize(pattern);
        var regex = Compile(normalized);
        return [.. Enumerate(projectDirectory).Where(file =>
            regex.IsMatch(Path.GetRelativePath(projectDirectory, file).Replace('\\', '/')))];
    }

    public static bool Matches(string projectDirectory, string file, string pattern)
    {
        var normalized = Normalize(pattern);
        var relative = Path.GetRelativePath(projectDirectory, file).Replace('\\', '/');
        if (!normalized.Contains('*', StringComparison.Ordinal) && !normalized.Contains('?', StringComparison.Ordinal))
        {
            return string.Equals(relative, normalized, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        return Compile(normalized).IsMatch(relative);
    }

    private static string Normalize(string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }

    private static Regex Compile(string normalized) =>
        new(
            "^" + Regex.Escape(normalized)
                .Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal)
                .Replace(@"\*\*", ".*", StringComparison.Ordinal)
                .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
                .Replace(@"\?", "[^/]", StringComparison.Ordinal) + "$",
            OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant : RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));

    public static IEnumerable<string> Enumerate(string projectDirectory)
    {
        var pending = new Stack<string>();
        pending.Push(projectDirectory);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal)
                .Where(child =>
                {
                    var info = new DirectoryInfo(child);
                    return info.LinkTarget is null && !Ignored.Contains(info.Name);
                }))
            {
                pending.Push(child);
            }

            foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal)
                .Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    && File.ResolveLinkTarget(file, returnFinalTarget: false) is null))
            {
                yield return file;
            }
        }
    }
}
