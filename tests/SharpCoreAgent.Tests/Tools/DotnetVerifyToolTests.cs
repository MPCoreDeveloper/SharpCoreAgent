using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Tools;

/// <summary>
/// <c>dotnet_verify</c> is the answer the turn loop gates a completion on, so these tests pin the three things a
/// gate depends on: a passing run is a success, a failing run is an error that still carries every diagnostic, and
/// a path the workspace does not own never reaches the compiler.
/// </summary>
/// <remarks>
/// One test starts a real <c>dotnet build</c>, because the whole point of the tool is what the SDK does with a
/// project — a stub would test the stub. Everything else is answered without a process: argument validation, the
/// refusal of a path outside the root, and the command line the effect list records.
/// </remarks>
public sealed class DotnetVerifyToolTests
{
    private const string Project =
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net11.0</TargetFramework>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    [Fact]
    public void TheCommandNamesTheVerbAndTheProject()
    {
        Assert.Equal("dotnet build", DotnetVerifyTool.CommandFor(DotnetVerifyTool.Build, null));
        Assert.Equal(
            "dotnet test src/App/App.csproj",
            DotnetVerifyTool.CommandFor(DotnetVerifyTool.Test, "src/App/App.csproj"));
    }

    [Fact]
    public async Task AProjectOutsideTheRootIsRefused()
    {
        using var sandbox = ToolTestWorkspace.Create();

        var result = await new DotnetVerifyTool().ExecuteAsync(
            ToolTestWorkspace.Call(
                DotnetVerifyTool.Name,
                ToolTestWorkspace.Args(new { kind = DotnetVerifyTool.Build, project = "../outside.csproj" })),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsRefused);
    }

    [Fact]
    public async Task AMissingProjectIsAFailureThatNamesIt()
    {
        using var sandbox = ToolTestWorkspace.Create();

        var result = await new DotnetVerifyTool().ExecuteAsync(
            ToolTestWorkspace.Call(
                DotnetVerifyTool.Name,
                ToolTestWorkspace.Args(new { kind = DotnetVerifyTool.Build, project = "src/Nowhere.csproj" })),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.False(result.IsRefused);
        Assert.Contains("src/Nowhere.csproj", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void TheToolDeclaresTheDotnetProfileTheGateUses()
    {
        var tool = new DotnetVerifyTool();
        var profile = Assert.Single(tool.Profiles);

        Assert.Equal(DotnetVerifyTool.ProfileName, profile.Name);
        Assert.Equal(".NET", profile.DisplayName);
        Assert.Contains(".cs", profile.Extensions);
        Assert.Contains(".csproj", profile.Extensions);
        Assert.True(profile.Matches("src/App/Program.cs"));
        Assert.True(profile.Matches("src/App/Program.CS"));
    }

    [Fact]
    public async Task AnUnknownKindIsAFailureThatNamesTheTwoValues()
    {
        using var sandbox = ToolTestWorkspace.Create();

        var result = await new DotnetVerifyTool().ExecuteAsync(
            ToolTestWorkspace.Call(DotnetVerifyTool.Name, """{"kind":"publish"}"""),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("'build'", result.Content, StringComparison.Ordinal);
        Assert.Contains("'test'", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARealBuildPassesAndThenFailsWithItsDiagnostics()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("App/App.csproj", Project);
        sandbox.Write("App/Good.cs", "namespace App; public static class Good { public static int Add(int a, int b) => a + b; }");

        var tool = new DotnetVerifyTool();
        var call = ToolTestWorkspace.Call(
            DotnetVerifyTool.Name,
            ToolTestWorkspace.Args(new { kind = DotnetVerifyTool.Build, project = "App/App.csproj" }));

        var good = await tool.ExecuteAsync(call, sandbox.Context, TestContext.Current.CancellationToken);

        Assert.False(good.IsError, good.Content);
        Assert.Contains("result       passed", good.Content, StringComparison.Ordinal);
        Assert.Contains("exit code    0", good.Content, StringComparison.Ordinal);
        Assert.Contains("errors       none", good.Content, StringComparison.Ordinal);
        Assert.Contains("Build succeeded.", good.Content, StringComparison.Ordinal);
        Assert.Equal(
            new ToolEffect(ToolEffectKind.CommandRun, "dotnet build App/App.csproj"),
            Assert.Single(good.Effects));

        // A file that no longer compiles: the run must be an error — the gate reads that flag — and the result must
        // still name the diagnostic, because that is what the model fixes.
        sandbox.Write("App/Good.cs", "namespace App; public static class Good { public static int Add(int a, int b) => a + ; }");

        var broken = await tool.ExecuteAsync(call, sandbox.Context, TestContext.Current.CancellationToken);

        Assert.True(broken.IsError);
        Assert.Contains("result       failed", broken.Content, StringComparison.Ordinal);
        Assert.Contains("error CS", broken.Content, StringComparison.Ordinal);
        Assert.Contains("errors       1", broken.Content, StringComparison.Ordinal);

        // The effect survives the failure: a report that lists the compiler beside a failed result says more than
        // one that lists nothing at all.
        Assert.Equal(
            new ToolEffect(ToolEffectKind.CommandRun, "dotnet build App/App.csproj"),
            Assert.Single(broken.Effects));
    }
}
