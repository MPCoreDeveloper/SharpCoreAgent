using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Mcp;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The adapter is the only place MCP meets the round-001 tool contract, so these tests cover both halves: how a
/// server's <c>content[]</c> becomes text a model can read, and how one server call becomes a
/// <see cref="ToolResult"/> the loop already understands.
/// </summary>
public sealed class McpToolTests
{
    [Fact]
    public void EveryContentKindIsDescribedRatherThanDropped()
    {
        var text = Flatten(
            """
            {"content":[
              {"type":"text","text":"plain"},
              {"type":"image","mimeType":"image/png","data":"AAAA"},
              {"type":"audio","mimeType":"audio/wav","data":"BBBB"},
              {"type":"resource","resource":{"uri":"file:///a.txt","mimeType":"text/plain","text":"body"}},
              {"type":"resource","resource":{"uri":"file:///b.bin","mimeType":"application/octet-stream","blob":"CCCC"}},
              {"type":"resource_link","name":"log","uri":"file:///c.log"},
              {"type":"future_thing","text":"later"}
            ]}
            """);

        Assert.StartsWith("plain", text, StringComparison.Ordinal);
        Assert.Contains("[image: image/png · 4 base64 characters]", text, StringComparison.Ordinal);
        Assert.Contains("[audio: audio/wav · 4 base64 characters]", text, StringComparison.Ordinal);
        Assert.Contains("[resource: file:///a.txt]\nbody", text, StringComparison.Ordinal);
        Assert.Contains("[resource: file:///b.bin · application/octet-stream · 4 base64 characters]", text, StringComparison.Ordinal);
        Assert.Contains("[link: log · file:///c.log]", text, StringComparison.Ordinal);
        Assert.Contains("[future_thing]\nlater", text, StringComparison.Ordinal);
        Assert.Contains("\n\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyOrMissingContentListSaysSo()
    {
        Assert.Equal("(the npx server returned no content)", Flatten("""{"content":[]}"""));
        Assert.Equal("(the npx server answered without content)", Flatten("""{"isError":false}"""));
        Assert.Equal("(the npx server answered without content)", Flatten("""[]"""));
    }

    [Fact]
    public async Task AReadOnlyToolCarriesTheServersOwnDescriptionAndSchema()
    {
        await using var harness = new Harness(Tool("search", readOnly: true), () => Content("hit", isError: false));
        var tool = await harness.StartAsync();

        Assert.Equal(ToolSafety.ReadOnly, tool.Safety);
        Assert.Equal("npx_search", tool.Definition.Name);
        Assert.Equal("search from the test server", tool.Definition.Description);
        Assert.Contains("\"type\":\"object\"", tool.Definition.ParametersSchemaJson, StringComparison.Ordinal);

        var result = await tool.ExecuteAsync(
            new ToolCall("call-1", "npx_search", """{"q":"x"}"""),
            Context(),
            Token);

        Assert.False(result.IsError);
        Assert.Equal("hit", result.Content);
        Assert.Empty(result.Effects);
    }

    [Fact]
    public async Task AnUnannotatedToolIsDestructiveAndReportsThatAProcessRan()
    {
        await using var harness = new Harness(Tool("write_row", readOnly: false), () => Content("inserted", isError: false));
        var tool = await harness.StartAsync();

        Assert.Equal(ToolSafety.Destructive, tool.Safety);

        var result = await tool.ExecuteAsync(new ToolCall("call-1", "npx_write_row", "{}"), Context(), Token);

        var effect = Assert.Single(result.Effects);
        Assert.Equal(ToolEffectKind.CommandRun, effect.Kind);
        Assert.Equal("npx: write_row", effect.Target);
    }

    [Fact]
    public async Task AnErrorAnswerIsAFailureThatKeepsTheServersText()
    {
        await using var harness = new Harness(Tool("search", readOnly: true), () => Content("boom", isError: true));
        var tool = await harness.StartAsync();

        var result = await tool.ExecuteAsync(new ToolCall("call-1", "npx_search", "{}"), Context(), Token);

        Assert.True(result.IsError);
        Assert.Equal("boom", result.Content);
    }

    [Fact]
    public async Task ALongAnswerIsBoundedAndSaysSo()
    {
        var long_ = new string('x', 4_000);
        await using var harness = new Harness(
            Tool("search", readOnly: true),
            () => Content(long_, isError: false),
            new McpClientOptions { MaxResultChars = 1_000 });
        var tool = await harness.StartAsync();

        var result = await tool.ExecuteAsync(new ToolCall("call-1", "npx_search", "{}"), Context(), Token);

        Assert.True(result.IsTruncated);
        Assert.Contains("[truncated]", result.Content, StringComparison.Ordinal);
        Assert.True(result.Content.Length <= 1_000);
    }

    [Fact]
    public async Task ArgumentsThatAreNotAnObjectAreRefusedBeforeAnythingIsSent()
    {
        await using var harness = new Harness(Tool("search", readOnly: true), () => Content("hit", isError: false));
        var tool = await harness.StartAsync();

        var error = await Assert.ThrowsAsync<ToolArgumentException>(() => tool
            .ExecuteAsync(new ToolCall("call-1", "npx_search", "[1,2]"), Context(), Token)
            .AsTask());

        Assert.Contains("JSON object", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASchemaAndADescriptionAreOptional()
    {
        var bare = new JsonObject { ["name"] = "bare" };
        await using var harness = new Harness(bare, () => Content("hit", isError: false));
        var tool = await harness.StartAsync();

        Assert.Equal(McpToolInfo.FallbackSchema, tool.Definition.ParametersSchemaJson);
        Assert.Contains("npx -y test-server", tool.Definition.Description, StringComparison.Ordinal);
        Assert.Equal(ToolSafety.Destructive, tool.Safety);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Flatten(string json)
    {
        using var document = JsonDocument.Parse(json);
        return McpContent.Flatten(document.RootElement, "npx");
    }

    private static ToolInvocationContext Context() => new(Workspace.Create(Path.GetTempPath()));

    private static JsonObject Tool(string name, bool readOnly) => new()
    {
        ["name"] = name,
        ["description"] = string.Create(CultureInfo.InvariantCulture, $"{name} from the test server"),
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = readOnly },
    };

    private static JsonObject Content(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    /// <summary>A scripted server, its connection, and the adapter built over the first tool it offered.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private static readonly JsonObject Handshake = new()
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
        };

        private readonly ScriptedMcpServer _server;
        private readonly McpClientOptions _options;

        public Harness(JsonObject tool, Func<JsonObject> call, McpClientOptions? options = null)
        {
            _options = options ?? new McpClientOptions();
            _server = new ScriptedMcpServer(request => request.Method switch
            {
                "initialize" => ScriptedMcpServer.Answer(request.Id, Handshake.DeepClone()),
                "tools/list" => ScriptedMcpServer.Answer(
                    request.Id,
                    new JsonObject { ["tools"] = new JsonArray(tool.DeepClone()) }),
                "tools/call" => ScriptedMcpServer.Answer(request.Id, call()),
                _ => null,
            });
            Connection = new McpServerConnection("npx", "npx -y test-server", _server.Input, _server.Output, _options);
        }

        public McpServerConnection Connection { get; }

        public async Task<McpTool> StartAsync()
        {
            await Connection.StartAsync(Token).ConfigureAwait(false);
            return new McpTool("npx_search", Connection.Tools[0], Connection, _options);
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync().ConfigureAwait(false);
            await _server.DisposeAsync().ConfigureAwait(false);
        }
    }
}

