using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SharpCoreAgent.Infrastructure.Mcp;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The streamable-HTTP channel: JSON and SSE answers, the session header, no redirects, and the endpoint rule.
/// </summary>
public sealed class HttpJsonRpcChannelTests
{
    [Fact]
    public async Task AJsonAnswerIsMatchedToItsRequest()
    {
        var handler = new ScriptedHandler(request =>
        {
            var id = Id(request);
            return Json(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new JsonObject { ["answer"] = "ok" },
            });
        });

        await using var channel = new HttpJsonRpcChannel(new Uri("https://mcp.example/rpc"), null, handler);
        var result = await channel.RequestAsync("tools/list", null, TimeSpan.FromSeconds(5), Token);

        Assert.Equal("ok", result.GetProperty("answer").GetString());
    }

    [Fact]
    public async Task AnSseAnswerIsReadUntilTheMatchingEvent()
    {
        var handler = new ScriptedHandler(request =>
        {
            var id = Id(request);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"event: message\ndata: {{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"answer\":\"sse\"}}}}\n\n",
                    Encoding.UTF8,
                    "text/event-stream"),
            };
            return response;
        });

        await using var channel = new HttpJsonRpcChannel(new Uri("https://mcp.example/rpc"), null, handler);
        var result = await channel.RequestAsync("tools/call", new JsonObject(), TimeSpan.FromSeconds(5), Token);

        Assert.Equal("sse", result.GetProperty("answer").GetString());
    }

    [Fact]
    public async Task TheSessionHeaderIsSentOnTheNextRequest()
    {
        var sessions = new List<string?>();
        var handler = new ScriptedHandler(request =>
        {
            sessions.Add(request.Headers.TryGetValues("Mcp-Session-Id", out var values)
                ? values.Single()
                : null);
            var response = Json(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = Id(request),
                ["result"] = new JsonObject(),
            });
            response.Headers.TryAddWithoutValidation("Mcp-Session-Id", "session-7");
            return response;
        });

        await using var channel = new HttpJsonRpcChannel(new Uri("https://mcp.example/rpc"), null, handler);
        await channel.RequestAsync("initialize", null, TimeSpan.FromSeconds(5), Token);
        await channel.RequestAsync("tools/list", null, TimeSpan.FromSeconds(5), Token);

        Assert.Null(sessions[0]);
        Assert.Equal("session-7", sessions[1]);
    }

    [Fact]
    public async Task ANonSuccessStatusIsATransportFailureNamingIt()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        await using var channel = new HttpJsonRpcChannel(new Uri("https://mcp.example/rpc"), null, handler);

        var error = await Assert.ThrowsAsync<McpTransportException>(
            () => channel.RequestAsync("tools/list", null, TimeSpan.FromSeconds(5), Token));

        Assert.Contains("502", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlainHttpIsAllowedOnlyForLoopback()
    {
        Assert.Throws<ArgumentException>(() => new HttpJsonRpcChannel(new Uri("http://mcp.example/rpc")));
        await using var loopback = new HttpJsonRpcChannel(new Uri("http://127.0.0.1:3000/rpc"));
    }

    [Fact]
    public async Task AConnectionCompletesItsHandshakeOverHttp()
    {
        var handler = new ScriptedHandler(request =>
        {
            using var body = System.Text.Json.JsonDocument.Parse(
                request.Content!.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult());
            var root = body.RootElement;
            if (!root.TryGetProperty("id", out var id))
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            var result = root.GetProperty("method").GetString() switch
            {
                "initialize" => new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                },
                "tools/list" => new JsonObject
                {
                    ["tools"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "search",
                        ["description"] = "search",
                        ["inputSchema"] = new JsonObject { ["type"] = "object" },
                        ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
                    }),
                },
                _ => new JsonObject(),
            };

            return Json(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id.GetInt64(),
                ["result"] = result,
            });
        });

        await using var connection = new McpServerConnection(
            "remote",
            new Uri("https://mcp.example/rpc"),
            handler,
            new McpClientOptions());

        await connection.StartAsync(Token);

        Assert.Equal(["search"], connection.Tools.Select(tool => tool.Name));
        Assert.True(connection.IsRunning);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static long Id(HttpRequestMessage request)
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            request.Content!.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult());
        return document.RootElement.GetProperty("id").GetInt64();
    }

    private static HttpResponseMessage Json(JsonObject body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
