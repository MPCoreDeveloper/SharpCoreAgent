using SharpCoreAgent.Domain.Code;
using SharpCoreAgent.Languages.CSharp;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Languages;

/// <summary>The C# adapter against a real two-project source graph.</summary>
public sealed class CSharpCodeIntelligenceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Program.cs")]
    [InlineData(@"src\Program.cs")]
    public void SupportsClaimsCSharpSources(string path)
    {
        using var sandbox = CsSandbox.Create();

        Assert.True(new CSharpCodeIntelligence(sandbox.Workspace).Supports(path));
    }

    [Theory]
    [InlineData("Program.fs")]
    [InlineData("script.csx")]
    [InlineData("README.md")]
    public void SupportsDeclinesEverythingElse(string path)
    {
        using var sandbox = CsSandbox.Create();

        Assert.False(new CSharpCodeIntelligence(sandbox.Workspace).Supports(path));
    }

    [Fact]
    public async Task DiagnosticsReportTheCompilerErrorAndNothingForACleanFile()
    {
        using var sandbox = CsSandbox.Create();
        var intelligence = new CSharpCodeIntelligence(sandbox.Workspace);
        var (line, column) = CsSandbox.PositionOf(CsSandbox.ProgramSource, "Subtotal");

        var broken = await intelligence.GetDiagnosticsAsync("App/Program.cs", Token);
        var clean = await intelligence.GetDiagnosticsAsync("Cart/Cart.cs", Token);

        Assert.Null(broken.Failure);
        var diagnostic = Assert.Single(broken.Diagnostics);
        Assert.Equal("CS0103", diagnostic.Id);
        Assert.Equal(CodeSeverity.Error, diagnostic.Severity);
        Assert.Equal("App/Program.cs", diagnostic.Path);
        Assert.Equal(line, diagnostic.Line);
        Assert.Equal(column, diagnostic.Column);
        Assert.Contains("Subtotal", diagnostic.Message, StringComparison.Ordinal);
        Assert.Null(clean.Failure);
        Assert.Empty(clean.Diagnostics);
    }

    [Fact]
    public async Task UsesAreCompilerBoundAcrossTheProjectReference()
    {
        using var sandbox = CsSandbox.Create();
        var intelligence = new CSharpCodeIntelligence(sandbox.Workspace);
        var (line, column) = CsSandbox.PositionOf(CsSandbox.ProgramSource, "Total(prices, tax");

        var result = await intelligence.FindUsesAsync("App/Program.cs", line, column, Token);
        var repeated = await intelligence.FindUsesAsync("App/Program.cs", line, column, Token);

        Assert.Null(result.Failure);
        Assert.Equal(2, result.Uses.Count);
        Assert.Single(result.Uses, use => use.IsDefinition);
        Assert.Equal("Cart.cs", Path.GetFileName(result.Uses.Single(use => use.IsDefinition).Path));
        var use = Assert.Single(result.Uses, use => !use.IsDefinition);
        Assert.Equal("App/Program.cs", use.Path);
        Assert.Equal("Total", use.Display);
        Assert.DoesNotContain(result.Uses, use => use.Path.EndsWith("Program.cs", StringComparison.Ordinal) && use.Line == 4);
        Assert.Equal(result.Uses, repeated.Uses);
    }

    [Fact]
    public async Task ASourceEditIsVisibleToTheNextDiagnosticsRequest()
    {
        using var sandbox = CsSandbox.Create();
        var intelligence = new CSharpCodeIntelligence(sandbox.Workspace);
        var before = await intelligence.GetDiagnosticsAsync("App/Program.cs", Token);

        sandbox.Write("App/Program.cs", CsSandbox.ProgramSource.Replace(
            "Console.WriteLine(Subtotal(prices));",
            "Console.WriteLine(prices.Length);",
            StringComparison.Ordinal));
        var after = await intelligence.GetDiagnosticsAsync("App/Program.cs", Token);

        Assert.Equal("CS0103", Assert.Single(before.Diagnostics).Id);
        Assert.Null(after.Failure);
        Assert.DoesNotContain(after.Diagnostics, diagnostic => diagnostic.Id == "CS0103");
    }
}
