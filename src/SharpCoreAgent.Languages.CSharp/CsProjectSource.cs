using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Code;
using AgentWorkspace = SharpCoreAgent.Domain.Workspace;

namespace SharpCoreAgent.Languages.CSharp;

/// <summary>Reads what the C# compiler would be told, without running MSBuild.</summary>
internal sealed class CsProjectSource(AgentWorkspace workspace)
{
    private static readonly string[] CoreImplicitUsings =
    [
        "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http",
        "System.Threading", "System.Threading.Tasks",
    ];

    private static readonly string[] WebImplicitUsings =
    [
        "System.Net.Http.Json", "Microsoft.AspNetCore.Builder", "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Http", "Microsoft.AspNetCore.Routing", "Microsoft.Extensions.Configuration",
        "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Logging",
    ];

    public CsProjectLoad Load(string projectFile, CancellationToken cancellationToken)
    {
        XDocument project;
        try
        {
            using var stream = File.OpenRead(projectFile);
            project = XDocument.Load(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return CsProjectLoad.Failed(CsFailures.NotAssembled(
                $"{CsPropertyBag.Display(projectFile)} could not be read: {ex.Message}"));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(projectFile)!;
        var properties = new CsPropertyBag(projectFile);
        var propertyFiles = ReadDirectoryProperties(directory, properties);
        properties.Apply(project, directory);
        ApplyDirectoryTargets(properties, propertyFiles);
        var sdk = (string?)project.Root?.Attribute("Sdk") ?? string.Empty;

        var targetFramework = TargetFramework(properties);
        if (string.IsNullOrWhiteSpace(targetFramework))
        {
            return CsProjectLoad.Failed(CsFailures.NotAssembled(
                $"{CsPropertyBag.Display(projectFile)} declares no TargetFramework."));
        }

        var (sources, sourceFailure) = Sources(project, projectFile, directory, properties, cancellationToken);
        if (sourceFailure is not null)
        {
            return CsProjectLoad.Failed(sourceFailure);
        }

        if (sources.Count == 0)
        {
            return CsProjectLoad.Failed(CsFailures.NotAssembled(
                $"{CsPropertyBag.Display(projectFile)} compiles no C# source files."));
        }

        var (frameworks, frameworkFailure) = FrameworkReferences(project, sdk, properties);
        if (frameworkFailure is not null)
        {
            return CsProjectLoad.Failed(frameworkFailure);
        }

        var frameworkReferences = CsReferencePacks.For(targetFramework, frameworks, out var packFailure);
        if (packFailure is not null)
        {
            return CsProjectLoad.Failed(CsFailures.NotAssembled(packFailure));
        }

        var packageCount = project.Descendants().Count(element =>
            element.Name.LocalName == "PackageReference" && element.Attribute("Include") is not null);
        if (project.Descendants().Any(element =>
            element.Name.LocalName == "PackageReference" && element.Attribute("Condition") is not null))
        {
            return CsProjectLoad.Failed(CsFailures.NotAssembled(
                $"{CsPropertyBag.Display(projectFile)} uses a conditional PackageReference this reader cannot evaluate."));
        }
        var (packages, packageFailure) = packageCount == 0
            ? ((IReadOnlyList<string>)[], (string?)null)
            : CsProjectAssets.Read(directory, targetFramework);
        if (packageFailure is not null)
        {
            return CsProjectLoad.Failed(CsFailures.NotAssembled(packageFailure));
        }

        var (projectReferences, referenceFailure) = ProjectReferences(project, directory, properties);
        if (referenceFailure is not null)
        {
            return CsProjectLoad.Failed(referenceFailure);
        }

        var (hintReferences, hintFailure) = HintReferences(project, directory, properties);
        if (hintFailure is not null)
        {
            return CsProjectLoad.Failed(hintFailure);
        }

        var references = frameworkReferences.Concat(packages).Concat(hintReferences)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var parseOptions = ParseOptions(properties, targetFramework);
        var compilationOptions = CompilationOptions(properties);
        var globalUsings = GlobalUsings(directory, properties, sdk);
        var stamp = Stamp(projectFile, sources, references, projectReferences, propertyFiles, targetFramework, globalUsings);

        return CsProjectLoad.Of(new CsProjectModel
        {
            ProjectFile = projectFile,
            TargetFramework = targetFramework,
            AssemblyName = properties.Get("AssemblyName") ?? properties.Get("MSBuildProjectName") ?? Path.GetFileNameWithoutExtension(projectFile),
            Sources = sources,
            ReferencePaths = references,
            ProjectReferences = projectReferences,
            InMemorySources = globalUsings,
            ParseOptions = parseOptions,
            CompilationOptions = compilationOptions,
            InputStamp = stamp,
        });
    }

    private List<string> ReadDirectoryProperties(string directory, CsPropertyBag properties)
    {
        var files = new List<string>();
        var props = CsPropertyBag.NearestFile(directory, workspace.Root, "Directory.Build.props");
        if (props is not null)
        {
            files.Add(props);
            properties.Apply(Load(props), Path.GetDirectoryName(props)!);
        }

        var targets = CsPropertyBag.NearestFile(directory, workspace.Root, "Directory.Build.targets");
        if (targets is not null)
        {
            files.Add(targets);
        }

        return files;
    }

    private static void ApplyDirectoryTargets(CsPropertyBag properties, List<string> files)
    {
        var targets = files.FirstOrDefault(file => Path.GetFileName(file).Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase));
        if (targets is not null)
        {
            properties.Apply(Load(targets), Path.GetDirectoryName(targets)!);
        }
    }

    private static XDocument Load(string path)
    {
        using var stream = File.OpenRead(path);
        return XDocument.Load(stream);
    }

    private static string? TargetFramework(CsPropertyBag properties)
    {
        var plural = properties.Get("TargetFrameworks");
        return plural is null
            ? properties.Get("TargetFramework")
            : plural.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
    }

    private (IReadOnlyList<string> Sources, CodeFailure? Failure) Sources(
        XDocument project,
        string projectFile,
        string directory,
        CsPropertyBag properties,
        CancellationToken cancellationToken)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var sources = new HashSet<string>(comparer);
        if (!string.Equals(properties.Get("EnableDefaultCompileItems"), "false", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var file in CsGlob.Enumerate(directory))
            {
                sources.Add(file);
            }
        }

        var items = project.Descendants().Where(element => element.Name.LocalName == "Compile").ToArray();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Attribute("Condition") is not null || item.Attribute("Exclude") is not null)
            {
                return ([], CsFailures.NotAssembled(
                    $"{CsPropertyBag.Display(projectFile)} uses a conditional or Exclude Compile item this reader cannot evaluate."));
            }

            if (item.Attribute("Include") is not { } include)
            {
                continue;
            }

            foreach (var raw in include.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var expanded = properties.Expand(raw);
                if (expanded is null)
                {
                    return ([], CsFailures.NotAssembled(
                        $"{CsPropertyBag.Display(projectFile)} computes its source list from an unsupported property: '{raw}'."));
                }

                expanded = expanded.Replace('\\', '/').Trim();
                if (expanded == ".." || expanded.StartsWith("../", StringComparison.Ordinal))
                {
                    return ([], CsFailures.NotAssembled(
                        $"{CsPropertyBag.Display(projectFile)} includes source outside its project directory, which this reader will not follow."));
                }

                if (Path.IsPathRooted(expanded))
                {
                    return ([], CsFailures.NotAssembled(
                        $"{CsPropertyBag.Display(projectFile)} includes an absolute source path this reader will not follow."));
                }

                if (expanded.Contains('*', StringComparison.Ordinal) || expanded.Contains('?', StringComparison.Ordinal))
                {
                    foreach (var file in CsGlob.Expand(directory, expanded))
                    {
                        sources.Add(file);
                    }
                }
                else
                {
                    var full = Path.GetFullPath(Path.Combine(directory, expanded));
                    if (!File.Exists(full) || !full.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    {
                        return ([], CsFailures.NotAssembled(
                            $"{CsPropertyBag.Display(projectFile)} lists '{expanded}' as Compile, but it is not a C# file on disk."));
                    }

                    sources.Add(full);
                }
            }
        }

        foreach (var item in items)
        {
            if (item.Attribute("Remove") is not { } remove)
            {
                continue;
            }

            foreach (var raw in remove.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var expanded = properties.Expand(raw);
                if (expanded is null)
                {
                    return ([], CsFailures.NotAssembled(
                        $"{CsPropertyBag.Display(projectFile)} computes a Compile Remove from an unsupported property: '{raw}'."));
                }

                sources.RemoveWhere(file => CsGlob.Matches(directory, file, expanded));
            }
        }

        return sources.Any(file => !IsPhysicallyInside(file))
            ? ([], CsFailures.NotAssembled($"{CsPropertyBag.Display(projectFile)} includes source outside the workspace."))
            : ([.. sources.Order(StringComparer.Ordinal)], null);
    }

    private bool IsPhysicallyInside(string path)
    {
        if (!workspace.IsInside(path))
        {
            return false;
        }

        try
        {
            var target = File.ResolveLinkTarget(path, returnFinalTarget: true);
            return target is null || workspace.IsInside(target.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }


    private static (IReadOnlySet<string> Names, CodeFailure? Failure) FrameworkReferences(
        XDocument project,
        string sdk,
        CsPropertyBag properties)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Microsoft.NETCore.App" };
        if (sdk.Contains(".Web", StringComparison.OrdinalIgnoreCase))
        {
            names.Add("Microsoft.AspNetCore.App");
        }

        if (properties.IsTrue("UseWPF") || properties.IsTrue("UseWindowsForms"))
        {
            names.Add("Microsoft.WindowsDesktop.App");
        }

        foreach (var item in project.Descendants().Where(element => element.Name.LocalName == "FrameworkReference"))
        {
            var include = item.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            if (!string.Equals(include, "Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(include, "Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(include, "Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase))
            {
                return (names, CsFailures.NotAssembled($"Framework reference '{include}' has no supported reference pack."));
            }

            names.Add(include);
        }

        return (names, null);
    }

    private (IReadOnlyList<string> References, CodeFailure? Failure) ProjectReferences(
        XDocument project,
        string directory,
        CsPropertyBag properties)
    {
        var references = new List<string>();
        foreach (var item in project.Descendants().Where(element => element.Name.LocalName == "ProjectReference"))
        {
            if (item.Attribute("Condition") is not null)
            {
                return ([], CsFailures.NotAssembled("A conditional ProjectReference cannot be evaluated without MSBuild."));
            }

            var include = item.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            var expanded = properties.Expand(include);
            if (expanded is null || expanded.Contains('*', StringComparison.Ordinal) || expanded.Contains('?', StringComparison.Ordinal))
            {
                return ([], CsFailures.NotAssembled($"Project reference '{include}' cannot be evaluated without MSBuild."));
            }

            var full = Path.GetFullPath(Path.Combine(directory, expanded.Replace('\\', '/')));
            if (!string.Equals(Path.GetExtension(full), ".csproj", StringComparison.OrdinalIgnoreCase))
            {
                return ([], CsFailures.NotAssembled(
                    $"Project reference '{include}' is not a C# project, so this C# solution cannot include it."));
            }

            if (!workspace.IsInside(full))
            {
                return ([], CsFailures.NotAssembled(
                    $"Project reference '{include}' resolves outside the workspace, which this reader will not follow."));
            }

            if (File.ResolveLinkTarget(full, returnFinalTarget: true) is { } target && !workspace.IsInside(target.FullName))
            {
                return ([], CsFailures.NotAssembled(
                    $"Project reference '{include}' is a link outside the workspace, which this reader will not follow."));
            }

            if (!File.Exists(full))
            {
                return ([], CsFailures.NotAssembled($"Project reference '{include}' does not exist."));
            }

            references.Add(full);
        }

        return ([.. references.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)], null);
    }

    private (IReadOnlyList<string> References, CodeFailure? Failure) HintReferences(
        XDocument project,
        string directory,
        CsPropertyBag properties)
    {
        var references = new List<string>();
        foreach (var item in project.Descendants().Where(element => element.Name.LocalName == "Reference"))
        {
            if (item.Attribute("Condition") is not null)
            {
                return ([], CsFailures.NotAssembled("A conditional assembly Reference cannot be evaluated without MSBuild."));
            }

            var hint = item.Elements().FirstOrDefault(element => element.Name.LocalName == "HintPath")?.Value;
            if (string.IsNullOrWhiteSpace(hint))
            {
                var name = item.Attribute("Include")?.Value ?? "(unnamed)";
                return ([], CsFailures.NotAssembled($"Assembly reference '{name}' has no HintPath this reader can resolve."));
            }

            var expanded = properties.Expand(hint);
            if (expanded is null)
            {
                return ([], CsFailures.NotAssembled($"Assembly reference HintPath '{hint}' cannot be evaluated without MSBuild."));
            }

            var full = Path.GetFullPath(Path.Combine(directory, expanded.Replace('\\', '/')));
            if (!workspace.IsInside(full))
            {
                return ([], CsFailures.NotAssembled($"Assembly reference HintPath '{hint}' resolves outside the workspace."));
            }

            if (!File.Exists(full))
            {
                return ([], CsFailures.NotAssembled($"Assembly reference '{hint}' does not exist."));
            }

            references.Add(full);
        }

        return ([.. references.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)], null);
    }


    private static CSharpParseOptions ParseOptions(CsPropertyBag properties, string targetFramework)
    {
        var version = LanguageVersion.Default;
        var requested = properties.Get("LangVersion");
        if (!string.IsNullOrWhiteSpace(requested) && !LanguageVersionFacts.TryParse(requested, out version))
        {
            version = LanguageVersion.Default;
        }

        var defines = new HashSet<string>(StringComparer.Ordinal) { "DEBUG", "TRACE", "NET" };
        var baseFramework = targetFramework.Split('-')[0];
        var digits = new string([.. baseFramework.Skip(3).TakeWhile(char.IsAsciiDigit)]);
        if (digits.Length > 0)
        {
            defines.Add("NET" + digits + "_0");
            defines.Add("NET" + digits + "_0_OR_GREATER");
        }

        if (targetFramework.Contains("windows", StringComparison.OrdinalIgnoreCase))
        {
            defines.Add("WINDOWS");
        }

        var configured = properties.Get("DefineConstants");
        if (configured is not null)
        {
            foreach (var define in configured.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                defines.Add(define);
            }
        }

        return new CSharpParseOptions(
            version,
            DocumentationMode.Parse,
            SourceCodeKind.Regular,
            defines.Order(StringComparer.Ordinal));
    }

    private static CSharpCompilationOptions CompilationOptions(CsPropertyBag properties)
    {
        var output = properties.Get("OutputType");
        var kind = OutputKind.DynamicallyLinkedLibrary;
        if (output is not null && output.Equals("Exe", StringComparison.OrdinalIgnoreCase))
        {
            kind = OutputKind.ConsoleApplication;
        }
        else if (output is not null && output.Equals("WinExe", StringComparison.OrdinalIgnoreCase))
        {
            kind = OutputKind.WindowsApplication;
        }

        var nullable = properties.Get("Nullable")?.ToLowerInvariant() switch
        {
            "enable" => NullableContextOptions.Enable,
            "warnings" => NullableContextOptions.Warnings,
            "annotations" => NullableContextOptions.Annotations,
            _ => NullableContextOptions.Disable,
        };

        return new CSharpCompilationOptions(kind)
            .WithNullableContextOptions(nullable)
            .WithAllowUnsafe(properties.IsTrue("AllowUnsafeBlocks"))
            .WithOptimizationLevel(OptimizationLevel.Debug)
            .WithWarningLevel(9999);
    }

    private static IReadOnlyList<InMemorySource> GlobalUsings(
        string directory,
        CsPropertyBag properties,
        string sdk)
    {
        if (!properties.IsTrue("ImplicitUsings") && !string.Equals(properties.Get("ImplicitUsings"), "enable", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var obj = Path.Combine(directory, "obj");
        if (Directory.Exists(obj))
        {
            try
            {
                var generated = Directory.EnumerateFiles(obj, "*.GlobalUsings.g.cs", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ThenBy(path => path, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (generated is not null)
                {
                    return [new InMemorySource(generated, File.ReadAllText(generated))];
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A synthetic set below is still exact for the default SDK and better than a missing-using cascade.
            }
        }

        var sdkUsings = sdk.Contains(".Web", StringComparison.OrdinalIgnoreCase) ? WebImplicitUsings : [];
        var names = CoreImplicitUsings.Concat(sdkUsings).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var text = string.Join('\n', names.Select(name => "global using global::" + name + ";")) + "\n";
        return [new InMemorySource(Path.Combine(obj, "SharpCoreAgent.ImplicitUsings.g.cs"), text)];
    }

    private static string Stamp(
        string projectFile,
        IReadOnlyList<string> sources,
        IReadOnlyList<string> references,
        IReadOnlyList<string> projectReferences,
        IReadOnlyList<string> propertyFiles,
        string targetFramework,
        IReadOnlyList<InMemorySource> inMemorySources)
    {
        var builder = new StringBuilder();
        builder.Append(projectFile).Append('|').Append(targetFramework).Append('|');
        foreach (var file in propertyFiles.Concat(sources).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AppendStamp(builder, file);
        }

        foreach (var reference in references)
        {
            builder.Append(reference).Append('|');
        }

        foreach (var reference in projectReferences)
        {
            builder.Append(reference).Append('|');
        }

        foreach (var source in inMemorySources)
        {
            builder.Append(source.Path).Append('|').Append(source.Text).Append('|');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendStamp(StringBuilder builder, string path)
    {
        try
        {
            var info = new FileInfo(path);
            builder.Append(path).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('|').Append(info.Length).Append('|');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            builder.Append(path).Append("|missing|");
        }
    }
}

