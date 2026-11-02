using System.Globalization;
using SharpCoreAgent.Cli;
using SharpCoreAgent.DependencyInjection;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The one status document both surfaces publish: its keys, its order, and the formatters that decide how a status reads.
/// </summary>
/// <remarks>
/// The byte-equality test is the reason this type exists. <c>index --status --json</c> and <c>/api/status.json</c> are one
/// object or they are two objects that will disagree the first time somebody adds a field, and comparing the two
/// renderings of one open index is the only assertion that catches that.
/// </remarks>
public sealed class IndexStatusDocumentTests
{
    [Fact]
    public void TheDocumentCarriesTheTwentyContractKeysInOrder()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var document = IndexStatusDocument.Build(WorkspaceFor(sandbox), Fresh());

        Assert.Equal(
            [
                "command",
                "action",
                "workspace",
                "state",
                "tier",
                "embedder",
                "dimensions",
                "files",
                "stale",
                "skipped",
                "orphans",
                "passages",
                "symbols",
                "edges",
                "communities",
                "sizeBytes",
                "vectorSearch",
                "builtUtc",
                "incomplete",
                "notes",
            ],
            document.Keys);
    }

    [Fact]
    public void AStatusWithNoCompleteBuildSerialisesTheBuildTimeAsNull()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var document = IndexStatusDocument.Build(WorkspaceFor(sandbox), Fresh() with { BuiltUtc = null });

        var json = IndexStatusDocument.Serialize(document);

        Assert.Contains("\"builtUtc\":null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("0001-01-01", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTwoRenderingsOfOneOpenIndexAreByteIdentical()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("src/Circle.cs", "namespace Demo;\n\ninternal sealed class Circle { }\n");
        var workspace = WorkspaceFor(sandbox);
        await using var index = SharpCoreAgentIndex.Open(workspace);
        await index.EnsureFreshAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var status = await index.GetStatusAsync(TestContext.Current.CancellationToken);
        var fromTheDocument = IndexStatusDocument.Serialize(IndexStatusDocument.Build(workspace, status));

        var printed = new StringWriter(CultureInfo.InvariantCulture);
        var errors = new StringWriter(CultureInfo.InvariantCulture);
        var exitCode = await IndexCommand.ExecuteAsync(
            CliOptions.Parse(["index", "--status", "--json", "--workspace", sandbox.Root], NoEnvironment),
            printed,
            errors,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(errors.ToString());

        // The command writes a line; the document is the text on it. Everything else is compared byte for byte.
        Assert.Equal(printed.ToString().TrimEnd('\r', '\n'), fromTheDocument);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1908, "1 908")]
    [InlineData(1234567, "1 234 567")]
    public void CountsAreGroupedWithSpacesNotSeparators(long value, string expected)
    {
        Assert.Equal(expected, IndexStatusDocument.Number(value));
    }

    [Theory]
    [InlineData(-1, "0 B")]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KiB")]
    [InlineData(14900224, "14.2 MiB")]
    [InlineData(2147483648, "2.0 GiB")]
    public void SizesReadInTheUnitThatFitsThem(long bytes, string expected)
    {
        Assert.Equal(expected, IndexStatusDocument.Size(bytes));
    }

    [Theory]
    [InlineData(IndexFreshness.Missing, "missing")]
    [InlineData(IndexFreshness.Building, "building")]
    [InlineData(IndexFreshness.Partial, "partial")]
    [InlineData(IndexFreshness.Stale, "stale")]
    [InlineData(IndexFreshness.Fresh, "fresh")]
    [InlineData(IndexFreshness.Corrupt, "corrupt")]
    public void EveryFreshnessStateHasOneLowercaseWord(IndexFreshness freshness, string expected)
    {
        Assert.Equal(expected, IndexStatusDocument.State(freshness));
    }

    [Fact]
    public void TheTierAndTheSeparatorsAreRenderedTheWayTheBlocksRenderThem()
    {
        Assert.Equal("lean", IndexStatusDocument.TierName(IndexTier.Lean));
        Assert.Equal("rich", IndexStatusDocument.TierName(IndexTier.Rich));
        Assert.Equal("a/b", IndexStatusDocument.Slash(@"a\b"));
    }

    /// <summary>A status with every count set, so a formatter that drops one is visible.</summary>
    private static IndexStatus Fresh() => new()
    {
        WorkspaceRoot = "C:/demo",
        Freshness = IndexFreshness.Fresh,
        Tier = IndexTier.Lean,
        Embedder = "hashed-local/512",
        Dimensions = 512,
        Files = 4,
        Chunks = 96,
        Symbols = 12,
        Edges = 21,
        Communities = 3,
        StaleFiles = 0,
        SkippedFiles = 1,
        OrphanRows = 2,
        VectorSearchEnabled = true,
        BuiltUtc = new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero),
        SizeBytes = 14900224,
    };

    private static Workspace WorkspaceFor(ToolTestWorkspace sandbox) =>
        CliOptions.Parse(["--workspace", sandbox.Root], NoEnvironment).ToSharpCoreAgentOptions().CreateWorkspace();

    private static string? NoEnvironment(string name) => null;
}
