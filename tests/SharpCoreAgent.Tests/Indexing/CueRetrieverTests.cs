using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing.Retrieval;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The cue scan over a hand-built snapshot: the four kinds, the kind filter, the text filter, the ordering and the cap.
/// </summary>
public sealed class CueRetrieverTests
{
    [Fact]
    public void FindsARouteWithItsPath()
    {
        var snapshot = Snapshot("src/app.js", 1, "app.get('/users', handler)");

        var cues = CueRetriever.Find(snapshot, CueKinds.Route, null, 10);

        var cue = Assert.Single(cues);
        Assert.Equal(CueKinds.Route, cue.Kind);
        Assert.Equal("app.get", cue.Target);
        Assert.Equal("/users", cue.Detail);
        Assert.Equal(1, cue.Line);
    }

    [Fact]
    public void FindsAPythonTestByName()
    {
        var snapshot = Snapshot("tests/test_cart.py", 1, "def test_total():");

        var cues = CueRetriever.Find(snapshot, CueKinds.Test, null, 10);

        Assert.Contains(cues, cue => cue.Target == "test_total");
    }

    [Fact]
    public void FindsADatabaseCall()
    {
        var snapshot = Snapshot("src/repo.py", 1, "rows = db.session.query(User).all()");

        var cues = CueRetriever.Find(snapshot, CueKinds.DatabaseCall, null, 10);

        Assert.Contains(cues, cue => cue.Target == "db.session.query");
    }

    [Fact]
    public void KindFilterExcludesOtherKinds()
    {
        var snapshot = Snapshot("src/app.js", 1, "app.get('/users'); describe('x', () => {});");

        var routes = CueRetriever.Find(snapshot, CueKinds.Route, null, 10);
        var tests = CueRetriever.Find(snapshot, CueKinds.Test, null, 10);

        Assert.All(routes, cue => Assert.Equal(CueKinds.Route, cue.Kind));
        Assert.Contains(tests, cue => cue.Target == "describe");
    }

    [Fact]
    public void TextFilterMatchesTheCapturedDetail()
    {
        var snapshot = Snapshot("src/app.js", 1, "app.get('/users');\napp.post('/orders');");

        var cues = CueRetriever.Find(snapshot, CueKinds.Route, "orders", 10);

        var cue = Assert.Single(cues);
        Assert.Equal("/orders", cue.Detail);
    }

    [Fact]
    public void AnyKindReturnsEveryKind()
    {
        var snapshot = Snapshot("src/app.js", 1, "app.get('/x');\nrows = db.query('select');");

        var cues = CueRetriever.Find(snapshot, CueKinds.Any, null, 20);

        Assert.Contains(cues, cue => cue.Kind == CueKinds.Route);
        Assert.Contains(cues, cue => cue.Kind == CueKinds.DatabaseCall);
    }

    [Fact]
    public void CuesAreOrderedByLine()
    {
        var snapshot = Snapshot("src/app.js", 1, "app.get('/first');\napp.post('/second');");

        var cues = CueRetriever.Find(snapshot, CueKinds.Route, null, 10);

        Assert.Equal(new[] { "/first", "/second" }, cues.Select(cue => cue.Detail));
    }

    [Fact]
    public void CapIsApplied()
    {
        var snapshot = Snapshot("src/app.js", 1, "app.get('/a');\napp.get('/b');\napp.get('/c');");

        Assert.Single(CueRetriever.Find(snapshot, CueKinds.Route, null, 1));
    }

    [Fact]
    public void UnrelatedTextFindsNothing()
    {
        var snapshot = Snapshot("src/util.py", 1, "def helper(value):\n    return value + 1");

        Assert.Empty(CueRetriever.Find(snapshot, CueKinds.Any, null, 10));
    }

    private static IndexSnapshot Snapshot(string path, int startLine, string text)
    {
        var chunk = new ChunkRow
        {
            Id = 1,
            FileId = 1,
            Path = path,
            StartLine = startLine,
            EndLine = startLine + text.Count(character => character == '\n'),
            Kind = "chunk",
            Text = text,
            Sha8 = "00000000",
        };

        return new IndexSnapshot
        {
            BuildId = "b1",
            Chunks = new Dictionary<long, ChunkRow> { [1L] = chunk },
            Symbols = new Dictionary<long, SymbolRow>(),
            Edges = [],
            Lexical = LexicalIndex.Build(Array.Empty<(long Id, string Tokens)>()),
            Paths = new Dictionary<long, string> { [1L] = path },
            Files = new Dictionary<long, FileRow>(),
            DocumentedSymbols = new HashSet<string>(StringComparer.Ordinal),
            Embedder = "off",
            Dimensions = 0,
        };
    }
}
