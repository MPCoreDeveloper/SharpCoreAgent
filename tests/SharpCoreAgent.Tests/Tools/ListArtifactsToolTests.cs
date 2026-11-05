using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Mcp;
using SharpCoreAgent.Infrastructure.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Tools;

/// <summary>
/// The artefact folder is hidden from ordinary listing and the index; this tool is the one way a model can discover
/// what the sink saved.
/// </summary>
public sealed class ListArtifactsToolTests
{
    [Fact]
    public async Task AMissingFolderIsAnEmptyList()
    {
        using var sandbox = ToolTestWorkspace.Create();

        var result = await new ListArtifactsTool().ExecuteAsync(
            ToolTestWorkspace.Call("list_artifacts", "{}"),
            sandbox.Context,
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("artifacts: 0\nno artefacts have been saved", result.Content);
        Assert.Equal(new ToolEffect(ToolEffectKind.Read, McpArtefactSink.Folder), Assert.Single(result.Effects));
    }

    [Fact]
    public async Task ListsNewestFirstAndHonoursTheLimit()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var folder = Path.Combine(sandbox.Root, ".sharpcoreagent", "artifacts");
        Directory.CreateDirectory(folder);
        var older = Path.Combine(folder, "older.png");
        var newer = Path.Combine(folder, "newer.png");
        await File.WriteAllBytesAsync(older, new byte[10], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(newer, new byte[20], TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddMinutes(-5));

        var result = await new ListArtifactsTool().ExecuteAsync(
            ToolTestWorkspace.Call("list_artifacts", """{"limit":1}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.StartsWith("artifacts: 2 (showing the newest 1)", result.Content, StringComparison.Ordinal);
        Assert.Contains("newer.png", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("older.png", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void IsReadOnlyAndTakesOnlyAPositiveLimit()
    {
        var tool = new ListArtifactsTool();

        Assert.Equal("list_artifacts", tool.Definition.Name);
        Assert.Equal(ToolSafety.ReadOnly, tool.Safety);
        Assert.Contains("\"limit\"", tool.Definition.ParametersSchemaJson, StringComparison.Ordinal);
    }
}
