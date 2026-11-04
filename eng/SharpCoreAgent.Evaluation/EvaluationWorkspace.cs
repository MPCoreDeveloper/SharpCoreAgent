using System.Security.Cryptography;

namespace SharpCoreAgent.Evaluation;

/// <summary>Owns one disposable fixture and compares its bytes rather than trusting reported tool effects.</summary>
internal sealed class EvaluationWorkspace : IDisposable
{
    private static readonly HashSet<string> GeneratedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".sharpcoreagent", "bin", "obj", "node_modules", "__pycache__", ".pytest_cache",
    };

    private EvaluationWorkspace(string root) => Root = root;

    public string Root { get; }

    public static EvaluationWorkspace Create(EvaluationScenario scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "sharpcoreagent-evals", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CopyTree(scenario.WorkspacePath, root);
            return new EvaluationWorkspace(root);
        }
        catch
        {
            TryDelete(root);
            throw;
        }
    }

    public IReadOnlyDictionary<string, string> Snapshot() => Snapshot(Root);

    public IReadOnlyList<WorkspaceChange> ChangesSince(IReadOnlyDictionary<string, string> before)
    {
        var after = Snapshot();
        var paths = before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var changes = new List<WorkspaceChange>();
        foreach (var path in paths)
        {
            var existed = before.TryGetValue(path, out var oldHash);
            var exists = after.TryGetValue(path, out var newHash);
            if (!existed && exists)
            {
                changes.Add(new WorkspaceChange("created", path));
            }
            else if (existed && !exists)
            {
                changes.Add(new WorkspaceChange("deleted", path));
            }
            else if (oldHash != newHash)
            {
                changes.Add(new WorkspaceChange("modified", path));
            }
        }

        return changes;
    }

    public void MaterialiseGrading(EvaluationScenario scenario)
    {
        if (Directory.Exists(scenario.GradingPath))
        {
            CopyTree(scenario.GradingPath, Path.Combine(Root, "grading"));
        }
    }

    public void Dispose() => TryDelete(Root);

    private static Dictionary<string, string> Snapshot(string root)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Files(root))
        {
            using var stream = File.OpenRead(file);
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            hashes.Add(relative, Convert.ToHexString(SHA256.HashData(stream)));
        }

        return hashes;
    }

    private static IEnumerable<string> Files(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
            {
                var info = new DirectoryInfo(child);
                if (GeneratedDirectories.Contains(info.Name) || info.LinkTarget is not null)
                {
                    continue;
                }

                pending.Push(child);
            }

            foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal)
                .Where(file => File.ResolveLinkTarget(file, returnFinalTarget: false) is null))
            {
                yield return file;
            }
        }
    }

    private static void CopyTree(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (Path.GetRelativePath(destination, target).StartsWith("..", StringComparison.Ordinal))
            {
                throw new EvaluationException($"fixture path '{relative}' escapes its destination");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A fixture left by a locked child is diagnostic residue, not a reason to hide the task result.
        }
    }
}
