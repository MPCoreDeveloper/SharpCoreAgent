using SharpCoreAgent.Cli;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// <c>mcp bench</c>: the file it reads, the number it computes, and the run a developer pastes into <c>plan.md</c>.
/// </summary>
/// <remarks>
/// The end-to-end test hands the bench a command line naming the CLI this build produced, because a command that
/// starts <em>itself</em> cannot do so from inside a test host: the process a test runs in is the runner, not
/// <c>sharpcoreagent</c>. Everything after that — the tools, the client, the answer — is the product's own.
/// </remarks>
public sealed class McpBenchTests : IDisposable
{
    private readonly ToolTestWorkspace _sandbox = ToolTestWorkspace.Create();

    /// <summary>Writes the two files an answer can cite.</summary>
    public McpBenchTests()
    {
        _sandbox.Write("src/Thermometer.cs", "namespace Demo;\n\npublic sealed class Thermometer\n{\n    public double Fahrenheit() => (Celsius * 9 / 5) + 32;\n\n    public double Celsius { get; private set; }\n}\n");
        _sandbox.Write("src/SensorLog.cs", "namespace Demo;\n\npublic sealed class SensorLog\n{\n    public void Add(double celsius) => _celsius.Add(celsius);\n\n    private readonly List<double> _celsius = [];\n}\n");
    }

    /// <inheritdoc />
    public void Dispose() => _sandbox.Dispose();

    [Fact]
    public void TheQuestionsSkipCommentsAndBlankLines()
    {
        var queries = McpBench.ReadQueries(
        [
            "# the twelve questions",
            string.Empty,
            "  where is the budget enforced  ",
            "   ",
            "which file writes the transcript",
        ]);

        Assert.Equal(["where is the budget enforced", "which file writes the transcript"], queries);
    }

    [Fact]
    public void TheSourceCharactersAreTheWholeFilesCountedOncePerQuery()
    {
        var thermometer = _sandbox.FullPath("src/Thermometer.cs");
        var log = _sandbox.FullPath("src/SensorLog.cs");

        var answer = string.Join(
            '\n',
            "found 3 blocks (asked 8) · index fresh · 0 stale · lexical",
            string.Empty,
            "1) src/Thermometer.cs:40-45 · method · Fahrenheit · sha256:ab12 · why: lexical",
            "     4│public double Fahrenheit() => (Celsius * 9 / 5) + 32;",
            "2) src/SensorLog.cs:30-34 · field · _celsius · sha256:cd34 · why: vector",
            "3) src/Thermometer.cs:41-52 · method · Celsius · sha256:ab12 · why: symbol");

        // Three headings, two distinct files: the heading that repeats a file must not be counted twice, and the
        // quoted source line (which contains no heading) must contribute nothing.
        Assert.Equal(File.ReadAllText(thermometer).Length + File.ReadAllText(log).Length, McpBench.CitedChars(_sandbox.Root, answer));
    }

    [Fact]
    public void ARatioBelowOneMeansTheAnswerCostLessThanReadingTheFiles()
    {
        var report = new BenchReport([new BenchRow("q", AnswerChars: 200, SourceChars: 2_000, Milliseconds: 30)], Attempted: 1);

        Assert.Equal(0.1, report.Ratio, 6);
        Assert.Equal(200, report.AnswerChars);
        Assert.Equal(2_000, report.SourceChars);
        Assert.True(report.IsComplete);
    }

    [Fact]
    public void TheNinetyFifthPercentileIsTheSlowestOfTwelveCalls()
    {
        var rows = Enumerable
            .Range(1, 12)
            .Select(milliseconds => new BenchRow($"q{milliseconds}", 1, 1, milliseconds))
            .ToArray();

        // Nearest rank, not an interpolation: with twelve samples no call ever took the number between the two slowest.
        Assert.Equal(12, new BenchReport(rows, 12).P95Milliseconds);
    }

    [Fact]
    public void AnEmptyRunHasNoNumberRatherThanADivideByZero()
    {
        var report = new BenchReport([], Attempted: 3);

        Assert.Equal(0, report.Ratio);
        Assert.Equal(0, report.P95Milliseconds);
        Assert.False(report.IsComplete);
        Assert.Equal(0, report.Identical);
    }

    [Fact]
    public void TheTableCarriesTheSummaryThePlanQuotesAndOneRowPerQuery()
    {
        var report = new BenchReport(
            [
                new BenchRow("where is the budget enforced", 268, 2_118, 41),
                new BenchRow("which file writes the transcript", 91, 512, 12),
            ],
            Attempted: 2);

        var table = McpBench.Render(report).Split('\n');

        Assert.Equal("bench · 2 queries · evidence 359 chars · sources 2 630 chars · ratio 0.137 · p95 41 ms · parity 2/2", table[0]);
        Assert.StartsWith("   chars    source   ratio", table[2], StringComparison.Ordinal);
        Assert.Contains("where is the budget enforced", table[3], StringComparison.Ordinal);
        Assert.Contains("which file writes the transcript", table[4], StringComparison.Ordinal);
        Assert.StartsWith("     268     2 118   0.127", table[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBenchAsksTheFixtureQuestionsOverARealServer()
    {
        _sandbox.Write(
            "specs/013-evidence-mcp-server/bench/queries.txt",
            "# two of them\nconvert celsius to fahrenheit\nwhere is a reading logged\n");
        await IndexAsync();

        var error = new StringWriter();
        var output = new StringWriter();

        var exit = await McpBench.ExecuteAsync(
            Options(),
            output,
            error,
            CliBinary.Serve(_sandbox.Root),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);

        var table = output.ToString();
        Assert.StartsWith("bench · 2 queries · evidence ", table, StringComparison.Ordinal);
        Assert.Contains("parity 2/2", table, StringComparison.Ordinal);
        Assert.Contains("p95 ", table, StringComparison.Ordinal);
        Assert.Contains("convert celsius to fahrenheit", table, StringComparison.Ordinal);
        Assert.Contains("where is a reading logged", table, StringComparison.Ordinal);

        // The header repeats the index the answers came from, because a ratio measured against the wrong index is a
        // number about nothing.
        Assert.Contains("index fresh", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(_sandbox.Root, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWorkspaceWithNoQuestionsIsRefusedByThePathItLookedFor()
    {
        await IndexAsync();

        var error = new StringWriter();

        var exit = await McpBench.ExecuteAsync(
            Options(),
            new StringWriter(),
            error,
            CliBinary.Serve(_sandbox.Root),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exit);
        Assert.Contains(McpBench.QueriesRelativePath, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIndexNobodyBuiltIsRefusedWithTheCommandThatBuildsOne()
    {
        _sandbox.Write("specs/013-evidence-mcp-server/bench/queries.txt", "convert celsius to fahrenheit\n");

        var error = new StringWriter();

        // No maintainer ran, so the refusal happens before any server is started: a bench over a missing index would
        // measure the "no index" answer twelve times and print it as a result.
        var exit = await McpBench.ExecuteAsync(
            Options(),
            new StringWriter(),
            error,
            CliBinary.Serve(_sandbox.Root),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exit);
        Assert.Contains("sharpcoreagent index", error.ToString(), StringComparison.Ordinal);
    }

    private CliOptions Options() =>
        CliOptions.Parse(["mcp", "bench", "--workspace", _sandbox.Root], _ => null);

    /// <summary>Builds the index the answers are expected to come from.</summary>
    private async Task IndexAsync()
    {
        await using var maintainer = SharpCoreDbIndexMaintainer.For(_sandbox.Workspace);
        var report = await maintainer.EnsureFreshAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

        Assert.False(report.Incomplete);
        Assert.Equal(IndexFreshness.Fresh, report.Status.Freshness);
        Assert.True(report.Status.Files >= 2, "the fixture writes two source files before anything is asked");
    }
}
