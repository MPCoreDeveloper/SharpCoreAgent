using System.Text.Json;

namespace SharpCoreAgent.Languages.CSharp;

/// <summary>Reads the compile-time assemblies a restore recorded for packages.</summary>
internal static class CsProjectAssets
{
    public static (IReadOnlyList<string> References, string? Failure) Read(string projectDirectory, string targetFramework)
    {
        var path = Path.Combine(projectDirectory, "obj", "project.assets.json");
        if (!File.Exists(path))
        {
            return ([], "The project has package references but obj/project.assets.json is missing; it has not been restored.");
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (!root.TryGetProperty("packageFolders", out var folders) || folders.ValueKind != JsonValueKind.Object)
            {
                return ([], "project.assets.json names no package folder.");
            }

            var packageFolder = folders.EnumerateObject().Select(entry => entry.Name).FirstOrDefault();
            if (packageFolder is null || !root.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object)
            {
                return ([], "project.assets.json has no package folder or target.");
            }

            var target = targets.EnumerateObject()
                .Where(entry => entry.Name == targetFramework || entry.Name.StartsWith(targetFramework + "/", StringComparison.Ordinal))
                .OrderBy(entry => entry.Name, StringComparer.Ordinal)
                .Select(entry => (JsonElement?)entry.Value)
                .FirstOrDefault();
            if (target is null)
            {
                return ([], $"project.assets.json has no target for '{targetFramework}'.");
            }

            if (!root.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Object)
            {
                return ([], "project.assets.json has no libraries section.");
            }

            var references = new List<string>();
            foreach (var library in target.Value.EnumerateObject())
            {
                if (library.Value.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && string.Equals(type.GetString(), "project", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!library.Value.TryGetProperty("compile", out var compile))
                {
                    continue;
                }

                if (!libraries.TryGetProperty(library.Name, out var descriptor)
                    || !descriptor.TryGetProperty("path", out var relativeLibrary)
                    || relativeLibrary.ValueKind != JsonValueKind.String
                    || relativeLibrary.GetString() is not { } libraryPath)
                {
                    continue;
                }

                foreach (var asset in CompileAssets(compile))
                {
                    if (asset.EndsWith("_._", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var reference = Path.GetFullPath(Path.Combine(
                        packageFolder,
                        libraryPath.Replace('/', Path.DirectorySeparatorChar),
                        asset.Replace('/', Path.DirectorySeparatorChar)));
                    if (!File.Exists(reference))
                    {
                        return ([], $"Package '{library.Name}' is restored but '{asset}' is missing from disk.");
                    }

                    references.Add(reference);
                }
            }

            return (references, null);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return ([], "project.assets.json could not be read: " + ex.Message);
        }
    }

    public static DateTime Stamp(string projectDirectory)
    {
        var path = Path.Combine(projectDirectory, "obj", "project.assets.json");
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
    }

    private static IEnumerable<string> CompileAssets(JsonElement compile) =>
        compile.ValueKind switch
        {
            JsonValueKind.Array => compile.EnumerateArray().Select(static item => item.GetString()).OfType<string>(),
            JsonValueKind.Object => compile.EnumerateObject().Select(static item => item.Name),
            _ => [],
        };
}
