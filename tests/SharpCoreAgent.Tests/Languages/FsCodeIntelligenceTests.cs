using SharpCoreAgent.Domain.Code;
using SharpCoreAgent.Languages.FSharp;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Languages;

/// <summary>
/// What the F# adapter answers, against a real project on a real file system.
/// </summary>
/// <remarks>
/// No file in the fixture is faked: the checker parses it, resolves FSharp.Core from the restore file and reports
/// what a compiler would. The only thing the fixture avoids is the NuGet round-trip, which is not what these tests
/// are about.
/// </remarks>
public sealed class FsCodeIntelligenceTests
{
    [Theory]
    [InlineData("Program.fs")]
    [InlineData("src/Cart.fsi")]
    [InlineData(@"src\Cart.fs")]
    public void SupportsClaimsFSharpSources(string path)
    {
        using var sandbox = FsSandbox.Create();
        var intelligence = new FsCodeIntelligence(sandbox.Workspace);

        Assert.True(intelligence.Supports(path));
    }

    [Theory]
    [InlineData("Program.cs")]
    [InlineData("readme.md")]
    [InlineData("script.fsx")]
    public void SupportsDeclinesEverythingElse(string path)
    {
        using var sandbox = FsSandbox.Create();
        var intelligence = new FsCodeIntelligence(sandbox.Workspace);

        Assert.False(intelligence.Supports(path));
    }

    [Fact]
    public async Task GetDiagnosticsAsyncReportsTheCompilerErrorForOneFile()
    {
        using var sandbox = FsSandbox.Create();
        var intelligence = new FsCodeIntelligence(sandbox.Workspace);
        var (line, column) = FsSandbox.PositionOf(FsSandbox.ProgramSource, "Subtotal");

        var result = await intelligence.GetDiagnosticsAsync("src/Program.fs", TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("FS0039", diagnostic.Id);
        Assert.Equal(CodeSeverity.Error, diagnostic.Severity);
        Assert.Equal("src/Program.fs", diagnostic.Path.Replace('\\', '/'));
        Assert.Equal(line, diagnostic.Line);
        Assert.Equal(column, diagnostic.Column);
        Assert.Contains("Subtotal", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDiagnosticsAsyncReportsNothingForTheCleanFile()
    {
        using var sandbox = FsSandbox.Create();
        var intelligence = new FsCodeIntelligence(sandbox.Workspace);

        var result = await intelligence.GetDiagnosticsAsync("src/Cart.fs", TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task FindUsesAsyncFindsTheDeclarationAndTheCrossFileUse()
    {
        using var sandbox = FsSandbox.Create();
        var intelligence = new FsCodeIntelligence(sandbox.Workspace);
        var (line, column) = FsSandbox.PositionOf(FsSandbox.ProgramSource, "(total");

        var result = await intelligence.FindUsesAsync(
            "src/Program.fs",
            line,
            column + "(".Length,
            TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
        Assert.Equal(2, result.Uses.Count);
        var definition = Assert.Single(result.Uses, use => use.IsDefinition);
        Assert.Equal("Cart.fs", Path.GetFileName(definition.Path));
        var call = Assert.Single(result.Uses, use => !use.IsDefinition);
        Assert.Equal("Program.fs", Path.GetFileName(call.Path));
        Assert.Equal("total", call.Display);
        Assert.Equal(line, call.Line);
    }

    [Fact]
    public async Task GetSignatureAsyncReturnsTheDeclarationAndItsArgument()
    {
        using var sandbox = FsSandbox.Create();
        var intelligence = new FsCodeIntelligence(sandbox.Workspace);
        var (line, column) = FsSandbox.PositionOf(FsSandbox.ProgramSource, "total");

        var result = await intelligence.GetSignatureAsync(
            "src/Program.fs",
            line,
            column + "total".Length,
            TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
        var signature = Assert.Single(result.Signatures);
        Assert.Contains("total", signature.Display, StringComparison.Ordinal);
        Assert.Contains("Item list", signature.Display, StringComparison.Ordinal);
        Assert.Equal("items: Item list", Assert.Single(signature.Parameters));
        Assert.Equal(0, signature.ActiveParameter);
        Assert.Equal(0, result.ActiveSignature);
    }

    [Fact]
    public async Task GetDiagnosticsAsyncAsksForARootWhenTheWorkspaceHasNoProject()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("Program.fs", FsSandbox.ProgramSource);
        var intelligence = new FsCodeIntelligence(sandbox.Workspace);

        var result = await intelligence.GetDiagnosticsAsync("Program.fs", TestContext.Current.CancellationToken);

        Assert.Equal("FSH001", result.Failure?.Code);
        Assert.Contains("list_roots", result.Failure?.Hint ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task GetDiagnosticsAsyncAsksForABuildWhenTheProjectWasNeverRestored()
    {
        using var sandbox = FsSandbox.Create(restored: false);
        var intelligence = new FsCodeIntelligence(sandbox.Workspace);

        var result = await intelligence.GetDiagnosticsAsync("src/Program.fs", TestContext.Current.CancellationToken);

        Assert.Equal("FSH002", result.Failure?.Code);
        Assert.Contains("dotnet_verify", result.Failure?.Hint ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDiagnosticsAsyncDeclinesAFileThisLanguageDoesNotOwn()
    {
        using var sandbox = FsSandbox.Create();
        var intelligence = new FsCodeIntelligence(sandbox.Workspace);

        var result = await intelligence.GetDiagnosticsAsync("src/Program.cs", TestContext.Current.CancellationToken);

        Assert.Equal("FSH004", result.Failure?.Code);
    }
}
