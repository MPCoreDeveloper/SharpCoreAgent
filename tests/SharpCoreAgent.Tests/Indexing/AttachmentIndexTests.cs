using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>A composite index may contain an attachment's absolute path.</summary>
public sealed class AttachmentIndexTests
{
    [Fact]
    public async Task AnAttachmentFileIsIndexedUnderItsAbsolutePath()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var attachment = sandbox.AddAttachment("shared");
        ToolTestWorkspace.WriteUnder(attachment, "src/Order.cs", "namespace Shared; public sealed class Order { public int Id => 1; }");

        await using var index = SharpCoreAgentIndex.Open(sandbox.Workspace);
        var report = await index.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.True(report.Status.Chunks > 0);
        var hits = await index.SearchAsync(
            CodeQuery.Create("Order", k: 5),
            TestContext.Current.CancellationToken);

        Assert.Contains(hits, hit => hit.Path.EndsWith("Order.cs", StringComparison.Ordinal) && Path.IsPathRooted(hit.Path));
    }
}
