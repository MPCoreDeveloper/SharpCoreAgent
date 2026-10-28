using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Tools;

/// <summary>
/// <c>list_roots</c> is how a model learns an absolute path it may use, so the tests pin the three things a
/// reader depends on: the labels, the order, and the fact that a path inside an attachment really is one.
/// </summary>
public sealed class ListRootsToolTests
{
    [Fact]
    public async Task ReportsThePrimaryRootAloneWhenNothingIsAttached()
    {
        using var sandbox = ToolTestWorkspace.Create();

        var result = await new ListRootsTool().ExecuteAsync(
            ToolTestWorkspace.Call("list_roots", "{}"),
            sandbox.Context,
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal($"primary    {Forward(sandbox.Root)}", result.Content);
        Assert.False(result.IsTruncated);
        Assert.Equal(new ToolEffect(ToolEffectKind.Read, "."), Assert.Single(result.Effects));
    }

    [Fact]
    public async Task ReportsThePrimaryRootThenEachAttachmentInTheOrderTheyWereNamed()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var first = sandbox.AddAttachment("shared");
        var second = sandbox.AddAttachment("proto");

        var result = await new ListRootsTool().ExecuteAsync(
            ToolTestWorkspace.Call("list_roots", "{}"),
            sandbox.Context,
            CancellationToken.None);

        Assert.Equal(
            string.Join(
                '\n',
                $"primary    {Forward(sandbox.Root)}",
                $"read-only  {Forward(first)}",
                $"read-only  {Forward(second)}"),
            result.Content);
    }

    [Fact]
    public void IsReadOnlyAndTakesNoArguments()
    {
        var tool = new ListRootsTool();

        Assert.Equal("list_roots", tool.Definition.Name);
        Assert.Equal(ToolSafety.ReadOnly, tool.Safety);
        Assert.Contains("\"additionalProperties\":false", tool.Definition.ParametersSchemaJson, StringComparison.Ordinal);

        // No approval mode has anything to ask about a tool that only reads the roots it was handed, and the
        // description has to say how an attachment is reached, because that is the part the model must learn.
        Assert.Contains("read-only attached root", tool.Definition.Description, StringComparison.Ordinal);
        Assert.Contains("absolute path", tool.Definition.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RendersEveryPathWithForwardSlashes()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.AddAttachment("shared");

        var result = await new ListRootsTool().ExecuteAsync(
            ToolTestWorkspace.Call("list_roots", "{}"),
            sandbox.Context,
            CancellationToken.None);

        Assert.DoesNotContain('\\', result.Content);
    }

    private static string Forward(string path) => path.Replace('\\', '/');
}
