using SharpCoreAgent.Infrastructure.Code;
using SharpCoreAgent.Infrastructure.Tools;
using SharpCoreAgent.Languages.CSharp;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Languages;

/// <summary>The three language-neutral tools through the router to the real C# adapter.</summary>
public sealed class CSharpToolTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FileDiagnosticsRendersTheCompilerErrorThroughTheRouter()
    {
        using var sandbox = CsSandbox.Create();
        var tool = new FileDiagnosticsTool(Router(sandbox.Workspace));
        var (line, column) = CsSandbox.PositionOf(CsSandbox.ProgramSource, "Subtotal");

        var result = await tool.ExecuteAsync(
            ToolTestWorkspace.Call(FileDiagnosticsTool.Name, ToolTestWorkspace.Args(new { path = "App/Program.cs" })),
            sandbox.Context,
            Token);

        Assert.False(result.IsError);
        Assert.Contains("App/Program.cs · 1 error, 0 warnings, 0 info", result.Content, StringComparison.Ordinal);
        Assert.Contains($"App/Program.cs:{line}:{column} · error CS0103", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsesAtRendersTheDeclarationAndCrossProjectUse()
    {
        using var sandbox = CsSandbox.Create();
        var tool = new UsesAtTool(Router(sandbox.Workspace));
        var (line, column) = CsSandbox.PositionOf(CsSandbox.ProgramSource, "Total(prices, tax");

        var result = await tool.ExecuteAsync(
            ToolTestWorkspace.Call(
                UsesAtTool.Name,
                ToolTestWorkspace.Args(new { path = "App/Program.cs", line, column })),
            sandbox.Context,
            Token);

        Assert.False(result.IsError);
        Assert.Contains("Total at App/Program.cs", result.Content, StringComparison.Ordinal);
        Assert.Contains("· 2 uses · 2 files", result.Content, StringComparison.Ordinal);
        Assert.Contains("definition Cart/Cart.cs:", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignatureAtRendersBothOverloadsAndTheNamedArgument()
    {
        using var sandbox = CsSandbox.Create();
        var tool = new SignatureAtTool(Router(sandbox.Workspace));
        var (line, column) = CsSandbox.PositionOf(CsSandbox.ProgramSource, "tax: 1m");
        column += "tax: ".Length;

        var result = await tool.ExecuteAsync(
            ToolTestWorkspace.Call(
                SignatureAtTool.Name,
                ToolTestWorkspace.Args(new { path = "App/Program.cs", line, column })),
            sandbox.Context,
            Token);

        Assert.False(result.IsError);
        Assert.Contains("2 signatures · active 1 of 2", result.Content, StringComparison.Ordinal);
        Assert.Contains("decimal tax ← active", result.Content, StringComparison.Ordinal);
    }

    private static LanguageRouter Router(SharpCoreAgent.Domain.Workspace workspace) =>
        new([new CSharpCodeIntelligence(workspace)]);
}
