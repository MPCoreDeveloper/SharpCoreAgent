using System.Text.Json;
using System.Text.Json.Nodes;
using SharpCoreAgent.Infrastructure.Mcp;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The connection, over a scripted server: the handshake in protocol order, paginated tool lists, arguments
/// forwarded byte for byte, and every way a call can fail becoming a result rather than an exception.
/// </summary>
public sealed class McpProtocolTests
{
    [Fact]
    public async Task StartupSendsTheHandshakeInProtocolOrder()
    {
        await using var server = new ScriptedMcpServer(request => request.Method switch
        {
            "initialize" => Initialized(request),
            "tools/list" => ScriptedMcpServer.Answer(request.Id, new JsonObject
            {
                ["tools"] = new JsonArray(Tool("search", readOnly: true), Tool("write_row", readOnly: false)),
            }),
            _ => null,
        });

        await using var connection = Connect(server);
        await connection.StartAsync(Token);

        Assert.Equal(["initialize", "notifications/initialized", "tools/list"], server.Methods);
        Assert.Equal(["search", "write_row"], connection.Tools.Select(tool => tool.Name));
        Assert.Equal("2025-06-18", connection.ProtocolVersion);

        var parameters = server.Received[0].Parameters ?? throw new InvalidOperationException("no initialize params");
        Assert.Equal("2025-06-18", parameters.GetProperty("protocolVersion").GetString());
        Assert.Equal("sharpcoreagent", parameters.GetProperty("clientInfo").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Object, parameters.GetProperty("capabilities").ValueKind);
    }

    [Fact]
    public async Task ToolsAreReadAcrossPagesWithTheCursorTheServerGave()
    {
        await using var server = new ScriptedMcpServer(request => request.Method switch
        {
            "initialize" => Initialized(request),
            "tools/list" when request.Cursor is null => ScriptedMcpServer.Answer(request.Id, new JsonObject
            {
                ["tools"] = new JsonArray(Tool("one", readOnly: true)),
                ["nextCursor"] = "page-2",
            }),
            "tools/list" => ScriptedMcpServer.Answer(request.Id, new JsonObject
            {
                ["tools"] = new JsonArray(Tool("two", readOnly: true)),
            }),
            _ => null,
        });

        await using var connection = Connect(server);
        await connection.StartAsync(Token);

        Assert.Equal(["one", "two"], connection.Tools.Select(tool => tool.Name));

        var pages = server.Received.Where(received => received.Method == "tools/list").ToArray();
        Assert.Null(pages[0].Cursor);
        Assert.Equal("page-2", pages[1].Cursor);
    }

    [Fact]
    public async Task ACallForwardsItsArgumentsUnchangedAndReturnsTheFlattenedText()
    {
        await using var server = new ScriptedMcpServer(request => request.Method switch
        {
            "initialize" => Initialized(request),
            "tools/list" => ScriptedMcpServer.Answer(request.Id, new JsonObject
            {
                ["tools"] = new JsonArray(Tool("write_row", readOnly: false)),
            }),
            "tools/call" => ScriptedMcpServer.Answer(request.Id, new JsonObject
            {
                ["content"] = new JsonArray(Text("row inserted")),
            }),
            _ => null,
        });

        await using var connection = Connect(server);
        await connection.StartAsync(Token);

        const string Payload = """{"table":"t","values":{"a":1,"b":[1,2]}}""";
        var result = await connection.CallAsync("write_row", Arguments(Payload), TimeSpan.FromSeconds(5), Token);

        Assert.False(result.IsError);
        Assert.Equal("row inserted", result.Text);

        var call = server.Received.Single(received => received.Method == "tools/call");
        var parameters = call.Parameters ?? throw new InvalidOperationException("no call params");
        Assert.Equal("write_row", parameters.GetProperty("name").GetString());
        Assert.Equal(Payload, parameters.GetProperty("arguments").GetRawText());
    }

    [Fact]
    public async Task AnErrorResultFromTheServerIsAFailureThatKeepsItsText()
    {
        await using var server = new ScriptedMcpServer(request => request.Method switch
        {
            "initialize" => Initialized(request),
            "tools/list" => ScriptedMcpServer.Answer(request.Id, new JsonObject
            {
                ["tools"] = new JsonArray(Tool("write_row", readOnly: false)),
            }),
            "tools/call" => ScriptedMcpServer.Answer(request.Id, new JsonObject
            {
                ["content"] = new JsonArray(Text("duplicate key")),
                ["isError"] = true,
            }),
            _ => null,
        });

        await using var connection = Connect(server);
        await connection.StartAsync(Token);

        var result = await connection.CallAsync("write_row", Arguments("{}"), TimeSpan.FromSeconds(5), Token);

        Assert.True(result.IsError);
        Assert.Equal("duplicate key", result.Text);
    }

    [Fact]
    public async Task AJsonRpcErrorIsAFailureNamingItsCode()
    {
        await using var server = new ScriptedMcpServer(request => request.Method switch
        {
            "initialize" => Initialized(request),
            "tools/list" => ScriptedMcpServer.Answer(request.Id, new JsonObject
            {
                ["tools"] = new JsonArray(Tool("search", readOnly: true)),
            }),
            "tools/call" => ScriptedMcpServer.Fail(request.Id, -32000, "table does not exist"),
            _ => null,
        });

        await using var connection = Connect(server);
        await connection.StartAsync(Token);

        var result = await connection.CallAsync("search", Arguments("{}"), TimeSpan.FromSeconds(5), Token);

        Assert.True(result.IsError);
        Assert.Contains("-32000", result.Text, StringComparison.Ordinal);
        Assert.Contains("table does not exist", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerThatGoesAwayCostsOneCallRatherThanTheSession()
    {
        ScriptedMcpServer? dying = null;
        await using var server = new ScriptedMcpServer(request => request.Method switch
        {
            "initialize" => Initialized(request),
            "tools/list" => ScriptedMcpServer.Answer(request.Id, new JsonObject
            {
                ["tools"] = new JsonArray(Tool("search", readOnly: true)),
            }),
            "tools/call" => Die(dying),
            _ => null,
        });
        dying = server;

        await using var connection = Connect(server);
        await connection.StartAsync(Token);

        var result = await connection.CallAsync("search", Arguments("{}"), TimeSpan.FromSeconds(5), Token);

        Assert.True(result.IsError);
        Assert.Contains("stopped answering", result.Text, StringComparison.Ordinal);
        Assert.Contains("cannot be restarted", result.Text, StringComparison.Ordinal);
        Assert.Equal(0, connection.RestartCount);

        var again = await connection.CallAsync("search", Arguments("{}"), TimeSpan.FromSeconds(5), Token);
        Assert.True(again.IsError);
        Assert.Equal(0, connection.RestartCount);
    }

    [Fact]
    public async Task AHandshakeThatNeverArrivesIsAStartupFailureNamingTheCommand()
    {
        await using var server = new ScriptedMcpServer(_ => null);
        await using var connection = new McpServerConnection(
            "npx",
            "npx -y slow-server",
            server.Input,
            server.Output,
            new McpClientOptions { ControlTimeout = TimeSpan.FromSeconds(1) });

        var error = await Assert.ThrowsAsync<McpStartupException>(() => connection.StartAsync(Token));

        Assert.Contains("npx -y slow-server", error.Message, StringComparison.Ordinal);
        Assert.Contains("initialize", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerThatOffersNoToolsIsAStartupFailure()
    {
        await using var server = new ScriptedMcpServer(request => request.Method switch
        {
            "initialize" => Initialized(request),
            "tools/list" => ScriptedMcpServer.Answer(request.Id, new JsonObject { ["tools"] = new JsonArray() }),
            _ => null,
        });

        await using var connection = Connect(server);

        var error = await Assert.ThrowsAsync<McpStartupException>(() => connection.StartAsync(Token));

        Assert.Contains("offered no tools", error.Message, StringComparison.Ordinal);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static McpServerConnection Connect(ScriptedMcpServer server) =>
        new("npx", "npx -y test-server", server.Input, server.Output, new McpClientOptions());

    private static JsonObject Initialized(ScriptedRequest request) => ScriptedMcpServer.Answer(request.Id, new JsonObject
    {
        ["protocolVersion"] = "2025-06-18",
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1.0.0" },
    });

    private static JsonObject? Die(ScriptedMcpServer? server)
    {
        server?.Close();
        return null;
    }

    private static JsonObject Tool(string name, bool readOnly) => new()
    {
        ["name"] = name,
        ["description"] = $"{name} from the scripted server",
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = readOnly },
    };

    private static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

    private static JsonElement Arguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

