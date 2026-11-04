using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpCoreAgent.Infrastructure.Mcp;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The transport is where an MCP conversation can hang, and every rule that prevents it is asserted here over an
/// in-memory pipe rather than a child process: correlating answers, ignoring what the client does not implement,
/// failing a waiter when the server goes away, and distinguishing a timeout from the caller's own cancellation.
/// </summary>
public sealed class JsonRpcPipeTests
{
    [Fact]
    public async Task AnAnswerIsMatchedToItsOwnRequestEvenWhenTheyArriveOutOfOrder()
    {
        using var pair = new PipePair();
        await using var pipe = new JsonRpcPipe(pair.ClientInput, pair.ClientOutput);
        var server = new StreamReader(pair.ServerInput, Encoding.UTF8);
        var writer = new StreamWriter(pair.ServerOutput, new UTF8Encoding(false)) { AutoFlush = true };

        var first = pipe.RequestAsync("first", null, TimeSpan.FromSeconds(5), Token);
        var second = pipe.RequestAsync("second", null, TimeSpan.FromSeconds(5), Token);

        var one = ReadId(await ReadAsync(server));
        var two = ReadId(await ReadAsync(server));

        // Answered in the opposite order on purpose: ids, not arrival order, are what pair a request with its answer.
        await SendAsync(writer, Answer(two, "second"));
        await SendAsync(writer, Answer(one, "first"));

        Assert.Equal("second", (await second).GetProperty("which").GetString());
        Assert.Equal("first", (await first).GetProperty("which").GetString());
    }

    [Fact]
    public async Task ANotificationAndAMalformedLineAreLoggedAndIgnored()
    {
        var log = new List<string>();
        using var pair = new PipePair();
        await using var pipe = new JsonRpcPipe(pair.ClientInput, pair.ClientOutput, log.Add);
        var server = new StreamReader(pair.ServerInput, Encoding.UTF8);
        var writer = new StreamWriter(pair.ServerOutput, new UTF8Encoding(false)) { AutoFlush = true };

        var pending = pipe.RequestAsync("tools/list", null, TimeSpan.FromSeconds(5), Token);
        var id = ReadId(await ReadAsync(server));

        await SendAsync(writer, "this is not json");
        await SendAsync(writer, """{"jsonrpc":"2.0","method":"notifications/progress"}""");
        await SendAsync(writer, Answer(id, "fine"));

        Assert.Equal("fine", (await pending).GetProperty("which").GetString());
        Assert.Contains(log, line => line.Contains("not JSON-RPC", StringComparison.Ordinal));
        Assert.Contains(log, line => line.Contains("notifications/progress", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARequestFromTheServerIsAnsweredMethodNotFound()
    {
        using var pair = new PipePair();
        await using var pipe = new JsonRpcPipe(pair.ClientInput, pair.ClientOutput);
        var server = new StreamReader(pair.ServerInput, Encoding.UTF8);
        var writer = new StreamWriter(pair.ServerOutput, new UTF8Encoding(false)) { AutoFlush = true };

        // Sampling is the request a server is most likely to send, and this client implements tools only.
        await SendAsync(writer, """{"jsonrpc":"2.0","id":99,"method":"sampling/createMessage","params":{}}""");

        using var document = JsonDocument.Parse((await ReadAsync(server))!);
        Assert.Equal(99, document.RootElement.GetProperty("id").GetInt64());
        Assert.Equal(-32601, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ALoggingNotificationIsRenderedAsAServerLogLine()
    {
        var log = new List<string>();
        using var pair = new PipePair();
        await using var pipe = new JsonRpcPipe(pair.ClientInput, pair.ClientOutput, log.Add);
        var server = new StreamReader(pair.ServerInput, Encoding.UTF8);
        var writer = new StreamWriter(pair.ServerOutput, new UTF8Encoding(false)) { AutoFlush = true };

        var pending = pipe.RequestAsync("tools/list", null, TimeSpan.FromSeconds(5), Token);
        var id = ReadId(await ReadAsync(server));
        await SendAsync(
            writer,
            """{"jsonrpc":"2.0","method":"notifications/message","params":{"level":"warning","logger":"test","data":"careful"}}""");
        await SendAsync(writer, Answer(id, "fine"));

        await pending;
        Assert.Contains(log, line => line.Contains("[warning] test: careful", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANotificationIsWrittenWithoutAnIdAndWithItsParameters()
    {
        using var pair = new PipePair();
        await using var pipe = new JsonRpcPipe(pair.ClientInput, pair.ClientOutput);
        var server = new StreamReader(pair.ServerInput, Encoding.UTF8);

        await pipe.NotifyAsync("notifications/initialized", new JsonObject { ["ready"] = true }, Token);

        using var document = JsonDocument.Parse((await ReadAsync(server))!);
        var root = document.RootElement;
        Assert.Equal("notifications/initialized", root.GetProperty("method").GetString());
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.False(root.TryGetProperty("id", out _));
        Assert.True(root.GetProperty("params").GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task ASilentServerIsATimeoutThatNamesTheMethod()
    {
        using var pair = new PipePair();
        await using var pipe = new JsonRpcPipe(pair.ClientInput, pair.ClientOutput);

        var error = await Assert.ThrowsAsync<McpTransportException>(
            () => pipe.RequestAsync("initialize", null, TimeSpan.FromMilliseconds(100), Token));

        Assert.Contains("initialize", error.Message, StringComparison.Ordinal);
        Assert.Contains("0.1 seconds", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCallersOwnCancellationEscapesAsCancellation()
    {
        using var pair = new PipePair();
        await using var pipe = new JsonRpcPipe(pair.ClientInput, pair.ClientOutput);
        using var cancellation = new CancellationTokenSource();

        var pending = pipe.RequestAsync("initialize", null, TimeSpan.FromSeconds(30), cancellation.Token);
        await cancellation.CancelAsync();

        // Not a transport failure: the developer pressed Ctrl+C, and the loop decides what that means.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task AServerThatClosesItsOutputFailsTheWaiter()
    {
        using var pair = new PipePair();
        await using var pipe = new JsonRpcPipe(pair.ClientInput, pair.ClientOutput);
        var server = new StreamReader(pair.ServerInput, Encoding.UTF8);

        var pending = pipe.RequestAsync("tools/call", null, TimeSpan.FromSeconds(30), Token);
        _ = await ReadAsync(server);

        await pair.ServerOutput.DisposeAsync();

        var error = await Assert.ThrowsAsync<McpTransportException>(() => pending);
        Assert.Contains("closed", error.Message, StringComparison.Ordinal);
        Assert.True(pipe.IsClosed);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Task<string?> ReadAsync(StreamReader reader) => reader.ReadLineAsync(Token).AsTask();

    private static Task SendAsync(StreamWriter writer, string line) => writer.WriteLineAsync(line.AsMemory(), Token);

    private static string Answer(long id, string result) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JsonObject { ["which"] = result } }
            .ToJsonString();

    private static long ReadId(string? line)
    {
        Assert.NotNull(line);
        using var document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("id").GetInt64();
    }
}

