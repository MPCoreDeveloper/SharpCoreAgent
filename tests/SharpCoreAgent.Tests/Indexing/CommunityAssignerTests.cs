using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Tests.Tools;
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

    private static async Task Build(ToolTestWorkspace sandbox)
    {
        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        await maintainer.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
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

        var symbols = new List<StoredSymbol>();
        foreach (var row in IndexSql.Query(
                     lease.Database.Database,
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
