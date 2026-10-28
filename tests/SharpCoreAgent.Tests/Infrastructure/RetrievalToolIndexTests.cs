using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Infrastructure.Tools;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The retrieval tools against a real index of a real directory tree.
/// </summary>
/// <remarks>
/// The fake-based tests pin the wording; this one pins the wiring. It is the only place where a tool, the
/// <see cref="SharpCoreAgentIndex"/> facade and the storage adapter meet, so a change to the signal between them — the
/// shape of a block, the target a context resolves, the note a status carries — fails here rather than in production.
/// </remarks>
public sealed class RetrievalToolIndexTests
{
    private const string CircleSource = """
        namespace Demo.Geometry;

        public sealed class Circle
        {
            private readonly double _radius;

            public double Circumference() => 2 * 3.14 * _radius;
        }
        """;

    [Fact]
    public async Task SearchCodeAnswersFromABuiltIndex()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("src/Geometry/Circle.cs", CircleSource);

        await using var index = SharpCoreAgentIndex.Open(sandbox.Workspace);
        await index.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        var result = await Execute(new SearchCodeTool(index), sandbox, """{"query":"Circumference"}""");

        Assert.False(result.IsError);
        Assert.Contains("found ", result.Content, StringComparison.Ordinal);
        Assert.Contains("src/Geometry/Circle.cs:", result.Content, StringComparison.Ordinal);
        Assert.Contains("· why: ", result.Content, StringComparison.Ordinal);
        Assert.Contains("sha256:", result.Content, StringComparison.Ordinal);
        Assert.Contains("│", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSymbolSeesTheDeclarationTheBuildStored()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("src/Geometry/Circle.cs", CircleSource);

        await using var index = SharpCoreAgentIndex.Open(sandbox.Workspace);
        await index.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        var result = await Execute(new FindSymbolTool(index), sandbox, """{"name":"Circle"}""");

        Assert.False(result.IsError);
        Assert.Contains("Demo.Geometry.Circle · class · src/Geometry/Circle.cs", result.Content, StringComparison.Ordinal);
        Assert.Contains("callers ", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodeContextAnswersAPathTargetWithItsDeclarations()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("src/Geometry/Circle.cs", CircleSource);

        await using var index = SharpCoreAgentIndex.Open(sandbox.Workspace);
        await index.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        var result = await Execute(new CodeContextTool(index), sandbox, """{"target":"src/Geometry/Circle.cs"}""");

        Assert.False(result.IsError);
        Assert.Contains("src/Geometry/Circle.cs · ", result.Content, StringComparison.Ordinal);
        Assert.Contains("declaration", result.Content, StringComparison.Ordinal);
        Assert.Contains("Demo.Geometry.Circle · class", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IndexStatusDescribesTheBuildThatJustRan()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("src/Geometry/Circle.cs", CircleSource);

        await using var index = SharpCoreAgentIndex.Open(sandbox.Workspace);
        await index.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        var result = await Execute(new IndexStatusTool(index), sandbox, "{}");

        Assert.False(result.IsError);
        Assert.StartsWith("state fresh · tier lean · embedder hashed-local/512", result.Content, StringComparison.Ordinal);
        Assert.Contains("files 1 ", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryToolSaysTheIndexIsMissingBeforeTheFirstBuild()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("src/Geometry/Circle.cs", CircleSource);

        await using var index = SharpCoreAgentIndex.Open(sandbox.Workspace);

        var search = await Execute(new SearchCodeTool(index), sandbox, """{"query":"Circle"}""");
        var symbols = await Execute(new FindSymbolTool(index), sandbox, """{"name":"Circle"}""");
        var context = await Execute(new CodeContextTool(index), sandbox, """{"target":"Circle"}""");

        Assert.Equal(CodeIndexAccess.MissingMessage, search.Content);
        Assert.Equal(CodeIndexAccess.MissingMessage, symbols.Content);
        Assert.Equal(CodeIndexAccess.MissingMessage, context.Content);
    }

    private static async Task<ToolResult> Execute(ITool tool, ToolTestWorkspace sandbox, string arguments) =>
        await tool.ExecuteAsync(
            ToolTestWorkspace.Call(tool.Definition.Name, arguments),
            sandbox.Context,
            TestContext.Current.CancellationToken);
}
