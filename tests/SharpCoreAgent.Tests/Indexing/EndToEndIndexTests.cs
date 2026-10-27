using System.Globalization;
using SharpCoreAgent.Cli;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Infrastructure.Tools;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The quickstart, scripted: build, ask all five tools, edit, refresh, verify, prune, rebuild.
/// </summary>
/// <remarks>
/// Every other test in this folder pins one seam. This one runs the seams in the order a developer meets them, so a
/// change that is right in isolation and wrong in sequence — a refresh that misses an edit, a prune that drops a live
/// file, a rebuild that forgets the schema — fails here rather than in the next turn of a real session.
/// </remarks>
public sealed class EndToEndIndexTests
{
    private const string CircleSource = """
        namespace Demo.Geometry;

        public sealed class Circle
        {
            private readonly double _radius = 2;

            public double Circumference() => 6.28 * _radius;
        }
        """;

    private const string PerimeterSource = """
        namespace Demo.Geometry;

        public sealed class CirclePerimeter
        {
            public double Measure(Circle circle) => circle.Circumference();
        }
        """;

    private const string DocsSource = """
        # Geometry

        The circumference of a circle is twice pi times the radius.
        """;

    [Fact]
    public async Task TheQuickstartRunsFromBuildToRebuild()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("src/Geometry/Circle.cs", CircleSource);
        sandbox.Write("src/Geometry/CirclePerimeter.cs", PerimeterSource);
        sandbox.Write("docs/geometry.md", DocsSource);

        await using var index = SharpCoreAgentIndex.Open(sandbox.Workspace);

        var build = await index.EnsureFreshAsync(TimeSpan.FromSeconds(60), cancellationToken);

        Assert.Equal(3, build.Added);
        Assert.Equal(0, build.Changed);
        Assert.False(build.Incomplete);
        Assert.Equal(IndexFreshness.Fresh, build.Status.Freshness);
        Assert.Equal(3, build.Status.Files);

        // 1. search_code: a citation, a hash, and the legs that found the block.
        var search = await ExecuteAsync(new SearchCodeTool(index), sandbox, """{"query":"circumference of a circle"}""");

        Assert.False(search.IsError);
        Assert.Matches(@"src/Geometry/Circle\.cs:\d+-\d+", search.Content);
        Assert.Contains("sha256:", search.Content, StringComparison.Ordinal);
        Assert.Contains("why: ", search.Content, StringComparison.Ordinal);

        // 2. find_symbol: the declaration, addressed by path and kind.
        var symbols = await ExecuteAsync(new FindSymbolTool(index), sandbox, """{"name":"Circumference"}""");

        Assert.False(symbols.IsError);
        Assert.Contains("Circumference · method · src/Geometry/Circle.cs", symbols.Content, StringComparison.Ordinal);

        // 3. find_references: the caller, not the callee.
        var references = await ExecuteAsync(
            new FindReferencesTool(index),
            sandbox,
            """{"symbol":"Circumference","direction":"callers"}""");

        Assert.False(references.IsError);
        Assert.Contains("src/Geometry/CirclePerimeter.cs", references.Content, StringComparison.Ordinal);

        // 4. code_context: the declaration and what sits around it.
        var context = await ExecuteAsync(new CodeContextTool(index), sandbox, """{"target":"Circumference","hops":1}""");

        Assert.False(context.IsError);
        Assert.Contains("Circumference", context.Content, StringComparison.Ordinal);

        // 5. index_status: the coverage the four answers above came from.
        var status = await ExecuteAsync(new IndexStatusTool(index), sandbox, "{}");

        Assert.False(status.IsError);
        Assert.StartsWith("state fresh", status.Content, StringComparison.Ordinal);
        Assert.Contains("files 3 ", status.Content, StringComparison.Ordinal);

        // An edit is picked up by the next refresh, and the refresh says which file it rewrote.
        sandbox.Write(
            "src/Geometry/Circle.cs",
            CircleSource.Replace(
                "public double Circumference() => 6.28 * _radius;",
                "public double Circumference() => 6.28 * _radius;\n\n    public double Diameter() => 2 * _radius;",
                StringComparison.Ordinal));

        var refresh = await index.EnsureFreshAsync(TimeSpan.FromSeconds(60), cancellationToken);

        Assert.Equal(1, refresh.Changed);
        Assert.Equal(0, refresh.Added);
        Assert.Equal(IndexFreshness.Fresh, refresh.Status.Freshness);

        var diameter = await ExecuteAsync(new SearchCodeTool(index), sandbox, """{"query":"Diameter","k":3}""");

        Assert.Contains("src/Geometry/Circle.cs:", diameter.Content, StringComparison.Ordinal);
        Assert.Contains("Diameter", diameter.Content, StringComparison.Ordinal);

        // Verify: the rows and the workspace agree, byte for byte.
        var verification = await index.VerifyAsync(cancellationToken);

        Assert.True(verification.IsHealthy, string.Join(" / ", verification.Details));

        // Prune: the deleted document loses its rows, and only its rows.
        File.Delete(sandbox.FullPath("docs/geometry.md"));

        var pruned = await index.PruneAsync(cancellationToken);

        Assert.Equal(1, pruned.Removed);
        Assert.Equal(2, pruned.Status.Files);

        // Rebuild: from scratch, same two files, same answers.
        var rebuilt = await index.RebuildAsync(cancellationToken);

        Assert.Equal(2, rebuilt.Status.Files);
        Assert.Equal(IndexFreshness.Fresh, rebuilt.Status.Freshness);
        Assert.True((await index.VerifyAsync(cancellationToken)).IsHealthy);

        var afterRebuild = await ExecuteAsync(new SearchCodeTool(index), sandbox, """{"query":"circumference"}""");

        Assert.Contains("src/Geometry/Circle.cs:", afterRebuild.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCommandBuildsTheIndexTheToolsThenRead()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("src/Geometry/Circle.cs", CircleSource);

        var (built, buildOutput, _) = await RunCommandAsync(sandbox, "index");

        Assert.Equal(ExitCodes.Success, built);
        Assert.Contains("1 added", buildOutput, StringComparison.Ordinal);

        await using (var index = SharpCoreAgentIndex.Open(sandbox.Workspace))
        {
            var search = await ExecuteAsync(new SearchCodeTool(index), sandbox, """{"query":"Circumference"}""");

            Assert.False(search.IsError);
            Assert.Contains("src/Geometry/Circle.cs:", search.Content, StringComparison.Ordinal);
        }

        var (verified, verifyOutput, _) = await RunCommandAsync(sandbox, "index", "--verify");

        Assert.Equal(ExitCodes.Success, verified);
        Assert.Contains("result      fresh", verifyOutput, StringComparison.Ordinal);
    }

    private static async Task<ToolResult> ExecuteAsync(ITool tool, ToolTestWorkspace sandbox, string arguments) =>
        await tool.ExecuteAsync(
            ToolTestWorkspace.Call(tool.Definition.Name, arguments),
            sandbox.Context,
            TestContext.Current.CancellationToken);

    private static async Task<(int ExitCode, string Output, string Error)> RunCommandAsync(
        ToolTestWorkspace sandbox,
        params string[] arguments)
    {
        string[] line = [.. arguments, "--workspace", sandbox.Root];
        var options = CliOptions.Parse(line, _ => null);
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var error = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await IndexCommand.ExecuteAsync(
            options,
            output,
            error,
            TestContext.Current.CancellationToken);

        return (exitCode, output.ToString(), error.ToString());
    }
}