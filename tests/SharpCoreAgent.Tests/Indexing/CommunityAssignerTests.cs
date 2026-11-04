using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Tests.Tools;
using SharpCoreDB.Interfaces;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The whole-graph pass: what a build writes into <c>symbols.community</c> and <c>symbols.centrality</c>.
/// </summary>
/// <remarks>
/// The corpus is two clusters that never reference each other, plus one declaration nothing references. That shape
/// is the smallest one that can tell a real partition from a constant: a build that labelled everything <c>c0</c>
/// would satisfy "every row has a label" and fail here, and a build that wrote a fresh label per declaration would
/// fail the opposite way.
/// </remarks>
public sealed class CommunityAssignerTests
{
    private const string AlphaServiceSource = """
        namespace Demo.Alpha;

        public sealed class AlphaService
        {
            public int Run() => 41;

            public int Bump() => Run() + 1;
        }
        """;

    private const string AlphaCallerSource = """
        namespace Demo.Alpha;

        public static class AlphaCaller
        {
            public static int Measure()
            {
                var service = new AlphaService();
                return service.Run() + service.Bump();
            }
        }
        """;

    private const string BetaServiceSource = """
        namespace Demo.Beta;

        public sealed class BetaService
        {
            public string Fetch() => "beta";

            public string Load() => Fetch();
        }
        """;

    private const string BetaCallerSource = """
        namespace Demo.Beta;

        public static class BetaCaller
        {
            public static string Total()
            {
                var service = new BetaService();
                return service.Fetch() + service.Load();
            }
        }
        """;

    private const string LonelySource = """
        namespace Demo.Lonely;

        public sealed class Lonely
        {
            public string Name() => "lonely";
        }
        """;

    [Fact]
    public async Task TwoDisjointClustersGetTwoLabels()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteClusters(sandbox);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        var report = await maintainer.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(IndexFreshness.Fresh, report.Status.Freshness);
        Assert.True(report.Status.Edges > 0, "the two clusters must produce edges, or the labels would mean nothing");
        Assert.Equal(2, report.Status.Communities);
        Assert.Equal(2, report.Status.Centers);

        var symbols = ReadSymbols(sandbox.Workspace);
        var alphaRun = CommunityOf(symbols, "AlphaService.Run");
        var alphaBump = CommunityOf(symbols, "AlphaService.Bump");
        var alphaMeasure = CommunityOf(symbols, "AlphaCaller.Measure");
        var betaFetch = CommunityOf(symbols, "BetaService.Fetch");
        var betaLoad = CommunityOf(symbols, "BetaService.Load");
        var betaTotal = CommunityOf(symbols, "BetaCaller.Total");

        Assert.False(string.IsNullOrEmpty(alphaRun), "AlphaService.Run is called, so it belongs to a cluster");
        Assert.False(string.IsNullOrEmpty(betaFetch), "BetaService.Fetch is called, so it belongs to a cluster");
        Assert.Equal(alphaRun, alphaBump);
        Assert.Equal(alphaRun, alphaMeasure);
        Assert.Equal(betaFetch, betaLoad);
        Assert.Equal(betaFetch, betaTotal);
        Assert.NotEqual(alphaRun, betaFetch);

        // Nothing calls Lonely, so a label on it would claim a cluster of one: the highest-degree declaration of a
        // one-member community is not the centre of anything.
        Assert.Equal(string.Empty, CommunityOf(symbols, "Lonely.Name"));
    }

    [Fact]
    public async Task TheSameCorpusProducesTheSameLabels()
    {
        using var first = ToolTestWorkspace.Create();
        using var second = ToolTestWorkspace.Create();
        WriteClusters(first);
        WriteClusters(second);

        await Build(first);
        await Build(second);

        var left = ReadSymbols(first.Workspace);
        var right = ReadSymbols(second.Workspace);

        Assert.Equal(left.Count, right.Count);
        Assert.Equal(
            left.Select(static symbol => symbol.Key),
            right.Select(static symbol => symbol.Key));

        // Labels are numbered by the ordinally-first member of each community, not by the order the algorithm
        // merged in, so two builds of the same corpus must agree key for key.
        Assert.Equal(
            left.Select(static symbol => symbol.Community),
            right.Select(static symbol => symbol.Community));
        Assert.Equal(
            left.Select(static symbol => symbol.Centrality),
            right.Select(static symbol => symbol.Centrality));
    }

    [Fact]
    public async Task ThePassReportsWhatEachOfItsFiveStepsCost()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteClusters(sandbox);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        var report = await maintainer.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        var phases = report.Phases.ToDictionary(static phase => phase.Name, StringComparer.Ordinal);

        // Every step ran on this corpus: declarations to read, relations to walk, two clusters to find, and two
        // columns to write back.
        Assert.True(phases[IndexPhases.Nodes].Wall > TimeSpan.Zero, "reading the declarations must be measured");
        Assert.True(phases[IndexPhases.Adjacency].Wall > TimeSpan.Zero, "reading the relations must be measured");
        Assert.True(phases[IndexPhases.Louvain].Wall > TimeSpan.Zero, "the partition must be measured");
        Assert.True(phases[IndexPhases.Degree].Wall > TimeSpan.Zero, "the ranking must be measured");
        Assert.True(phases[IndexPhases.Assign].Wall > TimeSpan.Zero, "writing the columns must be measured");

        // None of them is a sibling: each is time already counted in `communities`, which is why every one is nested.
        string[] steps =
        [
            IndexPhases.Nodes,
            IndexPhases.Adjacency,
            IndexPhases.Louvain,
            IndexPhases.Degree,
            IndexPhases.Assign,
        ];

        Assert.All(steps, name => Assert.True(phases[name].Nested, $"'{name}' runs inside `communities`"));
        Assert.True(phases[IndexPhases.Communities].Wall >= phases[IndexPhases.Nodes].Wall);
        Assert.True(phases[IndexPhases.Communities].Wall >= phases[IndexPhases.Louvain].Wall);
    }

    [Fact]
    public async Task CentralityRanksACalledDeclarationAboveAnUncalledOne()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteClusters(sandbox);
        await Build(sandbox);

        var symbols = ReadSymbols(sandbox.Workspace);
        var called = CentralityOf(symbols, "AlphaService.Run");
        var untouched = CentralityOf(symbols, "Lonely.Name");

        Assert.True(called > 0, "a called declaration has at least one neighbour, so its degree must be positive");
        Assert.True(called > untouched, "a declaration with neighbours must outrank one whose row has none");
        Assert.Equal(0, untouched);
        Assert.True(called <= 1, "degree is normalized, so no declaration may exceed one");
    }

    [Fact]
    public async Task ABuildLeavesTheColumnsThePassDoesNotOwnUntouched()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteClusters(sandbox);

        await Build(sandbox);

        var paths = IndexPaths.For(sandbox.Workspace);
        using var lease = IndexHandleCache.Acquire(paths, encryptionPassword: null, createIfMissing: false);

        // The pass writes two of thirteen columns by primary key. A store that replaced the row instead of updating it
        // would blank everything the extraction wrote, and every reader would still find the columns it asked for.
        var rows = IndexSql.Query(
            lease.Database.Database,
            "SELECT key, name, qualified_name, kind, start_line, end_line, signature, community FROM symbols ORDER BY key");

        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            var key = IndexSql.AsText(row.GetValueOrDefault("key"));
            Assert.False(string.IsNullOrWhiteSpace(key), "every declaration keeps its key");
            Assert.False(string.IsNullOrWhiteSpace(IndexSql.AsText(row.GetValueOrDefault("name"))), $"'{key}' keeps its name");

            var kind = IndexSql.AsText(row.GetValueOrDefault("kind"));
            Assert.True(kind is "class" or "method", $"'{key}' keeps its kind, but it reads '{kind}'");

            // The key is the qualified name plus the kind, so the pair is a check on both columns at once: a row replace
            // would leave either of them empty and this equality would fail.
            Assert.Equal(IndexSql.AsText(row.GetValueOrDefault("qualified_name")) + "#" + kind, key);

            var start = IndexSql.AsLong(row.GetValueOrDefault("start_line"));
            Assert.True(start > 0, $"'{key}' keeps its line range");
            Assert.True(IndexSql.AsLong(row.GetValueOrDefault("end_line")) >= start, $"'{key}' keeps its end line");
            Assert.True(row.ContainsKey("community"), "the pass writes a label column, even when the label is empty");
        }

        // And the one row whose label must be empty is still written: an omitted column would leave the previous build's
        // label behind, and a row that used to be clustered would keep a community that no longer exists.
        var lonely = rows.Where(row =>
            IndexSql.AsText(row.GetValueOrDefault("key")).Contains(".Lonely.Name", StringComparison.Ordinal)).ToList();
        Assert.Single(lonely);
        Assert.Equal(string.Empty, IndexSql.AsText(lonely[0].GetValueOrDefault("community")));
    }

    /// <summary>
    /// A build whose partition has not moved writes no community row, and its commit count says so.
    /// </summary>
    /// <remarks>
    /// The change is a line in a document: the passages of one file move, and no declaration and no relation does, so
    /// the second build computes the partition the first one already stored. A document rather than a source file
    /// because rewriting a source file rewrites its relations, and a commit for those would sit in the same count as
    /// the one this test is about. Before round 23 such a build rewrote all of its declarations to say what they
    /// already said, and paid a commit of its own for the privilege.
    /// </remarks>
    [Fact]
    public async Task APassThatFindsTheSameAnswersWritesNothing()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteClusters(sandbox);
        sandbox.Write("docs/Notes.md", "# Notes\n\nthe first line\n");
        await Build(sandbox);

        var before = ReadSymbols(sandbox.Workspace);

        sandbox.Write("docs/Notes.md", "# Notes\n\nthe second line\n");

        var result = await BuildReport(sandbox);

        Assert.Equal(1, result.Changed);

        // One commit for the changed document and one for `meta`. A third would be the community columns, and there is
        // nothing to commit for them: every row already says what the pass computed.
        Assert.Equal(2, result.Commits);
        Assert.Equal(before, ReadSymbols(sandbox.Workspace));
    }

    /// <summary>
    /// A label no community owns is taken back even when the graph has not moved.
    /// </summary>
    /// <remarks>
    /// This is the case the unconditional rewrite used to cover by accident. A declaration whose last caller has been
    /// deleted keeps the label of a community that no longer exists, and the build that clears it has no graph change to
    /// point at: the comparison has to be against the answer rather than against whether the graph moved, or the stale
    /// label survives every future build.
    /// <para>
    /// The stale row is written on the handle both builds share, because it is the one state a build cannot produce on
    /// its own: asking the store for the same database twice would hand back a second handle that never saw the write.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AStaleLabelIsTakenBackEvenWhenTheGraphHasNotMoved()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteClusters(sandbox);
        sandbox.Write("docs/Notes.md", "# Notes\n\nthe first line\n");

        var paths = IndexPaths.For(sandbox.Workspace);
        using var lease = IndexHandleCache.Acquire(paths, encryptionPassword: null, createIfMissing: true);
        var database = lease.Database.Database;
        IndexSchema.EnsureCreated(database, paths);

        await WriteAsync(sandbox, database);

        var lonely = ReadSymbols(database)
            .Single(symbol => symbol.Key.Contains(".Lonely.Name", StringComparison.Ordinal));
        Assert.Equal(string.Empty, lonely.Community);

        WriteCommunity(database, lonely.Key, "c99");
        Assert.Equal("c99", ReadSymbols(database).Single(symbol => symbol.Key == lonely.Key).Community);

        // A document changes, so a writing run happens and no declaration is re-extracted: the only row the pass has to
        // write is the one that lies. Rewriting the file that declares it would wipe the label before the pass ran,
        // which is the build covering for the pass rather than the pass doing its job.
        sandbox.Write("docs/Notes.md", "# Notes\n\nthe second line\n");

        var result = await WriteAsync(sandbox, database);

        // The changed document, the one community row and `meta`.
        Assert.Equal(1, result.Changed);
        Assert.Equal(3, result.Commits);

        var after = ReadSymbols(database).Single(symbol => symbol.Key == lonely.Key);
        Assert.Equal(string.Empty, after.Community);
    }

    private static async Task Build(ToolTestWorkspace sandbox)
    {
        await BuildReport(sandbox);
    }

    private static async Task<IndexBuildReport> BuildReport(ToolTestWorkspace sandbox)
    {
        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        return await maintainer.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
    }

    private static ValueTask<IndexWriteResult> WriteAsync(ToolTestWorkspace sandbox, IDatabase database) =>
        IndexWriter
            .For(sandbox.Workspace, database)
            .WriteAsync(
                TimeSpan.FromMinutes(1),
                IndexBudgetClock.ProcessorTime,
                IndexBuildId.NewId(),
                TestContext.Current.CancellationToken);

    /// <summary>
    /// Puts a label on one stored declaration without going through a build.
    /// </summary>
    /// <param name="database">The open index to change.</param>
    /// <param name="key">The symbol key to relabel.</param>
    /// <param name="label">The label to store.</param>
    /// <remarks>
    /// A build cannot be asked for this state: it writes the labels it computed. The row it leaves behind after a caller
    /// is deleted looks exactly like this, so the test makes the row directly.
    /// </remarks>
    private static void WriteCommunity(IDatabase database, string key, string label)
    {
        var rows = IndexSql.Query(
            database,
            "SELECT id FROM symbols WHERE key = @key",
            IndexSql.Parameters(("@key", key)));

        Assert.Single(rows);
        Assert.True(
            IndexSql.Update(
                database,
                IndexTable.Symbols,
                IndexSql.AsLong(rows[0].GetValueOrDefault("id")),
                new Dictionary<string, object>(StringComparer.Ordinal) { ["community"] = label }),
            $"'{key}' must be a row the store can find by its primary key");
    }

    private static void WriteClusters(ToolTestWorkspace sandbox)
    {
        sandbox.Write("src/Alpha/AlphaService.cs", AlphaServiceSource);
        sandbox.Write("src/Alpha/AlphaCaller.cs", AlphaCallerSource);
        sandbox.Write("src/Beta/BetaService.cs", BetaServiceSource);
        sandbox.Write("src/Beta/BetaCaller.cs", BetaCallerSource);
        sandbox.Write("src/Lonely.cs", LonelySource);
    }

    /// <summary>
    /// Returns the label every declaration whose key contains one fragment shares.
    /// </summary>
    /// <param name="symbols">The stored declarations.</param>
    /// <param name="fragment">The part of the key to look for, for example <c>AlphaService</c>.</param>
    /// <returns>The shared label, or an empty string when the build assigned none.</returns>
    private static string CommunityOf(IReadOnlyList<StoredSymbol> symbols, string fragment)
    {
        var matches = symbols.Where(symbol => symbol.Key.Contains(fragment, StringComparison.Ordinal)).ToList();
        Assert.True(matches.Count > 0, $"the corpus declares nothing whose key contains '{fragment}'");

        var labels = matches.Select(static symbol => symbol.Community).Distinct(StringComparer.Ordinal).ToList();
        Assert.True(
            labels.Count == 1,
            $"the declarations of '{fragment}' should share one label, but the labels are {string.Join(", ", labels)}"
                + $" [{string.Join("; ", matches.Select(static symbol => $"{symbol.Key}='{symbol.Community}'"))}]");

        return labels[0];
    }

    private static double CentralityOf(IReadOnlyList<StoredSymbol> symbols, string key) =>
        symbols.First(symbol => symbol.Key.Contains(key, StringComparison.Ordinal)).Centrality;

    private static List<StoredSymbol> ReadSymbols(Workspace workspace)
    {
        var paths = IndexPaths.For(workspace);
        using var lease = IndexHandleCache.Acquire(paths, encryptionPassword: null, createIfMissing: false);
        return ReadSymbols(lease.Database.Database);
    }

    private static List<StoredSymbol> ReadSymbols(IDatabase database)
    {
        var symbols = new List<StoredSymbol>();
        foreach (var row in IndexSql.Query(
                     database,
                     "SELECT key, community, centrality FROM symbols ORDER BY key"))
        {
            symbols.Add(new StoredSymbol(
                IndexSql.AsText(row.GetValueOrDefault("key")),
                IndexSql.AsOptionalText(row.GetValueOrDefault("community")) ?? string.Empty,
                row.GetValueOrDefault("centrality") is { } value and not DBNull
                    ? Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)
                    : 0));
        }

        return symbols;
    }

    /// <summary>One declaration as the assertions read it: what it is called, its cluster, and its degree.</summary>
    /// <param name="Key">The stored symbol key.</param>
    /// <param name="Community">The stored label, or an empty string.</param>
    /// <param name="Centrality">The stored normalized degree.</param>
    private sealed record StoredSymbol(string Key, string Community, double Centrality);
}
