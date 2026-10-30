using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Code;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Tools;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The three code-intelligence tools: the exact shape of an answer, and what they do with one they cannot give.
/// </summary>
/// <remarks>
/// The ports are faked here for the same reason <see cref="RetrievalToolTests"/> fakes the index: the tool's job is
/// validation and wording, and both are asserted without a compiler, a project or a restore. The adapter's own
/// answers are proved against a real F# project in <see cref="Languages.FsCodeIntelligenceTests"/>, and the two
/// together are proved end to end in <see cref="Languages.FSharpToolTests"/>.
/// </remarks>
public sealed class CodeIntelligenceToolTests
{
    private static readonly ToolInvocationContext Context =
        new(Workspace.Create(Directory.GetCurrentDirectory()));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FileDiagnosticsRendersTheCountsAndThePosition()
    {
        var tool = new FileDiagnosticsTool(new FakeDiagnostics
        {
            Result = CodeDiagnosticsResult.Ok(
            [
                Diagnostic(CodeSeverity.Error, "FS0039", "src/Program.fs", 8, 18, "The value or constructor 'Subtotal' is not defined."),
                Diagnostic(CodeSeverity.Warning, "FS0025", "src/Program.fs", 12, 4, "Incomplete pattern matches."),
            ]),
        });

        var result = await tool.ExecuteAsync(
            ToolTestWorkspace.Call(FileDiagnosticsTool.Name, ToolTestWorkspace.Args(new { path = "src/Program.fs" })),
            Context,
            Token);

        Assert.False(result.IsError);
        Assert.Contains("src/Program.fs · 1 error, 1 warning, 0 info", result.Content, StringComparison.Ordinal);
        Assert.Contains(
            "  src/Program.fs:8:18 · error FS0039 · The value or constructor 'Subtotal' is not defined.",
            result.Content,
            StringComparison.Ordinal);
        Assert.Contains("  src/Program.fs:12:4 · warning FS0025", result.Content, StringComparison.Ordinal);
        Assert.Equal(new ToolEffect(ToolEffectKind.Read, "src/Program.fs"), Assert.Single(result.Effects));
    }

    [Fact]
    public async Task FileDiagnosticsSaysNoDiagnosticsForACleanFile()
    {
        var tool = new FileDiagnosticsTool(new FakeDiagnostics());

        var result = await tool.ExecuteAsync(
            ToolTestWorkspace.Call(FileDiagnosticsTool.Name, ToolTestWorkspace.Args(new { path = "src/Cart.fs" })),
            Context,
            Token);

        Assert.False(result.IsError);
        Assert.Equal("src/Cart.fs · no diagnostics", result.Content);
    }

    [Fact]
    public async Task FileDiagnosticsPassesAFailureThroughWithItsCodeAndHint()
    {
        var tool = new FileDiagnosticsTool(new FakeDiagnostics
        {
            Result = CodeDiagnosticsResult.Failed(new CodeFailure
            {
                Code = "FSH002",
                Message = "The project has no assets file.",
                Hint = "Run dotnet_verify kind=build.",
            }),
        });

        var result = await tool.ExecuteAsync(
            ToolTestWorkspace.Call(FileDiagnosticsTool.Name, ToolTestWorkspace.Args(new { path = "src/Program.fs" })),
            Context,
            Token);

        Assert.True(result.IsError);
        Assert.Equal("FSH002: The project has no assets file.\nnext: Run dotnet_verify kind=build.", result.Content);
    }

    [Fact]
    public async Task FileDiagnosticsRejectsACallWithoutAPath() =>
        await Assert.ThrowsAsync<ToolArgumentException>(async () => await new FileDiagnosticsTool(new FakeDiagnostics())
            .ExecuteAsync(ToolTestWorkspace.Call(FileDiagnosticsTool.Name, "{}"), Context, Token));

    [Fact]
    public async Task UsesAtRendersTheDeclarationAndTheUse()
    {
        var tool = new UsesAtTool(new FakeUses
        {
            Result = SymbolUsesResult.Ok(
            [
                new SymbolUse { Path = "src/Cart.fs", Line = 5, Column = 4, IsDefinition = true, Display = "total" },
                new SymbolUse { Path = "src/Program.fs", Line = 8, Column = 18, IsDefinition = false, Display = "total" },
            ]),
        });

        var result = await tool.ExecuteAsync(
            ToolTestWorkspace.Call(
                UsesAtTool.Name,
                ToolTestWorkspace.Args(new { path = "src/Program.fs", line = 8, column = 18 })),
            Context,
            Token);

        Assert.False(result.IsError);
        Assert.Contains("total at src/Program.fs:8:18 · 2 uses · 2 files", result.Content, StringComparison.Ordinal);
        Assert.Contains("\n  definition src/Cart.fs:5:4 · total", result.Content, StringComparison.Ordinal);
        Assert.Contains("\n  use src/Program.fs:8:18 · total", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsesAtRejectsAPositionWithoutALine() =>
        await Assert.ThrowsAsync<ToolArgumentException>(async () => await new UsesAtTool(new FakeUses())
            .ExecuteAsync(
                ToolTestWorkspace.Call(UsesAtTool.Name, ToolTestWorkspace.Args(new { path = "src/Program.fs", column = 4 })),
                Context,
                Token));

    [Fact]
    public async Task UsesAtPassesAFailureThroughWithItsCode()
    {
        var tool = new UsesAtTool(new FakeUses
        {
            Result = SymbolUsesResult.Failed(new CodeFailure
            {
                Code = "FSH003",
                Message = "Nothing nameable is at 1:0.",
                Hint = "Use find_references for a name-based search.",
            }),
        });

        var result = await tool.ExecuteAsync(
            ToolTestWorkspace.Call(
                UsesAtTool.Name,
                ToolTestWorkspace.Args(new { path = "src/Program.fs", line = 1, column = 0 })),
            Context,
            Token);

        Assert.True(result.IsError);
        Assert.StartsWith("FSH003: ", result.Content, StringComparison.Ordinal);
        Assert.Contains("next: Use find_references", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignatureAtMarksTheActiveArgument()
    {
        var tool = new SignatureAtTool(new FakeSignatures
        {
            Result = SignatureHelpResult.Ok(
            [
                new SignatureInfo
                {
                    Display = "val total: items: Item list -> decimal",
                    Parameters = ["items: Item list"],
                    ActiveParameter = 0,
                },
            ], 0),
        });

        var result = await tool.ExecuteAsync(
            ToolTestWorkspace.Call(
                SignatureAtTool.Name,
                ToolTestWorkspace.Args(new { path = "src/Program.fs", line = 8, column = 18 })),
            Context,
            Token);

        Assert.False(result.IsError);
        Assert.Contains("src/Program.fs:8:18 · 1 signature · active 1 of 1", result.Content, StringComparison.Ordinal);
        Assert.Contains("\n  1) val total: items: Item list -> decimal", result.Content, StringComparison.Ordinal);
        Assert.Contains("\n     items: Item list ← active", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignatureAtDrawsNoMarkerWhenThePositionIsOutsideAnArgument()
    {
        var tool = new SignatureAtTool(new FakeSignatures
        {
            Result = SignatureHelpResult.Ok(
            [
                new SignatureInfo
                {
                    Display = "val total: items: Item list -> decimal",
                    Parameters = ["items: Item list"],
                    ActiveParameter = -1,
                },
            ], 0),
        });

        var result = await tool.ExecuteAsync(
            ToolTestWorkspace.Call(
                SignatureAtTool.Name,
                ToolTestWorkspace.Args(new { path = "src/Program.fs", line = 8, column = 18 })),
            Context,
            Token);

        Assert.DoesNotContain("← active", result.Content, StringComparison.Ordinal);
        Assert.Contains("items: Item list", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignatureAtSaysWhenNothingCallableSitsThere()
    {
        var tool = new SignatureAtTool(new FakeSignatures { Result = SignatureHelpResult.Ok([], -1) });

        var result = await tool.ExecuteAsync(
            ToolTestWorkspace.Call(
                SignatureAtTool.Name,
                ToolTestWorkspace.Args(new { path = "src/Program.fs", line = 8, column = 18 })),
            Context,
            Token);

        Assert.False(result.IsError);
        Assert.Equal("src/Program.fs:8:18 · nothing callable at this position", result.Content);
    }

    private static CodeDiagnostic Diagnostic(
        CodeSeverity severity,
        string id,
        string path,
        int line,
        int column,
        string message) => new()
        {
            Severity = severity,
            Id = id,
            Path = path,
            Line = line,
            Column = column,
            Message = message,
        };

    private sealed class FakeDiagnostics : ICodeDiagnostics
    {
        public CodeDiagnosticsResult Result { get; init; } = CodeDiagnosticsResult.Ok([]);

        public ValueTask<CodeDiagnosticsResult> GetDiagnosticsAsync(string path, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result);
    }

    private sealed class FakeUses : ISymbolUses
    {
        public SymbolUsesResult Result { get; init; } = SymbolUsesResult.Ok([]);

        public ValueTask<SymbolUsesResult> FindUsesAsync(
            string path,
            int line,
            int column,
            CancellationToken cancellationToken) => ValueTask.FromResult(Result);
    }

    private sealed class FakeSignatures : ISignatureHelp
    {
        public SignatureHelpResult Result { get; init; } = SignatureHelpResult.Ok([], -1);

        public ValueTask<SignatureHelpResult> GetSignatureAsync(
            string path,
            int line,
            int column,
            CancellationToken cancellationToken) => ValueTask.FromResult(Result);
    }
}
