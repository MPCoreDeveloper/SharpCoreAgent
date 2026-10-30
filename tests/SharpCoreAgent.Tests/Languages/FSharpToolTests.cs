using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Code;
using SharpCoreAgent.Infrastructure.Code;
using SharpCoreAgent.Infrastructure.Tools;
using SharpCoreAgent.Languages.FSharp;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Languages;

/// <summary>
/// The three tools against the real F# adapter, over the router, on the fixture project.
/// </summary>
/// <remarks>
/// This is the seam each of those pieces was written for: the tools are handed ports and never a language, the router
/// picks the adapter from the extension alone, and the adapter type-checks the project it was handed. A bug in the
/// wiring of any one of them — a port registered against a different instance, a path relative to the wrong root —
/// shows up here and nowhere else.
/// </remarks>
public sealed class FSharpToolTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FileDiagnosticsReportsTheCompilerErrorThroughTheRouter()
    {
        using var sandbox = FsSandbox.Create();
        var tool = new FileDiagnosticsTool(Router(sandbox.Workspace));
        var (line, column) = FsSandbox.PositionOf(FsSandbox.ProgramSource, "Subtotal");

        var result = await tool.ExecuteAsync(
            ToolTestWorkspace.Call(FileDiagnosticsTool.Name, ToolTestWorkspace.Args(new { path = "src/Program.fs" })),
            sandbox.Context,
            Token);

        Assert.False(result.IsError);
        Assert.Contains("src/Program.fs · 1 error, 0 warnings, 0 info", result.Content, StringComparison.Ordinal);
        Assert.Contains(
            $"src/Program.fs:{line}:{column} · error FS0039",
            result.Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsesAtFindsTheDeclarationAndTheCallAcrossFiles()
    {
        using var sandbox = FsSandbox.Create();
        var tool = new UsesAtTool(Router(sandbox.Workspace));
        var (line, column) = FsSandbox.PositionOf(FsSandbox.ProgramSource, "(total");

        var result = await tool.ExecuteAsync(
            ToolTestWorkspace.Call(
                UsesAtTool.Name,
                ToolTestWorkspace.Args(new { path = "src/Program.fs", line, column = column + 1 })),
            sandbox.Context,
            Token);

        Assert.False(result.IsError);
        Assert.Contains("total at src/Program.fs", result.Content, StringComparison.Ordinal);
        Assert.Contains("· 2 uses · 2 files", result.Content, StringComparison.Ordinal);
        Assert.Contains("definition src/Cart.fs:", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignatureAtShowsTheArgumentAndMarksItActive()
    {
        using var sandbox = FsSandbox.Create();
        var tool = new SignatureAtTool(Router(sandbox.Workspace));
        var (line, column) = FsSandbox.PositionOf(FsSandbox.ProgramSource, "total");

        var result = await tool.ExecuteAsync(
            ToolTestWorkspace.Call(
                SignatureAtTool.Name,
                ToolTestWorkspace.Args(new { path = "src/Program.fs", line, column = column + "total".Length })),
            sandbox.Context,
            Token);

        Assert.False(result.IsError);
        Assert.Contains("1 signature · active 1 of 1", result.Content, StringComparison.Ordinal);
        Assert.Contains("items: Item list ← active", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllThreeToolsSayWhenNoLanguageClaimsTheFile()
    {
        using var sandbox = FsSandbox.Create();
        var router = Router(sandbox.Workspace);

        var diagnostics = await new FileDiagnosticsTool(router).ExecuteAsync(
            ToolTestWorkspace.Call(FileDiagnosticsTool.Name, ToolTestWorkspace.Args(new { path = "src/Program.cs" })),
            sandbox.Context,
            Token);
        var uses = await new UsesAtTool(router).ExecuteAsync(
            ToolTestWorkspace.Call(UsesAtTool.Name, ToolTestWorkspace.Args(new { path = "src/Program.cs", line = 1, column = 0 })),
            sandbox.Context,
            Token);
        var signature = await new SignatureAtTool(router).ExecuteAsync(
            ToolTestWorkspace.Call(SignatureAtTool.Name, ToolTestWorkspace.Args(new { path = "src/Program.cs", line = 1, column = 0 })),
            sandbox.Context,
            Token);

        Assert.True(diagnostics.IsError);
        Assert.StartsWith("LANG001: No code intelligence is registered for .cs files", diagnostics.Content, StringComparison.Ordinal);
        Assert.StartsWith("LANG001: ", uses.Content, StringComparison.Ordinal);
        Assert.StartsWith("LANG001: ", signature.Content, StringComparison.Ordinal);
    }

    private static LanguageRouter Router(Workspace workspace) =>
        new([new FsCodeIntelligence(workspace)]);
}
