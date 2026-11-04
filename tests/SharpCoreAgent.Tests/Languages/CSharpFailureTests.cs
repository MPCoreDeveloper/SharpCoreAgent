using SharpCoreAgent.Languages.CSharp;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Languages;

public sealed class CSharpFailureTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SignatureHelpSelectsTheOverloadAndNamedArgument()
    {
        using var sandbox = CsSandbox.Create();
        var intelligence = new CSharpCodeIntelligence(sandbox.Workspace);
        var (line, column) = CsSandbox.PositionOf(CsSandbox.ProgramSource, "tax: 1m");
        column += "tax: ".Length;

        var result = await intelligence.GetSignatureAsync("App/Program.cs", line, column, Token);

        Assert.Null(result.Failure);
        Assert.True(result.Signatures.Count >= 2);
        var selected = result.Signatures[result.ActiveSignature];
        Assert.Contains("Cart.Total", selected.Display, StringComparison.Ordinal);
        Assert.Equal(["decimal[] prices", "decimal tax"], selected.Parameters);
        Assert.Equal(1, selected.ActiveParameter);
    }

    [Fact]
    public async Task SignatureHelpMarksTheFirstPositionalArgument()
    {
        using var sandbox = CsSandbox.Create();
        var intelligence = new CSharpCodeIntelligence(sandbox.Workspace);
        var (line, column) = CsSandbox.PositionOf(CsSandbox.ProgramSource, "Total(prices))");
        column += "Total(".Length;

        var result = await intelligence.GetSignatureAsync("App/Program.cs", line, column, Token);

        Assert.Null(result.Failure);
        Assert.Equal(0, result.Signatures[result.ActiveSignature].ActiveParameter);
    }

    [Fact]
    public async Task SignatureHelpHandlesObjectCreation()
    {
        using var sandbox = CsSandbox.Create();
        var intelligence = new CSharpCodeIntelligence(sandbox.Workspace);
        var (line, column) = CsSandbox.PositionOf(CsSandbox.ProgramSource, """Item("widget""");
        column += "Item(".Length;

        var result = await intelligence.GetSignatureAsync("App/Program.cs", line, column, Token);

        Assert.Null(result.Failure);
        var signature = result.Signatures[result.ActiveSignature];
        Assert.Contains("Item", signature.Display, StringComparison.Ordinal);
        Assert.Equal(["string name", "decimal price"], signature.Parameters);
        Assert.Equal(0, signature.ActiveParameter);
    }

    [Fact]
    public async Task DirectoryBuildPropertiesAndCompileRemoveReachTheCompilation()
    {
        using var sandbox = CsSandbox.Create();

        var result = await new CSharpCodeIntelligence(sandbox.Workspace)
            .GetDiagnosticsAsync("Cart/Cart.cs", Token);

        Assert.Null(result.Failure);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task AWorkspaceWithoutAProjectSaysHowToFindTheRoot()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("Program.cs", CsSandbox.ProgramSource);

        var result = await new CSharpCodeIntelligence(sandbox.Workspace)
            .GetDiagnosticsAsync("Program.cs", Token);

        Assert.Equal("CSH001", result.Failure?.Code);
        Assert.Contains("list_roots", result.Failure?.Hint ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnsupportedTargetIsAnExplicitFailure()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("Program.cs", "class Program { static void Main() { } }");
        sandbox.Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup>
            </Project>
            """);

        var result = await new CSharpCodeIntelligence(sandbox.Workspace)
            .GetDiagnosticsAsync("Program.cs", Token);

        Assert.Equal("CSH002", result.Failure?.Code);
        Assert.Contains("reference pack", result.Failure?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARestoredPackageCompileAssetIsPartOfTheCompilation()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var target = $"net{Environment.Version.Major}.0";
        var packageDirectory = Path.Combine(sandbox.Root, "packages", "fsharp.core", "10.1.403", "lib", "netstandard2.1");
        Directory.CreateDirectory(packageDirectory);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "FSharp.Core.dll"),
            Path.Combine(packageDirectory, "FSharp.Core.dll"));
        sandbox.Write("Program.cs", """
            class Program
            {
                static object Value => typeof(Microsoft.FSharp.Core.FSharpFunc<int, int>);
            }
            """);
        sandbox.Write("App.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>{{target}}</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="FSharp.Core" Version="10.1.403" /></ItemGroup>
            </Project>
            """);
        sandbox.Write("obj/project.assets.json", $$"""
            {
              "version": 3,
              "targets": {
                "{{target}}": {
                  "FSharp.Core/10.1.403": {
                    "type": "package",
                    "compile": { "lib/netstandard2.1/FSharp.Core.dll": {} }
                  }
                }
              },
              "libraries": {
                "FSharp.Core/10.1.403": { "type": "package", "path": "fsharp.core/10.1.403" }
              },
              "packageFolders": { "{{sandbox.FullPath("packages").Replace("\\", "\\\\")}}": {} }
            }
            """);

        var result = await new CSharpCodeIntelligence(sandbox.Workspace)
            .GetDiagnosticsAsync("Program.cs", Token);

        Assert.Null(result.Failure);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task AnUnrestoredPackageProjectSaysToBuild()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("Program.cs", "class Program { static void Main() { } }");
        sandbox.Write("App.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net{{Environment.Version.Major}}.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="Example.Package" Version="1.0.0" /></ItemGroup>
            </Project>
            """);

        var result = await new CSharpCodeIntelligence(sandbox.Workspace)
            .GetDiagnosticsAsync("Program.cs", Token);

        Assert.Equal("CSH002", result.Failure?.Code);
        Assert.Contains("project.assets.json", result.Failure?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANonCSharpPathIsDeclinedByTheAdapter()
    {
        using var sandbox = CsSandbox.Create();

        var result = await new CSharpCodeIntelligence(sandbox.Workspace)
            .GetDiagnosticsAsync("Cart/Cart.fs", Token);

        Assert.Equal("CSH004", result.Failure?.Code);
    }

    [Fact]
    public async Task AWhitespacePositionSaysNothingIsThere()
    {
        using var sandbox = CsSandbox.Create();
        var (line, _) = CsSandbox.PositionOf(CsSandbox.ProgramSource, "Subtotal appears");

        var result = await new CSharpCodeIntelligence(sandbox.Workspace)
            .FindUsesAsync("App/Program.cs", line, 2, Token);

        Assert.Equal("CSH003", result.Failure?.Code);
    }
}
