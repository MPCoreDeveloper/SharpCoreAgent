using System.Text.Json.Nodes;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Mcp;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The resources and prompts bridges: a server that advertises either capability gets one read-only tool, the
/// catalog is text the model can read, and a read/get call reaches the method the server named.
/// </summary>
public sealed class McpCatalogTests
{
    [Fact]
    public async Task ResourcesAreListedAndReadByUri()
    {
        await using var harness = new Harness(capabilities: true);
        await harness.StartAsync();

        Assert.True(harness.Connection.HasResources);
        var tool = new McpResourceTool("npx_resources", harness.Connection, new McpClientOptions());

        var list = await ExecuteAsync(tool, "{}");
        Assert.False(list.IsError);
        Assert.Contains("file:///a.txt", list.Content, StringComparison.Ordinal);
        Assert.Contains("A file", list.Content, StringComparison.Ordinal);

        var read = await ExecuteAsync(tool, """{"uri":"file:///a.txt"}""");
        Assert.False(read.IsError);
        Assert.Contains("hello resource", read.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromptsAreListedWithTheirRequiredArgumentsAndRendered()
    {
        await using var harness = new Harness(capabilities: true);
        await harness.StartAsync();

        Assert.True(harness.Connection.HasPrompts);
        var tool = new McpPromptTool("npx_prompts", harness.Connection, new McpClientOptions());

        var list = await ExecuteAsync(tool, "{}");
        Assert.False(list.IsError);
        Assert.Contains("review", list.Content, StringComparison.Ordinal);
        Assert.Contains("path*", list.Content, StringComparison.Ordinal);

        var get = await ExecuteAsync(tool, """{"name":"review","arguments":{"path":"src"}}""");
        Assert.False(get.IsError);
        Assert.Contains("user: review src", get.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACapabilityThatWasNotAdvertisedIsNotClaimed()
    {
        await using var harness = new Harness(capabilities: false);
        await harness.StartAsync();

        Assert.False(harness.Connection.HasResources);
        Assert.False(harness.Connection.HasPrompts);
    }

    private static async Task<ToolResult> ExecuteAsync(ITool tool, string arguments) =>
        await tool.ExecuteAsync(
            new ToolCall("call-1", tool.Definition.Name, arguments),
            new ToolInvocationContext(Workspace.Create(Path.GetTempPath())),
            TestContext.Current.CancellationToken);
 
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ScriptedMcpServer _server;

        public Harness(bool capabilities)
        {
            _server = new ScriptedMcpServer(request => request.Method switch
            {
                "initialize" => ScriptedMcpServer.Answer(request.Id, Handshake(capabilities)),
                "tools/list" => ScriptedMcpServer.Answer(request.Id, new JsonObject
                {
                    ["tools"] = new JsonArray(Tool()),
                }),
                "resources/list" => ScriptedMcpServer.Answer(request.Id, new JsonObject
                {
                    ["resources"] = new JsonArray(new JsonObject
                    {
                        ["uri"] = "file:///a.txt",
                        ["name"] = "a.txt",
                        ["mimeType"] = "text/plain",
                        ["description"] = "A file",
                    }),
                }),
                "resources/read" => ScriptedMcpServer.Answer(request.Id, new JsonObject
                {
                    ["contents"] = new JsonArray(new JsonObject
                    {
                        ["uri"] = "file:///a.txt",
                        ["mimeType"] = "text/plain",
                        ["text"] = "hello resource",
                    }),
                }),
                "prompts/list" => ScriptedMcpServer.Answer(request.Id, new JsonObject
                {
                    ["prompts"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "review",
                        ["description"] = "Review a path",
                        ["arguments"] = new JsonArray(new JsonObject
                        {
                            ["name"] = "path",
                            ["required"] = true,
                        }),
                    }),
                }),
                "prompts/get" => ScriptedMcpServer.Answer(request.Id, new JsonObject
                {
                    ["description"] = "Review a path",
                    ["messages"] = new JsonArray(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = "review src",
                        },
                    }),
                }),
                _ => null,
            });

            Connection = new McpServerConnection(
                "npx",
                "npx -y test-server",
                _server.Input,
                _server.Output,
                new McpClientOptions());
        }

        public McpServerConnection Connection { get; }

        public Task StartAsync() => Connection.StartAsync(TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync().ConfigureAwait(false);
            await _server.DisposeAsync().ConfigureAwait(false);
        }

        private static JsonObject Handshake(bool capabilities) => new()
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = capabilities
                ? new JsonObject
                {
                    ["resources"] = new JsonObject(),
                    ["prompts"] = new JsonObject(),
                }
                : new JsonObject(),
        };

        private static JsonObject Tool() => new()
        {
            ["name"] = "search",
            ["description"] = "search",
            ["inputSchema"] = new JsonObject { ["type"] = "object" },
            ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
        };
    }

}
