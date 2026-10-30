using SharpCoreAgent.Domain.Code;
using SharpCoreAgent.Infrastructure.Code;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The router: which adapter answers, and what a model is told when none can.
/// </summary>
/// <remarks>
/// The adapters here are fakes because the question is the choice, not the compiler: a real F# project would make these
/// tests slow and would prove nothing about the two codes the router owns. The end-to-end proof lives in
/// <see cref="Languages.FSharpToolTests"/>.
/// </remarks>
public sealed class LanguageRouterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheAdapterThatClaimsThePathAnswers()
    {
        var fsharp = new FakeAdapter(".fs", "FS0039");
        var router = new LanguageRouter([fsharp, new FakeAdapter(".cs", "CS1002")]);

        var result = await router.GetDiagnosticsAsync("src/Program.fs", Token);

        Assert.Null(result.Failure);
        Assert.Equal("FS0039", Assert.Single(result.Diagnostics).Id);
        Assert.Equal(1, fsharp.Asked);
    }

    [Fact]
    public async Task APathNoAdapterClaimsIsAFailureRatherThanAnException()
    {
        var router = new LanguageRouter([new FakeAdapter(".fs", "FS0039")]);

        var result = await router.GetDiagnosticsAsync("tools/build.py", Token);

        Assert.Equal("LANG001", result.Failure?.Code);
        Assert.Contains(".py", result.Failure?.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("dotnet_verify", result.Failure?.Hint ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task AnAdapterThatDoesNotImplementThePortIsNamedInTheAnswer()
    {
        // A registration mistake, not a workspace problem: the router says which port the adapter lacks, because
        // "nothing claims this file" would send a developer looking in the wrong place.
        var router = new LanguageRouter([new ClaimOnlyAdapter()]);

        var diagnostics = await router.GetDiagnosticsAsync("src/Program.fs", Token);
        var uses = await router.FindUsesAsync("src/Program.fs", 1, 0, Token);
        var signature = await router.GetSignatureAsync("src/Program.fs", 1, 0, Token);

        Assert.Equal("LANG002", diagnostics.Failure?.Code);
        Assert.Contains(nameof(ICodeDiagnostics), diagnostics.Failure?.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("LANG002", uses.Failure?.Code);
        Assert.Contains(nameof(ISymbolUses), uses.Failure?.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("LANG002", signature.Failure?.Code);
        Assert.Contains(nameof(ISignatureHelp), signature.Failure?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostWithNoAdaptersStillAnswers()
    {
        var router = new LanguageRouter([]);

        var result = await router.GetSignatureAsync("src/Program.fs", 3, 8, Token);

        Assert.Equal("LANG001", result.Failure?.Code);
        Assert.Empty(result.Signatures);
    }

    [Fact]
    public async Task TheFirstAdapterThatClaimsThePathWins()
    {
        // Order is the registration order, so a second adapter that also claims .fs is a configuration mistake with a
        // stable outcome rather than a race.
        var first = new FakeAdapter(".fs", "FS0001");
        var second = new FakeAdapter(".fs", "FS0002");
        var router = new LanguageRouter([first, second]);

        var result = await router.GetDiagnosticsAsync("src/Program.fs", Token);

        Assert.Equal("FS0001", Assert.Single(result.Diagnostics).Id);
        Assert.Equal(0, second.Asked);
    }

    [Fact]
    public async Task AnEmptyPathIsRejectedBeforeAnyAdapterIsAsked() =>
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await new LanguageRouter([new FakeAdapter(".fs", "FS0039")]).GetDiagnosticsAsync(" ", Token));
    /// <summary>An adapter that answers every question, so a test can see which one was chosen.</summary>
    private sealed class FakeAdapter : ILanguageAdapter, ICodeDiagnostics, ISymbolUses, ISignatureHelp
    {
        private readonly string _id;

        public FakeAdapter(string extension, string id)
        {
            Extension = extension;
            _id = id;
        }

        public string Extension { get; }

        public int Asked { get; private set; }

        public bool Supports(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

        public ValueTask<CodeDiagnosticsResult> GetDiagnosticsAsync(string path, CancellationToken cancellationToken)
        {
            Asked++;
            return ValueTask.FromResult(CodeDiagnosticsResult.Ok(
            [
                new CodeDiagnostic
                {
                    Severity = CodeSeverity.Error,
                    Id = _id,
                    Path = path,
                    Line = 1,
                    Column = 0,
                    Message = "reported by " + Extension,
                },
            ]));
        }

        public ValueTask<SymbolUsesResult> FindUsesAsync(
            string path,
            int line,
            int column,
            CancellationToken cancellationToken)
        {
            Asked++;
            return ValueTask.FromResult(SymbolUsesResult.Ok(
            [
                new SymbolUse { Path = path, Line = line, Column = column, IsDefinition = true, Display = "symbol" },
            ]));
        }

        public ValueTask<SignatureHelpResult> GetSignatureAsync(
            string path,
            int line,
            int column,
            CancellationToken cancellationToken)
        {
            Asked++;
            return ValueTask.FromResult(SignatureHelpResult.Ok(
            [
                new SignatureInfo { Display = "val f: int -> int", ActiveParameter = 0 },
            ], 0));
        }
    }

    /// <summary>An adapter that claims a file without implementing any of the three ports.</summary>
    private sealed class ClaimOnlyAdapter : ILanguageAdapter
    {
        public bool Supports(string path) => path.EndsWith(".fs", StringComparison.OrdinalIgnoreCase);
    }
}
