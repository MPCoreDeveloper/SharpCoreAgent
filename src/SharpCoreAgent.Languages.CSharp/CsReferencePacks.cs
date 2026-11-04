using System.Globalization;
using System.Runtime.InteropServices;

namespace SharpCoreAgent.Languages.CSharp;

/// <summary>Locates installed targeting packs and returns their reference assemblies.</summary>
internal static class CsReferencePacks
{
    private static readonly Lazy<string?> DotnetRoot = new(DiscoverRoot);

    public static IReadOnlyList<string> For(string targetFramework, IReadOnlySet<string> frameworkReferences, out string? failure)
    {
        failure = null;
        if (!TryMajor(targetFramework, out var major))
        {
            failure = $"Target framework '{targetFramework}' has no installed reference pack this reader can use.";
            return [];
        }

        var packs = new List<string> { "Microsoft.NETCore.App" };
        packs.AddRange(frameworkReferences);
        var references = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packDirectories = packs.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(pack => (Pack: pack, Directory: FindPack(pack, major)))
            .ToArray();
        var missing = packDirectories.FirstOrDefault(pack => pack.Directory is null);
        if (missing.Pack is not null)
        {
            failure = string.Create(
                CultureInfo.InvariantCulture,
                $"Target '{targetFramework}' needs the {missing.Pack} reference pack for major {major} under '{DotnetRoot.Value ?? "<dotnet root not found>"}', which is not installed.");
            return [];
        }

        foreach (var (_, directory) in packDirectories)
        {
            references.AddRange(Directory.EnumerateFiles(directory!, "*.dll")
                .Order(StringComparer.Ordinal)
                .Where(assembly => names.Add(Path.GetFileName(assembly))));
        }

        return references;
    }

    private static string? FindPack(string packName, int major)
    {
        var root = DotnetRoot.Value;
        if (root is null)
        {
            return null;
        }

        var packRoot = Path.Combine(root, "packs", packName + ".Ref");
        if (!Directory.Exists(packRoot))
        {
            return null;
        }

        var versions = Directory.EnumerateDirectories(packRoot)
            .Select(path => (Path: path, Version: Parse(Path.GetFileName(path))))
            .Where(candidate => candidate.Version?.Major == major)
            .OrderByDescending(candidate => candidate.Version)
            .ThenByDescending(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase);
        return versions
            .Select(candidate => Path.Combine(candidate.Path, "ref", string.Create(CultureInfo.InvariantCulture, $"net{major}.0")))
            .FirstOrDefault(Directory.Exists);
    }

    private static Version? Parse(string name)
    {
        var core = name.Split('-')[0];
        return Version.TryParse(core, out var version) ? version : null;
    }

    private static bool TryMajor(string targetFramework, out int major)
    {
        major = 0;
        var normalized = targetFramework.Split('-')[0];
        var digits = new string([.. normalized.Skip(3).TakeWhile(char.IsAsciiDigit)]);
        if (!normalized.StartsWith("net", StringComparison.OrdinalIgnoreCase)
            || digits.Length == 0
            || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out major))
        {
            return false;
        }

        return major >= 5;
    }

    private static string? DiscoverRoot()
    {
        var trusted = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { } host ? Path.GetDirectoryName(host) : null,
            RuntimeEnvironment.GetRuntimeDirectory(),
            Path.GetDirectoryName(typeof(object).Assembly.Location),
            trusted is null ? null : Path.GetDirectoryName(trusted),
        };

        foreach (var candidate in candidates.Where(candidate => !string.IsNullOrWhiteSpace(candidate)))
        {
            for (var directory = new DirectoryInfo(candidate!); directory is not null; directory = directory.Parent)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "packs")))
                {
                    return directory.FullName;
                }
            }
        }

        return null;
    }
}
