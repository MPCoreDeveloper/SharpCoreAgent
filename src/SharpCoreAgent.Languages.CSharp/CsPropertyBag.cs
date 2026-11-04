using System.Globalization;
using System.Xml.Linq;

namespace SharpCoreAgent.Languages.CSharp;

/// <summary>The small property vocabulary this in-process project reader supports, with explicit expansion.</summary>
internal sealed class CsPropertyBag
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public CsPropertyBag(string projectFile)
    {
        ProjectFile = projectFile;
        ProjectDirectory = Path.GetDirectoryName(projectFile)!;
        _values["MSBuildProjectDirectory"] = ProjectDirectory;
        _values["MSBuildProjectFile"] = Path.GetFileName(projectFile);
        _values["MSBuildProjectName"] = Path.GetFileNameWithoutExtension(projectFile);
        _values["MSBuildProjectExtension"] = Path.GetExtension(projectFile);
    }

    public string ProjectFile { get; }
    public string ProjectDirectory { get; }

    public void Apply(XDocument document, string fileDirectory)
    {
        _values["MSBuildThisFileDirectory"] = fileDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var element in document.Descendants().Where(static element => element.Parent is not null && !element.HasElements))
        {
            if (element.Name.LocalName is "Import" or "Compile" or "ProjectReference" or "PackageReference" or "Reference" or "FrameworkReference")
            {
                continue;
            }

            var text = element.Value.Trim();
            if (text.Length > 0)
            {
                _values[element.Name.LocalName] = Expand(text) ?? text;
            }
        }
    }

    public string? Get(string name) =>
        _values.TryGetValue(name, out var value) ? Expand(value, 0) : null;

    public bool IsTrue(string name) =>
        string.Equals(Get(name), "true", StringComparison.OrdinalIgnoreCase);

    public string? Expand(string value, int depth = 0)
    {
        if (depth > 12)
        {
            return null;
        }

        var result = value;
        for (var guard = 0; guard < 32; guard++)
        {
            var start = result.IndexOf("$(", StringComparison.Ordinal);
            if (start < 0)
            {
                return result;
            }

            var end = result.IndexOf(')', start + 2);
            if (end < 0)
            {
                return null;
            }

            var name = result[(start + 2)..end];
            if (!_values.TryGetValue(name, out var replacement))
            {
                return null;
            }

            replacement = Expand(replacement, depth + 1);
            if (replacement is null)
            {
                return null;
            }

            result = string.Concat(result.AsSpan(0, start), replacement, result.AsSpan(end + 1));
        }

        return null;
    }

    public static string? NearestFile(string projectDirectory, string workspaceRoot, string fileName)
    {
        var root = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar);
        for (var directory = new DirectoryInfo(projectDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (string.Equals(directory.FullName.TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return null;
    }

    public static string Display(string projectFile) =>
        string.Create(CultureInfo.InvariantCulture, $"'{Path.GetFileName(projectFile)}'");
}
