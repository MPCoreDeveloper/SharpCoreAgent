using System.Globalization;
using System.Text.Json;
using SharpCoreAgent.Tests.Tools;

namespace SharpCoreAgent.Tests.Languages;

/// <summary>
/// A throwaway workspace holding a two-file F# project that needs no restore, no build and no network.
/// </summary>
/// <remarks>
/// The project is real: a <c>.fsproj</c>, an ordered compile list and an <c>obj/project.assets.json</c> shaped
/// exactly like a restored one. What it does not need is NuGet, because the only package it references is
/// <c>FSharp.Core</c>, which the test host already has on disk — the fixture copies it into a package layout and
/// points the assets file at that folder. So the adapter under test reads the same files it reads in a real
/// workspace, and the test still runs offline in a fresh clone.
/// </remarks>
internal static class FsSandbox
{
    /// <summary>
    /// The first file of the fixture project: a declaration the second file calls.
    /// </summary>
    public const string CartSource = """
        module Cart

        type Item = { Name: string; Price: decimal }

        let total (items: Item list) =
            items |> List.sumBy (fun i -> i.Price)
        """;

    /// <summary>
    /// The second file: one correct call and one call to a name that does not exist.
    /// </summary>
    public const string ProgramSource = """
        module Program

        open Cart

        let items = [ { Name = "a"; Price = 2.5M }; { Name = "b"; Price = 3.0M } ]

        let run () =
            printfn "%A" (total items)
            printfn "%A" (Subtotal items)
        """;

    /// <summary>
    /// Creates the workspace.
    /// </summary>
    /// <param name="restored">
    /// Whether the restore's output is written. <see langword="false"/> models a project that was cloned and never
    /// built.
    /// </param>
    /// <returns>The sandbox, which the caller disposes.</returns>
    public static ToolTestWorkspace Create(bool restored = true)
    {
        var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("src/Cart.fs", CartSource);
        sandbox.Write("src/Program.fs", ProgramSource);
        sandbox.Write("src/Fixture.fsproj", ProjectFile());

        if (restored)
        {
            var fsharpCore = Path.Combine(AppContext.BaseDirectory, "FSharp.Core.dll");
            var packageDirectory = Path.Combine(sandbox.Root, "packages", "fsharp.core", "9.0.0", "lib", "netstandard2.1");
            Directory.CreateDirectory(packageDirectory);
            File.Copy(fsharpCore, Path.Combine(packageDirectory, "FSharp.Core.dll"), overwrite: true);

            sandbox.Write("src/obj/project.assets.json", AssetsFile(sandbox.FullPath("packages")));
        }

        return sandbox;
    }

    /// <summary>
    /// Finds where a token starts in a fixture source.
    /// </summary>
    /// <param name="source">The source text.</param>
    /// <param name="token">The token to locate.</param>
    /// <returns>The 1-based line and the 0-based column of the token's first character.</returns>
    public static (int Line, int Column) PositionOf(string source, string token)
    {
        var index = source.IndexOf(token, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"'{token}' does not appear in the fixture source."));
        }

        var line = 1;
        var column = 0;
        for (var position = 0; position < index; position++)
        {
            if (source[position] == '\n')
            {
                line++;
                column = 0;
            }
            else if (source[position] != '\r')
            {
                column++;
            }
        }

        return (line, column);
    }

    private static string ProjectFile() => $$"""
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <TargetFramework>net{{Environment.Version.Major}}.0</TargetFramework>
          </PropertyGroup>

          <ItemGroup>
            <Compile Include="Cart.fs" />
            <Compile Include="Program.fs" />
          </ItemGroup>

        </Project>
        """;

    private static string AssetsFile(string packageFolder) => JsonSerializer.Serialize(
        new
        {
            version = 3,
            targets = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [$"net{Environment.Version.Major}.0"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["FSharp.Core/9.0.0"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["type"] = "package",
                        ["compile"] = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["lib/netstandard2.1/FSharp.Core.dll"] = new { },
                        },
                        ["runtime"] = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["lib/netstandard2.1/FSharp.Core.dll"] = new { },
                        },
                    },
                },
            },
            libraries = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["FSharp.Core/9.0.0"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["type"] = "package",
                    ["path"] = "fsharp.core/9.0.0",
                },
            },
            packageFolders = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [packageFolder] = new { },
            },
        },
        new JsonSerializerOptions { WriteIndented = true });
}
