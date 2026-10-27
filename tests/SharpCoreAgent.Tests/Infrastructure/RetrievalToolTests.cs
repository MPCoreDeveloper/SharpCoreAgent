using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The five retrieval tools: what they say when the index cannot answer, and the exact shape of the answer when it can.
/// </summary>
/// <remarks>
/// Each test drives one tool through <see cref="ITool.ExecuteAsync"/> with a fake <see cref="ICodeIndex"/>, which is the
/// whole point of the interface: the tool's job is wording and validation, and both are asserted without a database, a
/// build or a workspace tree. The assertions name the pieces of a line rather than a whole transcript, so a change to
/// the order of blocks fails one test instead of five.
/// </remarks>
public sealed class RetrievalToolTests : IDisposable
{
    private const string Root = "src/SharpCoreAgent.Domain/Index/CodeContext.cs";

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "sca-tools-" + Guid.NewGuid().ToString("N"));

    /// <summary>Initialises the test with an empty workspace directory the tools may resolve paths against.</summary>
    public RetrievalToolTests() => Directory.CreateDirectory(_workspace);

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory must never fail a test run.
        }
    }

    [Fact]
    public async Task SearchCodeSaysTheIndexIsMissing()
    {
        var index = new FakeIndex { Status = FakeIndex.StatusView(IndexFreshness.Missing) };

        var result = await Search(index, """{"query":"neighbourhood"}""");

        Assert.True(result.IsError);
        Assert.Equal(CodeIndexAccess.MissingMessage, result.Content);
        Assert.Equal(0, index.Searches);
    }

    [Fact]
    public async Task SearchCodeSaysTheIndexIsDisabled()
    {
        var result = await Search(null, """{"query":"neighbourhood"}""");

        Assert.True(result.IsError);
        Assert.Equal(CodeIndexAccess.DisabledMessage, result.Content);
    }

    [Fact]
    public async Task SearchCodeReportsTheCountAskedForAndTheLegsThatFoundIt()
    {
        var index = new FakeIndex
        {
            Blocks =
            [
                Block(Root, 10, 12, "method", "SharpCoreAgent.Domain.Index.CodeContext.Empty", ["lexical", "vector"]),
            ],
        };

        var result = await Search(index, """{"query":"empty context","k":3}""");

        Assert.False(result.IsError);
        Assert.Contains("found 1 block (asked 3) · index fresh · 0 stale · lexical+vector+graph", result.Content, StringComparison.Ordinal);
        Assert.Contains("1) src/SharpCoreAgent.Domain/Index/CodeContext.cs:10-12 · method · SharpCoreAgent.Domain.Index.CodeContext.Empty · sha256:abcd1234 · why: lexical,vector", result.Content, StringComparison.Ordinal);
        Assert.Contains("  10│public static CodeContext Empty(string target)", result.Content, StringComparison.Ordinal);
        Assert.Equal(3, index.LastQuery?.K);
    }

    [Fact]
    public async Task SearchCodeRefusesAPathOutsideTheWorkspace()
    {
        var index = new FakeIndex();

        var result = await Search(index, """{"query":"x","path":"../outside"}""");

        Assert.True(result.IsRefused);
        Assert.Contains("outside the workspace root", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, index.Searches);
    }

    [Fact]
    public async Task SearchCodeSaysWhenNothingMatched()
    {
        var index = new FakeIndex();

        var result = await Search(index, """{"query":"nothing at all"}""");

        Assert.False(result.IsError);
        Assert.Equal("no blocks matched 'nothing at all' in the index (412 files, 1 908 passages)", result.Content);
    }

    [Fact]
    public async Task FindSymbolCountsTheMatchTiers()
    {
        var index = new FakeIndex
        {
            Symbols =
            [
                Symbol("Turn", "SharpCoreAgent.Application.AgentLoop.Turn", "method", callers: 2, callees: 7),
                Symbol("TURN", "SharpCoreAgent.Application.Other.TURN", "method", callers: 0, callees: 3),
            ],
        };

        var result = await Execute(new FindSymbolTool(index), """{"name":"Turn","max_results":5}""");

        Assert.Contains("2 symbols for 'Turn' (exact 1, ignore-case 1, partial 0)", result.Content, StringComparison.Ordinal);
        Assert.Contains("1) SharpCoreAgent.Application.AgentLoop.Turn · method · " + Root + ":10-12", result.Content, StringComparison.Ordinal);
        Assert.Contains("   SharpCoreAgent.Application.AgentLoop · callers 2 · callees 7 · community c4", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSymbolSaysWhenTheNameIsUnknown()
    {
        var index = new FakeIndex();

        var result = await Execute(new FindSymbolTool(index), """{"name":"xyz"}""");

        Assert.False(result.IsError);
        Assert.Equal("no symbol named 'xyz' (searched 1 122 declarations)", result.Content);
    }

    [Fact]
    public async Task FindReferencesGroupsByFileAndKeepsFileOrderOrdinal()
    {
        var index = new FakeIndex
        {
            Symbols = [Symbol("TurnAsync", "SharpCoreAgent.Application.AgentLoop.TurnAsync", "method", callers: 2, callees: 7)],
            Blocks =
            [
                Block("src/b/Two.cs", 40, 42, "method", "B.Run", ["graph"], relation: "calls"),
                Block("src/a/One.cs", 10, 12, "method", "A.Run", ["graph"], relation: "calls"),
            ],
        };

        var result = await Execute(new FindReferencesTool(index), """{"symbol":"TurnAsync"}""");

        Assert.StartsWith("2 callers of SharpCoreAgent.Application.AgentLoop.TurnAsync · 2 files · 2 listed", result.Content, StringComparison.Ordinal);
        Assert.True(
            result.Content.IndexOf("src/a/One.cs", StringComparison.Ordinal) <
            result.Content.IndexOf("src/b/Two.cs", StringComparison.Ordinal),
            "file headings must be in ordinal path order");
        Assert.Contains("· rel calls · sha256:abcd1234", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindReferencesRejectsAnUnknownDirection()
    {
        var tool = new FindReferencesTool(new FakeIndex());

        await Assert.ThrowsAsync<ToolArgumentException>(async () =>
        {
            await Execute(tool, """{"symbol":"X","direction":"sideways"}""");
        });
    }

    [Fact]
    public async Task CodeContextRendersTheNeighbourhoodAndTheFocus()
    {
        var focus = Block(Root, 20, 22, "method", "SharpCoreAgent.Domain.Index.CodeContext.Empty", ["symbol"]);
        var neighbour = Block(Root, 5, 6, "method", "SharpCoreAgent.Domain.Index.CodeContext.MinWeight", ["graph"], relation: "calls", score: 0.83);

        var index = new FakeIndex
        {
            Context = new CodeContext
            {
                Target = "Empty",
                Subject = Symbol("Empty", "SharpCoreAgent.Domain.Index.CodeContext.Empty", "method", callers: 1, callees: 0, startLine: 52, endLine: 55),
                Declarations = [Symbol("Empty", "SharpCoreAgent.Domain.Index.CodeContext.Empty", "method", callers: 1, callees: 0, startLine: 52, endLine: 55)],
                Focus = focus,
                Neighbours = [neighbour],
                Considered = 26,
                MinWeight = 0.2,
            },
        };

        var result = await Execute(new CodeContextTool(index), """{"target":"CodeContext.cs"}""");

        Assert.Contains("CodeContext.cs · 1 declaration", result.Content, StringComparison.Ordinal);
        Assert.Contains("neighbours (hops 1, 1 shown of 26, min weight 0.20)", result.Content, StringComparison.Ordinal);
        Assert.Contains("calls", result.Content, StringComparison.Ordinal);
        Assert.Contains(" · w 0.83", result.Content, StringComparison.Ordinal);
        Assert.Contains("  20│public static CodeContext Empty(string target)", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodeContextSaysWhenTheTargetMatchesNothing()
    {
        var result = await Execute(new CodeContextTool(new FakeIndex()), """{"target":"Nowhere.AtAll"}""");

        Assert.False(result.IsError);
        Assert.Equal("nothing in the index matches 'Nowhere.AtAll' (try find_symbol or search_code)", result.Content);
    }

    [Fact]
    public async Task IndexStatusPrintsTheCompactThreeLineForm()
    {
        var result = await Execute(new IndexStatusTool(new FakeIndex()), "{}");

        var lines = result.Content.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("state fresh · tier lean · embedder hashed-local/512 · dimensions 512 · built 2026-10-24T09:12:44Z", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("files 412 (0 stale, 3 skipped) · passages 1 908 · symbols 1 122 · edges 2 640 · size ", lines[1], StringComparison.Ordinal);
        Assert.EndsWith(" · workspace workspace-root", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task IndexStatusTreatsAMissingIndexAsAnAnswer()
    {
        var index = new FakeIndex { Status = FakeIndex.StatusView(IndexFreshness.Missing) };

        var result = await Execute(new IndexStatusTool(index), "{}");

        Assert.False(result.IsError);
        Assert.Equal("state missing · no index at .sharpcoreagent/index; run `sharpcoreagent index`", result.Content);
    }

    [Theory]
    [InlineData("search_code")]
    [InlineData("find_symbol")]
    [InlineData("find_references")]
    [InlineData("code_context")]
    [InlineData("index_status")]
    public void EveryRetrievalToolIsReadOnly(string name)
    {
        var index = new FakeIndex();
        ITool tool = name switch
        {
            "search_code" => new SearchCodeTool(index),
            "find_symbol" => new FindSymbolTool(index),
            "find_references" => new FindReferencesTool(index),
            "code_context" => new CodeContextTool(index),
            _ => new IndexStatusTool(index),
        };

        Assert.Equal(name, tool.Definition.Name);
        Assert.Equal(ToolSafety.ReadOnly, tool.Safety);
        Assert.Contains("\"type\":\"object\"", tool.Definition.ParametersSchemaJson, StringComparison.Ordinal);
    }

    private Task<ToolResult> Search(ICodeIndex? index, string arguments) => Execute(new SearchCodeTool(index), arguments);

    private async Task<ToolResult> Execute(ITool tool, string arguments) => await tool.ExecuteAsync(
        new ToolCall("call-1", tool.Definition.Name, arguments),
        new ToolInvocationContext(Workspace.Create(_workspace)),
        CancellationToken.None);

    private static CodeEvidence Block(
        string path,
        int start,
        int end,
        string kind,
        string? symbol,
        string[] legs,
        string? relation = null,
        double score = 0) => new()
        {
            Path = path,
            StartLine = start,
            EndLine = end,
            Sha8 = "abcd1234",
            Text = "public static CodeContext Empty(string target)",
            Kind = kind,
            Symbol = symbol,
            Relation = relation,
            Score = score,
            Legs = [.. legs.Select(Leg)],
        };

    private static EvidenceLeg Leg(string leg) => leg switch
    {
        "lexical" => EvidenceLeg.Lexical,
        "vector" => EvidenceLeg.Vector,
        "graph" => EvidenceLeg.Graph,
        "symbol" => EvidenceLeg.Symbol,
        _ => EvidenceLeg.Path,
    };

    private static CodeSymbolRef Symbol(
        string name,
        string qualified,
        string kind,
        int callers,
        int callees,
        int startLine = 10,
        int endLine = 12) => new()
        {
            Key = string.Concat(qualified, "#", kind),
            Name = name,
            QualifiedName = qualified,
            Kind = kind,
            Path = Root,
            StartLine = startLine,
            EndLine = endLine,
            Signature = "public static CodeContext Empty(string target)",
            Community = "c4",
            Callers = callers,
            Callees = callees,
        };

    /// <summary>
    /// A stand-in for the index that answers with whatever a test hands it.
    /// </summary>
    /// <remarks>
    /// Every member returns the value it was given, so a test can assert the tool's wording without a database; the
    /// counters exist to prove a tool stopped before querying when it should have.
    /// </remarks>
    private sealed class FakeIndex : ICodeIndex
    {
    public static IndexStatus StatusView(IndexFreshness freshness) => new()
    {
        WorkspaceRoot = "workspace-root",
        Freshness = freshness,
        Tier = IndexTier.Lean,
        Embedder = "hashed-local/512",
        Dimensions = 512,
        Files = 412,
        Chunks = 1_908,
        Symbols = 1_122,
        Edges = 2_640,
        StaleFiles = 0,
        SkippedFiles = 3,
        OrphanRows = 0,
        VectorSearchEnabled = true,
        LexicalFallback = false,
        BuiltUtc = new DateTimeOffset(2026, 10, 24, 9, 12, 44, TimeSpan.Zero),
        SizeBytes = 14_889_779,
    };

        public IndexStatus Status { get; set; } = FakeIndex.StatusView(IndexFreshness.Fresh);

        public IReadOnlyList<CodeEvidence> Blocks { get; set; } = [];

        public IReadOnlyList<CodeSymbolRef> Symbols { get; set; } = [];

        public CodeContext? Context { get; set; }

        public int Searches { get; private set; }

        public CodeQuery? LastQuery { get; private set; }

        public ValueTask<IndexStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Status);

        public ValueTask<IReadOnlyList<CodeEvidence>> SearchAsync(CodeQuery query, CancellationToken cancellationToken)
        {
            Searches++;
            LastQuery = query;
            return ValueTask.FromResult(Blocks);
        }

        public ValueTask<IReadOnlyList<CodeSymbolRef>> FindSymbolsAsync(
            string name,
            string? kind,
            CancellationToken cancellationToken) => ValueTask.FromResult(Symbols);

        public ValueTask<IReadOnlyList<CodeEvidence>> FindReferencesAsync(
            string symbol,
            ReferenceDirection direction,
            int maxResults,
            CancellationToken cancellationToken) => ValueTask.FromResult(Blocks);

        public ValueTask<CodeContext> GetContextAsync(
            string target,
            int hops,
            int k,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Context is null ? CodeContext.Empty(target) : Context with { Target = target });
    }
}
