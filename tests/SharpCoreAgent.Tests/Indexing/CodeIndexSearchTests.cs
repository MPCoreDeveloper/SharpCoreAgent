using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Indexing.Ranking;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The whole search path through the adapter: two legs, fusion, ranking and the budget, answering from one snapshot.
/// </summary>
public sealed class CodeIndexSearchTests
{
    [Fact]
    public async Task SearchAnswersFromTheBuiltIndex()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);
        await BuildAsync(sandbox);

        using var index = SharpCoreDbCodeIndex.For(sandbox.Workspace);
        var blocks = index.Search(
            CodeQuery.Create("Circumference", k: 5),
            DateTimeOffset.UnixEpoch,
            EvidenceBudget.DefaultMaxChars);

        Assert.NotEmpty(blocks);
        Assert.Equal("src/Geometry/Circle.cs", blocks[0].Path);
        Assert.Equal("Demo.Geometry.Circle.Circumference", blocks[0].Symbol);
        Assert.True(blocks[0].Score > 0);
    }

    [Fact]
    public void SearchWithoutAnIndexIsEmpty()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        using var index = SharpCoreDbCodeIndex.For(sandbox.Workspace);

        Assert.Empty(index.Search(CodeQuery.Create("Circle"), DateTimeOffset.UnixEpoch, EvidenceBudget.DefaultMaxChars));
    }

    [Fact]
    public async Task SearchRespectsTheCharacterBudget()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);
        await BuildAsync(sandbox);

        using var index = SharpCoreDbCodeIndex.For(sandbox.Workspace);
        var blocks = index.Search(CodeQuery.Create("circle radius area shape"), DateTimeOffset.UnixEpoch, maxChars: 200);

        Assert.True(blocks.Count <= 2);
    }

    [Fact]
    public async Task FindDocsAnswersFromTheBuiltIndex()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);
        await BuildAsync(sandbox);

        using var index = SharpCoreDbCodeIndex.For(sandbox.Workspace);
        var docs = await index.FindDocsAsync("circle notes", 10, TestContext.Current.CancellationToken);

        Assert.NotEmpty(docs);
        Assert.Equal("docs/notes.md", docs[0].Path);
        Assert.Equal("Circle notes", docs[0].Heading);
    }

    [Fact]
    public async Task FindSymbolsRespectsPathAndLanguageFilters()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);
        await BuildAsync(sandbox);

        using var index = SharpCoreDbCodeIndex.For(sandbox.Workspace);
        var byPath = await index.FindSymbolsAsync(
            "Circle", null, "src/Geometry/Circle.cs", null, null, TestContext.Current.CancellationToken);
        var byForeignLanguage = await index.FindSymbolsAsync(
            "Circle", null, null, "python", null, TestContext.Current.CancellationToken);

        Assert.NotEmpty(byPath);
        Assert.All(byPath, symbol => Assert.Equal("src/Geometry/Circle.cs", symbol.Path));
        Assert.Empty(byForeignLanguage);
    }

    [Fact]
    public async Task FindCuesScansTheIndexedSource()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);
        await BuildAsync(sandbox);

        using var index = SharpCoreDbCodeIndex.For(sandbox.Workspace);
        var routes = await index.FindCuesAsync(CueKinds.Route, null, 10, TestContext.Current.CancellationToken);
        var databases = await index.FindCuesAsync(CueKinds.DatabaseCall, null, 10, TestContext.Current.CancellationToken);

        Assert.Contains(routes, cue => cue.Detail == "/users");
        Assert.Contains(databases, cue => cue.Target == "db.query");
    }

    private const string CircleSource = """
        namespace Demo.Geometry;

        public sealed class Circle
        {
            private readonly double _radius;

            public double Circumference() => 2 * 3.14 * _radius;
        }
        """;

    private const string SquareSource = """
        namespace Demo.Geometry;

        public sealed class Square
        {
            public double Side { get; init; }
        }
        """;

    private const string NotesSource = """
        # Circle notes

        The Circle type is immutable.
        """;

    private const string AppSource = """
        const app = express();

        app.get('/users', requireAuth);
        db.query('select * from users');
        describe('users', () => {});
        """;

    private static void WriteCorpus(ToolTestWorkspace sandbox)
    {
        sandbox.Write("src/Geometry/Circle.cs", CircleSource);
        sandbox.Write("src/Geometry/Square.cs", SquareSource);
        sandbox.Write("docs/notes.md", NotesSource);
        sandbox.Write("src/app.js", AppSource);
    }

    private static async Task BuildAsync(ToolTestWorkspace sandbox)
    {
        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        var report = await maintainer.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(IndexFreshness.Fresh, report.Status.Freshness);
    }
}
