using System.Diagnostics;
using System.Globalization;
using System.Text;
using SharpCoreAgent.Cli;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Infrastructure.Tools;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// SC-003: a <c>search_code</c> call on a 20 000-passage index answers in under 250 ms at p95.
/// </summary>
/// <remarks>
/// The corpus is synthetic and generated here so the harness has no fixture to drift. It is skipped by default because
/// a 1 000-file build is not a unit test: set <c>SHARPCOREAGENT_RUN_LATENCY=1</c> and run this class to measure.
/// </remarks>
public sealed class SearchLatencyTests
{
    private const int FileCount = 1_700;

    private const int MethodsPerFile = 20;

    private const int Samples = 100;

    private const double TargetMilliseconds = 250;

    [Fact]
    [Trait("Category", "Performance")]
    public async Task TwentyThousandPassagesAnswerUnder250MillisecondsAtP95()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("SHARPCOREAGENT_RUN_LATENCY"),
            "1",
            StringComparison.Ordinal))
        {
            Assert.Skip("set SHARPCOREAGENT_RUN_LATENCY=1 to build the synthetic 20 000-passage corpus and measure p95");
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        await using var index = SharpCoreAgentIndex.Open(sandbox.Workspace);
        var build = await index.RebuildAsync(cancellationToken);

        Assert.False(build.Incomplete);
        Assert.True(
            build.Status.Chunks >= 20_000,
            string.Create(CultureInfo.InvariantCulture, $"the synthetic corpus produced {build.Status.Chunks} passages, not 20 000"));

        var tool = new SearchCodeTool(index);
        var samples = new List<double>(Samples);
        for (var sample = 0; sample < Samples; sample++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var method = sample * 197 % (FileCount * MethodsPerFile);
            var query = string.Create(CultureInfo.InvariantCulture, $"M{method:000000}");
            var stopwatch = Stopwatch.StartNew();
            var result = await tool.ExecuteAsync(
                ToolTestWorkspace.Call(tool.Definition.Name, ToolTestWorkspace.Args(new { query, k = 5 })),
                sandbox.Context,
                cancellationToken);
            stopwatch.Stop();

            Assert.False(result.IsError, result.Content);
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        var p95 = samples[(int)Math.Ceiling(samples.Count * 0.95) - 1];
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"latency: files={FileCount} passages={build.Status.Chunks} samples={Samples} p50={samples[Samples / 2]:0.0} ms p95={p95:0.0} ms max={samples[^1]:0.0} ms"));

        Assert.True(
            p95 < TargetMilliseconds,
            string.Create(
                CultureInfo.InvariantCulture,
                $"search_code p95 was {p95:0.0} ms on {build.Status.Chunks} passages; the target is {TargetMilliseconds:0} ms"));
    }

    private static void WriteCorpus(ToolTestWorkspace sandbox)
    {
        for (var file = 0; file < FileCount; file++)
        {
            var source = new StringBuilder();
            source.AppendLine(CultureInfo.InvariantCulture, $"namespace Synthetic.Generated{file:0000};");
            source.AppendLine();
            source.AppendLine(CultureInfo.InvariantCulture, $"public sealed class C{file:0000}");
            source.AppendLine("{");
            for (var method = 0; method < MethodsPerFile; method++)
            {
                var ordinal = file * MethodsPerFile + method;
                source.AppendLine(CultureInfo.InvariantCulture, $"    public int M{ordinal:000000}(int value) => value + {ordinal};");
            }

            source.AppendLine("}");
            sandbox.Write(string.Create(CultureInfo.InvariantCulture, $"src/Generated/F{file:0000}.cs"), source.ToString());
        }
    }
}
