using SharpCoreAgent.Domain;
using AgentWorkspace = SharpCoreAgent.Domain.Workspace;

namespace SharpCoreAgent.Languages.CSharp;

/// <summary>Finds C# project files inside the primary workspace without following build output or links.</summary>
internal static class CsProjectProbe
{
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".sharpcoreagent", "bin", "obj", "node_modules",
    };

    public static IReadOnlyList<string> Find(AgentWorkspace workspace)
    {
        var projects = new List<string>();
        var pending = new Stack<string>();
        pending.Push(workspace.Root);
        try
        {
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                foreach (var child in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
                {
                    var info = new DirectoryInfo(child);
                    if (info.LinkTarget is null && !Ignored.Contains(info.Name))
                    {
                        pending.Push(child);
                    }
                }

                projects.AddRange(Directory.EnumerateFiles(directory, "*.csproj").Order(StringComparer.Ordinal));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The projects found before an unreadable directory are still useful; the loader reports unreadable projects.
        }

        return projects;
    }

    public static IReadOnlyList<string> Nearest(string sourcePath, IEnumerable<string> projects)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return [.. projects
            .Select(project => (Project: project, Directory: Path.GetDirectoryName(project)!))
            .Where(candidate => sourcePath.StartsWith(candidate.Directory + Path.DirectorySeparatorChar, comparison))
            .OrderByDescending(candidate => candidate.Directory.Length)
            .ThenBy(candidate => candidate.Project, StringComparer.Ordinal)
            .Select(candidate => candidate.Project)];
    }
}
