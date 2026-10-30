using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Code;

namespace SharpCoreAgent.Languages.FSharp;

/// <summary>
/// The outcome of describing one project: a project, or the reason there is none.
/// </summary>
/// <param name="Project">The resolved project, or <see langword="null"/> on failure.</param>
/// <param name="Failure">The reason, or <see langword="null"/> on success.</param>
internal readonly record struct FsProjectLoad(FsProject? Project, CodeFailure? Failure)
{
    /// <summary>Creates a successful load.</summary>
    /// <param name="project">The resolved project.</param>
    /// <returns>A load that carries a project.</returns>
    public static FsProjectLoad Of(FsProject project) => new(project, null);

    /// <summary>Creates a failed load.</summary>
    /// <param name="failure">The reason.</param>
    /// <returns>A load that carries a failure.</returns>
    public static FsProjectLoad Failed(CodeFailure failure) => new(null, failure);
}

/// <summary>
/// Reads what MSBuild would have told the compiler, without running MSBuild.
/// </summary>
/// <remarks>
/// A <c>.fsproj</c> is an XML document with two interesting parts for a type checker — the ordered
/// <c>&lt;Compile&gt;</c> list and the properties that shape the command line — and a restore is a JSON document
/// listing one assembly per package. Both are files on disk, so this type reads them and runs nothing: no
/// <c>dotnet build</c>, no restore, no process, no network.
/// <para>
/// That is a deliberate trade. Reading the restore's output instead of invoking the restore means the answer is
/// only as good as the last build, so a project that has never been restored gets <c>FSH002</c> and a hint naming
/// <c>dotnet_verify</c> rather than a five-second restore the model did not ask for in the middle of a read-only
/// tool call.
/// </para>
/// <para>
/// Deliberate limits, all of them reported rather than guessed: a wildcard or <c>$(Property)</c> in the compile
/// list, a target framework the running runtime cannot stand in for, a package the restore lists but did not
/// extract, and a project reference with no built assembly. Guessing at any of these produces diagnostics that do
/// not exist, which is worse than a failure that says what to run.
/// </para>
/// </remarks>
/// <param name="workspace">The workspace the paths are reported relative to.</param>
internal sealed class MsBuildFsProjectSource(Workspace workspace)
{
    private static readonly string[] AlwaysOnOptions =
    [
        "--targetprofile:netcore",
        "--noframework",
        "--simpleresolution",
        "--debug:portable",
    ];

    /// <summary>
    /// Describes one project as compiler arguments.
    /// </summary>
    /// <param name="projectFile">The absolute path of the <c>.fsproj</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The project, or the failure that explains why it could not be described.</returns>
    public FsProjectLoad Load(string projectFile, CancellationToken cancellationToken)
    {
        XDocument document;
        try
        {
            using var stream = File.OpenRead(projectFile);
            document = XDocument.Load(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return FsProjectLoad.Failed(FsFailures.NotAssembled(
                string.Create(CultureInfo.InvariantCulture, $"'{Name(projectFile)}' could not be read: {ex.Message}")));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var projectDirectory = Path.GetDirectoryName(projectFile) ?? workspace.Root;
        var sources = new List<string>();
        foreach (var include in CompileIncludes(document))
        {
            if (include.IndexOfAny(['*', '@']) >= 0 || include.Contains("$(", StringComparison.Ordinal))
            {
                return FsProjectLoad.Failed(FsFailures.NotAssembled(string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{Name(projectFile)}' computes its source list ('{include}'), which cannot be read without MSBuild.")));
            }

            var full = Path.GetFullPath(Path.Combine(projectDirectory, include.Replace('\\', '/')));
            if (!File.Exists(full))
            {
                return FsProjectLoad.Failed(FsFailures.NotAssembled(string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{workspace.ToRelative(full)}' is listed in '{Name(projectFile)}' but does not exist.")));
            }

            sources.Add(full);
        }

        if (sources.Count == 0)
        {
            return FsProjectLoad.Failed(FsFailures.NotAssembled(string.Create(
                CultureInfo.InvariantCulture,
                $"'{Name(projectFile)}' declares no <Compile> items, so it describes no compilation.")));
        }

        return Assemble(document, projectFile, projectDirectory, sources);
    }

    private static FsProjectLoad Assemble(XDocument document, string projectFile, string projectDirectory, List<string> sources)
    {
        var targetFramework = TargetFramework(document);
        if (!RuntimeMatches(targetFramework, out var mismatch))
        {
            return FsProjectLoad.Failed(FsFailures.NotAssembled(mismatch));
        }

        var (packages, packageFailure) = ResolvePackages(projectDirectory);
        if (packageFailure is not null)
        {
            return FsProjectLoad.Failed(FsFailures.NotAssembled(packageFailure));
        }

        var (projectReferences, referenceFailure) = ResolveProjectReferences(document, projectDirectory, targetFramework);
        if (referenceFailure is not null)
        {
            return FsProjectLoad.Failed(FsFailures.NotAssembled(referenceFailure));
        }

        var references = new List<string>(FrameworkAssemblies.Value);
        references.AddRange(packages);
        references.AddRange(projectReferences);

        var arguments = new List<string>(AlwaysOnOptions);
        arguments.AddRange(Defines(document).Select(static define => "--define:" + define));
        arguments.AddRange(references
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static reference => "-r:" + reference));
        arguments.AddRange(sources);

        return FsProjectLoad.Of(new FsProject
        {
            ProjectFile = projectFile,
            Sources = sources,
            Arguments = arguments,
        });
    }

    private static IEnumerable<string> CompileIncludes(XDocument document) =>
        document.Descendants()
            .Where(static element => element.Name.LocalName == "Compile")
            .Select(static element => (string?)element.Attribute("Include"))
            .Where(static include => !string.IsNullOrWhiteSpace(include))
            .Select(static include => include!.Trim());

    private static string? Property(XDocument document, string name) =>
        document.Descendants()
            .Where(element => element.Name.LocalName == name)
            .Select(static element => element.Value.Trim())
            .FirstOrDefault(static value => value.Length > 0);

    private static string TargetFramework(XDocument document)
    {
        var plural = Property(document, "TargetFrameworks");
        if (plural is not null)
        {
            return plural.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        }

        return Property(document, "TargetFramework") ?? string.Empty;
    }

    private static IEnumerable<string> Defines(XDocument document) =>
        (Property(document, "DefineConstants") ?? string.Empty)
            .Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal);

    private static string Name(string projectFile) => Path.GetFileName(projectFile);

    private static bool RuntimeMatches(string targetFramework, out string detail)
    {
        detail = string.Empty;
        if (string.IsNullOrWhiteSpace(targetFramework))
        {
            detail = "The project declares no <TargetFramework>, so its references cannot be worked out.";
            return false;
        }

        var digits = new string([.. targetFramework.Skip(3).TakeWhile(char.IsAsciiDigit)]);
        if (!targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || major != Environment.Version.Major)
        {
            detail = string.Create(
                CultureInfo.InvariantCulture,
                $"The project targets '{targetFramework}' but this process runs .NET {Environment.Version.Major}, so checking it would compare the code against the wrong reference assemblies.");
            return false;
        }

        return true;
    }

    private static readonly Lazy<IReadOnlyList<string>> FrameworkAssemblies = new(() =>
    {
        var trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        return string.IsNullOrEmpty(trusted)
            ? []
            : [.. trusted.Split(Path.PathSeparator).Where(static path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))];
    });

    private static (IReadOnlyList<string> References, string? Failure) ResolvePackages(string projectDirectory)
    {
        var assets = Path.Combine(projectDirectory, "obj", "project.assets.json");
        if (!File.Exists(assets))
        {
            return ([], "The project has not been restored: obj/project.assets.json is missing.");
        }

        try
        {
            using var stream = File.OpenRead(assets);
            using var json = JsonDocument.Parse(stream);
            var root = json.RootElement;

            var packageFolder = FirstPropertyName(root, "packageFolders");
            if (packageFolder is null)
            {
                return ([], "project.assets.json names no package folder, so its packages have no location.");
            }

            var target = FirstPropertyValue(root, "targets");
            if (target is null)
            {
                return ([], "project.assets.json lists no target, so it describes no compilation.");
            }

            var references = new List<string>();
            foreach (var library in target.Value.EnumerateObject())
            {
                var entry = library.Value;
                if (IsProject(entry))
                {
                    continue;
                }

                if (!entry.TryGetProperty("compile", out var compile))
                {
                    continue;
                }

                var path = LibraryPath(root, library.Name);
                if (path is null)
                {
                    continue;
                }

                foreach (var relative in CompileAssets(compile))
                {
                    var candidate = Path.GetFullPath(Path.Combine(
                        packageFolder,
                        path.Replace('/', Path.DirectorySeparatorChar),
                        relative.Replace('/', Path.DirectorySeparatorChar)));

                    if (!File.Exists(candidate))
                    {
                        return ([], $"Package '{library.Name}' is restored but '{relative}' is not on disk.");
                    }

                    references.Add(candidate);
                }
            }

            return (references, null);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return ([], $"obj/project.assets.json could not be read: {ex.Message}");
        }
    }

    private static IEnumerable<string> CompileAssets(JsonElement compile)
    {
        // A restored project writes `compile` as an object keyed by the asset path; older and synthetic files use a
        // plain array. Both are accepted because a reader that silently skipped the object form would resolve no
        // package references at all and report every project as uncheckable.
        if (compile.ValueKind == JsonValueKind.Array)
        {
            return compile.EnumerateArray().Select(static asset => asset.GetString()).OfType<string>();
        }

        return compile.ValueKind == JsonValueKind.Object
            ? compile.EnumerateObject().Select(static asset => asset.Name)
            : [];
    }

    private static string? FirstPropertyName(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return property.EnumerateObject().Select(static entry => entry.Name).FirstOrDefault();
    }

    private static JsonElement? FirstPropertyValue(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return property.EnumerateObject().Select(static entry => (JsonElement?)entry.Value).FirstOrDefault();
    }

    private static string? LibraryPath(JsonElement root, string key) =>
        root.TryGetProperty("libraries", out var libraries)
        && libraries.ValueKind == JsonValueKind.Object
        && libraries.TryGetProperty(key, out var library)
        && library.TryGetProperty("path", out var path)
        && path.ValueKind == JsonValueKind.String
            ? path.GetString()
            : null;

    private static bool IsProject(JsonElement entry) =>
        entry.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
        && string.Equals(type.GetString(), "project", StringComparison.OrdinalIgnoreCase);

    private static (IReadOnlyList<string> References, string? Failure) ResolveProjectReferences(
        XDocument document,
        string projectDirectory,
        string targetFramework)
    {
        var references = new List<string>();
        foreach (var include in document.Descendants()
            .Where(static element => element.Name.LocalName == "ProjectReference")
            .Select(static element => (string?)element.Attribute("Include"))
            .Where(static include => !string.IsNullOrWhiteSpace(include)))
        {
            var full = Path.GetFullPath(Path.Combine(projectDirectory, include!.Trim().Replace('\\', '/')));
            var directory = Path.GetDirectoryName(full);
            var name = Path.GetFileNameWithoutExtension(full);
            if (directory is null)
            {
                return ([], $"Project reference '{include}' has no directory.");
            }

            var assembly = FindBuiltAssembly(directory, name, targetFramework);
            if (assembly is null)
            {
                return ([], $"Project reference '{name}' has no assembly built for {targetFramework}.");
            }

            references.Add(assembly);
        }

        return (references, null);
    }

    private static string? FindBuiltAssembly(string projectDirectory, string name, string targetFramework)
    {
        try
        {
            var bin = Path.Combine(projectDirectory, "bin");
            if (!Directory.Exists(bin))
            {
                return null;
            }

            return Directory.EnumerateFiles(bin, name + ".dll", SearchOption.AllDirectories)
                .Where(path => path.Contains(targetFramework, StringComparison.OrdinalIgnoreCase))
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
