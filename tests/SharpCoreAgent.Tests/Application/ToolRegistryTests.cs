using SharpCoreAgent.Application;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Application;

/// <summary>
/// The registry is both what the model is told about and what a call is looked up in, so the tests pin
/// the three promises the loop relies on: a duplicate name is a wiring bug caught at construction, the
/// order is stable between runs, and lookup is ordinal.
/// </summary>
public sealed class ToolRegistryTests
{
    [Fact]
    public void DefinitionsAndToolsAreOrderedByName()
    {
        var registry = new ToolRegistry([new NamedTool("write_file"), new NamedTool("read_file"), new NamedTool("list_directory")]);

        Assert.Equal(["list_directory", "read_file", "write_file"], registry.Definitions.Select(definition => definition.Name));
        Assert.Equal(["list_directory", "read_file", "write_file"], registry.Tools.Select(tool => tool.Definition.Name));
    }

    [Fact]
    public void ADuplicateNameIsRejectedAndNamed()
    {
        var error = Assert.Throws<ArgumentException>(
            () => new ToolRegistry([new NamedTool("echo"), new NamedTool("echo")]));

        Assert.Contains("'echo'", error.Message, StringComparison.Ordinal);
        Assert.Equal("tools", error.ParamName);
    }

    [Fact]
    public void FindIsOrdinalSoCasingMatters()
    {
        var tool = new NamedTool("read_file");
        var registry = new ToolRegistry([tool]);

        Assert.Same(tool, registry.Find("read_file"));
        Assert.Null(registry.Find("Read_File"));
    }

    [Fact]
    public void FindReturnsNullForAnUnknownName()
    {
        var registry = new ToolRegistry([new NamedTool("read_file")]);

        Assert.Null(registry.Find("git_diff"));
    }

    [Fact]
    public void ANullToolIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => new ToolRegistry([null!]));

    [Fact]
    public void ANullListIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => new ToolRegistry(null!));

    private sealed class NamedTool(string name, ToolSafety safety = ToolSafety.ReadOnly) : ITool
    {
        public ToolDefinition Definition { get; } = new(name, "a test tool", """{"type":"object"}""");

        public ToolSafety Safety { get; } = safety;

        public ValueTask<ToolResult> ExecuteAsync(
            ToolCall toolCall,
            ToolInvocationContext context,
            CancellationToken cancellationToken) => ValueTask.FromResult(ToolResult.Success(toolCall.Id, toolCall.Name, "ok"));
    }
}
