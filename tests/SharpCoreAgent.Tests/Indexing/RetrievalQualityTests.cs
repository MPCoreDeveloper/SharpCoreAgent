using System.Globalization;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Indexing.Ranking;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// SC-001 at a size a CI run can afford: a 24-file corpus and twelve questions, asking for the expected file in the
/// top three rather than the top five, because five of twenty-four is not a claim worth making.
/// </summary>
/// <remarks>
/// <para>
/// The spec's own measurement is a 200-file corpus with 25 recorded queries, where the expected file must appear in
/// the top five for 22 and first for 18. That run is manual, like the performance smoke in <c>plan.md</c>; this test
/// keeps the shape and scales the numbers down — top three for at least ten of twelve, first for at least eight —
/// which is what the tasks file asks for.
/// </para>
/// <para>
/// The corpus lives in <c>Indexing/Fixtures/Corpus</c> as source files rather than as strings in this file, so the
/// indexed text is the text a developer would browse, and so the fixture can grow without touching an assertion.
/// A failing run prints every question with the three paths it did return, because a recall number alone says
/// nothing about which question drifted.
/// </para>
/// </remarks>
public sealed class RetrievalQualityTests
{
    private const int CorpusSize = 24;
    private const int BlocksPerQuestion = 3;
    private const int TopThreeRequired = 10;
    private const int FirstRequired = 8;

    private static readonly (string Question, string Expected)[] SeededQuestions =
    [
        ("where is a failed http request retried with exponential backoff", "src/Http/RetryPolicy.cs"),
        ("which code limits how many requests per second a client may send", "src/Http/RateLimiter.cs"),
        ("how is a blob written to storage so a reader never sees half a payload", "src/Storage/BlobStore.cs"),
        ("where is the rolling hash that decides where a chunk ends", "src/Storage/ChunkWriter.cs"),
        ("which method checks an access token signature and expiry", "src/Auth/TokenValidator.cs"),
        ("how is a password salted and iterated with pbkdf2", "src/Auth/PasswordHasher.cs"),
        ("where does a cron expression become the next occurrence", "src/Scheduling/CronParser.cs"),
        ("which queue orders pending items by their due timestamp", "src/Scheduling/DelayQueue.cs"),
        ("where is the hsv to rgb conversion for a hue rotation", "src/Imaging/ColorConverter.cs"),
        ("how is an image downscaled with bilinear interpolation", "src/Imaging/ImageResizer.cs"),
        ("where is the 95th percentile computed from recorded durations", "src/Telemetry/MetricAggregator.cs"),
        ("which policy decides how many traces are sampled", "src/Telemetry/SamplingPolicy.cs"),
    ];

    [Fact]
    public async Task SeededQuestionsFindTheirFileInTheTopThree()
    {
        using var sandbox = ToolTestWorkspace.Create();
        Assert.Equal(CorpusSize, CopyCorpus(sandbox));

        await using (var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace))
        {
            var report = await maintainer.EnsureFreshAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

            Assert.False(report.Incomplete, "a partial index measures the budget, not the ranking");
            Assert.Equal(CorpusSize, report.Status.Files);
            Assert.Equal(IndexFreshness.Fresh, report.Status.Freshness);
        }

        using var index = SharpCoreDbCodeIndex.For(sandbox.Workspace);

        var first = 0;
        var topThree = 0;
        var transcript = new List<string>(SeededQuestions.Length);

        foreach (var (question, expected) in SeededQuestions)
        {
            // Distinct because two passages of one file can both rank, and "the expected file is in the top
            // three" is a claim about files: counting blocks would let a single file fill the window.
            var ranked = index
                .Search(
                    CodeQuery.Create(question, k: BlocksPerQuestion),
                    DateTimeOffset.UtcNow,
                    EvidenceBudget.DefaultMaxChars)
                .Select(static block => block.Path)
                .Distinct(StringComparer.Ordinal)
                .Take(BlocksPerQuestion)
                .ToList();

            var isFirst = ranked.Count > 0 && string.Equals(ranked[0], expected, StringComparison.Ordinal);

            if (isFirst)
            {
                first++;
            }

            if (ranked.Contains(expected, StringComparer.Ordinal))
            {
                topThree++;
            }

            transcript.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {(isFirst ? "hit " : "miss")} {question} -> {expected}, got [{string.Join(", ", ranked)}]"));
        }

        var measured = string.Create(
            CultureInfo.InvariantCulture,
            $"first {first}/{SeededQuestions.Length}, top three {topThree}/{SeededQuestions.Length}");
        var detail = measured + Environment.NewLine + string.Join(Environment.NewLine, transcript);

        Assert.True(topThree >= TopThreeRequired, "too few questions found their file in the top three: " + detail);
        Assert.True(first >= FirstRequired, "too few questions put their file first: " + detail);
    }

    /// <summary>
    /// Copies the fixture corpus into the sandbox under <c>src</c> and returns how many files it wrote.
    /// </summary>
    /// <param name="sandbox">The workspace to fill.</param>
    /// <returns>The number of files, which the test compares with <see cref="CorpusSize"/>.</returns>
    private static int CopyCorpus(ToolTestWorkspace sandbox)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Indexing", "Fixtures", "Corpus");
        Assert.True(Directory.Exists(root), $"the fixture corpus was not copied to '{root}'");

        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            sandbox.Write("src/" + relative, File.ReadAllText(file));
            copied++;
        }

        return copied;
    }
}
