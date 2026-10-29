using Microsoft.Extensions.DependencyInjection;
using SharpCoreAgent.Application;
using SharpCoreAgent.DependencyInjection;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Mcp;
using SharpCoreAgent.Infrastructure.McpServer;
using SharpCoreAgent.Infrastructure.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// What the server is willing to publish, asserted against the container the CLI actually builds.
/// </summary>
/// <remarks>
/// The subject is the filter, not the five tools: a registry holding every workspace tool — including, with
/// <c>--web</c>, the one that reaches the network — must yield exactly the allow-list. The registry comes from the
/// composition root rather than from a list written here, because a list in a test cannot drift out of step with the
/// product and therefore cannot catch the drift that matters.
/// </remarks>
public sealed class McpServerPublishedSetTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "sca-mcp-set-" + Guid.NewGuid().ToString("N"));

    /// <summary>Initialises the test with an empty workspace directory the container may name.</summary>
    public McpServerPublishedSetTests() => Directory.CreateDirectory(_workspace);

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory must never fail a test run.
        }
    }

    [Fact]
    public void TheSetIsExactlyTheAllowListInOrder()
    {
        var published = Publish();

        Assert.Equal(McpServerPublishedSet.AllowList, published.Tools.Select(tool => tool.Tool.Definition.Name));
        Assert.Equal(5, published.Count);
    }

    [Fact]
    public void EveryPublishedToolIsReadOnly()
    {
        var published = Publish();

        Assert.All(published.Tools, tool => Assert.Equal(ToolSafety.ReadOnly, tool.Tool.Safety));
    }

    [Theory]
    [InlineData("delete_file")]
    [InlineData("fetch_url")]
    [InlineData("list_directory")]
    [InlineData("list_roots")]
    [InlineData("read_file")]
    [InlineData("replace_in_file")]
    [InlineData("run_shell")]
    [InlineData("search_text")]
    [InlineData("shell_output")]
    [InlineData("shell_stop")]
    [InlineData("write_file")]
    public void AToolTheRegistryHoldsIsStillNotPublishedUnlessItIsOnTheList(string name)
    {
        using var provider = Build();
        var registry = provider.GetRequiredService<IToolRegistry>();

        // Registered, and every one of them is a real tool: the exclusion is the set's doing, not the test's.
        Assert.NotNull(registry.Find(name));
        Assert.Null(new McpServerPublishedSet(registry).Find(name));
    }

    [Fact]
    public void AToolOfTheAdapterIsNotPublishedEvenThoughItReads()
    {
        // An MCP server's tools are read-only by their annotations too, and they are the one class of tool whose
        // argv, environment and reach this process does not control. The allow-list is what keeps them out.
        var name = McpToolName.ForTool("playwright", "browser_click");
        var published = Publish(services => services.AddSingleton<ITool>(new FakeTool(name, ToolSafety.ReadOnly)));

        Assert.Null(published.Find(name));
        Assert.Equal(McpServerPublishedSet.AllowList, published.Tools.Select(tool => tool.Tool.Definition.Name));
    }

    [Fact]
    public void EveryPublishedSchemaIsTheToolsOwnText()
    {
        var published = Publish();

        // Byte for byte, because the schema is what a client validates against: a copy maintained here would be a
        // second description of the arguments, and the one the model reads would win the argument.
        Assert.All(
            published.Tools,
            tool => Assert.Equal(tool.Tool.Definition.ParametersSchemaJson, tool.InputSchema.ToJsonString()));
    }

    [Fact]
    public void ANameTheRegistryDoesNotHoldIsSkippedRatherThanInvented()
    {
        var published = new McpServerPublishedSet(new ToolRegistry([new IndexStatusTool()]));

        Assert.Equal(["index_status"], published.Tools.Select(tool => tool.Tool.Definition.Name));
    }

    [Fact]
    public void ANameThatIsNoLongerReadOnlyIsNotPublished()
    {
        // The second gate on its own: a name on the list whose safety changed must disappear from the wire.
        var published = new McpServerPublishedSet(
            new ToolRegistry([new FakeTool(SearchCodeTool.Name, ToolSafety.Destructive)]));

        Assert.Equal(0, published.Count);
        Assert.Null(published.Find(SearchCodeTool.Name));
    }

    [Fact]
    public void ASetWithNoRegistryPublishesNothing() => Assert.Equal(0, McpServerPublishedSet.Empty.Count);

    private McpServerPublishedSet Publish(Action<IServiceCollection>? extra = null)
    {
        using var provider = Build(extra);
        return new McpServerPublishedSet(provider.GetRequiredService<IToolRegistry>());
    }

    private ServiceProvider Build(Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();

        // The network tool is registered on purpose: it reads, it is not on the allow-list, and it must not reach a
        // client.
        services.AddSharpCoreAgent(new SharpCoreAgentOptions
        {
            WorkspaceRoot = _workspace,
            WebAccessEnabled = true,
            WebAllowedHosts = ["localhost"],
        });

        extra?.Invoke(services);

        return services.BuildServiceProvider();
    }

    /// <summary>A tool a test registered, so the filter can be shown accepting and rejecting one it never heard of.</summary>
    private sealed class FakeTool(string name, ToolSafety safety) : ITool
    {
        public ToolDefinition Definition { get; } =
            new(name, "a tool a test registered", """{"type":"object","properties":{},"required":[]}""");

        public ToolSafety Safety => safety;

        public ValueTask<ToolResult> ExecuteAsync(
            ToolCall toolCall,
            ToolInvocationContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ToolResult.Success(toolCall.Id, toolCall.Name, "ok"));
    }
}
