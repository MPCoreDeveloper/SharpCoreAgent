using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpCoreAgent.Infrastructure.Mcp;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The legacy HTTP+SSE channel: the GET event stream names the POST endpoint, and answers arrive as later events.
/// </summary>
public sealed class SseJsonRpcChannelTests
{
    [Fact]
    public async Task AnAnswerArrivesOnTheEventStream()
    {
        await using var handler = new SseHandler(method => method == "tools/list"
            ? new JsonObject { ["tools"] = new JsonArray() }
            : new JsonObject());

        await using var channel = new SseJsonRpcChannel(new Uri("https://mcp.example/sse"), null, handler);
        var result = await channel.RequestAsync("tools/list", null, TimeSpan.FromSeconds(5), Token);

        Assert.Equal(JsonValueKind.Array, result.GetProperty("tools").ValueKind);
        Assert.Equal("/messages", handler.PostPath);
    }

    [Fact]
    public async Task AConnectionCompletesTheHandshakeOverSse()
    {
        await using var handler = new SseHandler(method => method switch
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
        });

        await using var connection = new McpServerConnection(
            "remote",
            new Uri("https://mcp.example/sse"),
            handler,
            new McpClientOptions(),
            legacySse: true);

        await connection.StartAsync(Token);

        Assert.Equal(["search"], connection.Tools.Select(tool => tool.Name));
        Assert.True(connection.IsRunning);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class SseHandler(Func<string, JsonObject> respond) : HttpMessageHandler, IAsyncDisposable
    {
        private readonly Pipe _events = new();
        private readonly SemaphoreSlim _write = new(1, 1);

        public string? PostPath { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                await WriteAsync("event: endpoint\ndata: /messages\n\n", cancellationToken).ConfigureAwait(false);
                var content = new StreamContent(_events.Reader.AsStream());
                content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }

            PostPath = request.RequestUri?.AbsolutePath;
            var body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("id", out var id))
            {
                var method = root.GetProperty("method").GetString() ?? string.Empty;
                var answer = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id.GetInt64(),
                    ["result"] = respond(method),
                };
                await WriteAsync($"event: message\ndata: {answer.ToJsonString()}\n\n", cancellationToken).ConfigureAwait(false);
            }

            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }

        public async ValueTask DisposeAsync()
        {
            _write.Dispose();
            await _events.Writer.CompleteAsync().ConfigureAwait(false);
            await _events.Reader.CompleteAsync().ConfigureAwait(false);
        }

        private async Task WriteAsync(string text, CancellationToken cancellationToken)
        {
            await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _events.Writer.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _write.Release();
            }
        }
    }
}
