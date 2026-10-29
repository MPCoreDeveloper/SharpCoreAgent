using System.Text;
using System.Text.Json;
using SharpCoreAgent.Infrastructure.McpServer;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The server's half of the wire, asserted over two memory streams rather than a child process: what the
/// handshake promises, which errors carry an id and which cannot, and the fact that a malformed line costs one
/// answer instead of the loop.
/// </summary>
public sealed class McpServerSessionTests
{
    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"copilot","version":"1.0"}}}""";

    [Fact]
    public async Task InitializeEchoesAKnownVersionAndNamesTheServer()
    {
        var (answers, exit, _) = await ServeAsync(Initialize);

        Assert.Equal(0, exit);
        var root = Root(Assert.Single(answers));
        Assert.Equal(1, root.GetProperty("id").GetInt64());

        var result = root.GetProperty("result");
        Assert.Equal("2025-06-18", result.GetProperty("protocolVersion").GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
        Assert.Equal("sharpcoreagent", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("serverInfo").GetProperty("version").GetString()));
    }

    [Fact]
    public async Task AnUnknownProtocolVersionFallsBackToTheOneThisBuildSpeaks()
    {
        const string Asked = """{"jsonrpc":"2.0","id":7,"method":"initialize","params":{"protocolVersion":"2099-01-01"}}""";

        var (answers, _, _) = await ServeAsync(Asked);
        var root = Root(Assert.Single(answers));

        Assert.Equal(7, root.GetProperty("id").GetInt64());
        Assert.Equal(
            McpServerOptions.DefaultProtocolVersion,
            root.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task AnInitializeWithoutProtocolVersionIsStillAnswered()
    {
        const string Bare = """{"jsonrpc":"2.0","id":2,"method":"initialize"}""";

        var (answers, _, _) = await ServeAsync(Bare);
        var result = Root(Assert.Single(answers)).GetProperty("result");

        Assert.Equal(McpServerOptions.DefaultProtocolVersion, result.GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task PingAnswersAnEmptyObject()
    {
        const string Ping = """{"jsonrpc":"2.0","id":3,"method":"ping"}""";

        var (answers, _, _) = await ServeAsync(Ping);
        var root = Root(Assert.Single(answers));

        Assert.Equal(3, root.GetProperty("id").GetInt64());
        Assert.Empty(root.GetProperty("result").EnumerateObject());
    }

    [Fact]
    public async Task AnUnknownMethodAnswersMethodNotFoundAndTheLoopKeepsGoing()
    {
        const string Prompt = """{"jsonrpc":"2.0","id":4,"method":"prompts/list"}""";
        const string Ping = """{"jsonrpc":"2.0","id":5,"method":"ping"}""";

        var (answers, _, _) = await ServeAsync(Prompt, Ping);

        Assert.Equal(2, answers.Count);
        var refused = Root(answers[0]);
        Assert.Equal(-32601, refused.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains(
            "prompts/list",
            refused.GetProperty("error").GetProperty("message").GetString()!,
            StringComparison.Ordinal);
        Assert.True(Root(answers[1]).TryGetProperty("result", out _));
    }

    [Fact]
    public async Task AMalformedBodyAnswersParseErrorWithANullId()
    {
        var (answers, _, _) = await ServeAsync("this is not json");

        var root = Root(Assert.Single(answers));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("id").ValueKind);
        Assert.Equal(-32700, root.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ABodyThatIsNotAnObjectIsAnInvalidRequest()
    {
        var (answers, _, _) = await ServeAsync("[1,2,3]");

        var root = Root(Assert.Single(answers));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("id").ValueKind);
        Assert.Equal(-32600, root.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ARequestWithNoMethodKeepsItsIdAndNamesTheProblem()
    {
        const string Methodless = """{"jsonrpc":"2.0","id":9,"params":{}}""";

        var (answers, _, _) = await ServeAsync(Methodless);

        var root = Root(Assert.Single(answers));
        Assert.Equal(9, root.GetProperty("id").GetInt64());
        Assert.Equal(-32600, root.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains(
            "no method",
            root.GetProperty("error").GetProperty("message").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotificationsAreNeverAnswered()
    {
        const string Ready = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
        const string Progress = """{"jsonrpc":"2.0","method":"notifications/progress","params":{"progress":1}}""";
        const string Ping = """{"jsonrpc":"2.0","id":6,"method":"ping"}""";

        var (answers, _, log) = await ServeAsync(Ready, Progress, Ping);

        // One answer for three messages: the two notifications were read and dropped, which is what a client
        // expects rather than a courtesy.
        var only = Root(Assert.Single(answers));
        Assert.Equal(6, only.GetProperty("id").GetInt64());
        Assert.Contains(log, line => line.Contains("ready", StringComparison.Ordinal));
        Assert.Contains(log, line => line.Contains("notifications/progress", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BlankLinesAreIgnoredAndEndOfInputExitsZero()
    {
        const string Ping = """{"jsonrpc":"2.0","id":8,"method":"ping"}""";

        var (answers, exit, _) = await ServeAsync(string.Empty, Ping, "   ");

        Assert.Equal(0, exit);
        Assert.Single(answers);
    }

    [Fact]
    public async Task AnswersArriveInTheOrderTheRequestsDid()
    {
        var (answers, _, _) = await ServeAsync(
            """{"jsonrpc":"2.0","id":11,"method":"ping"}""",
            """{"jsonrpc":"2.0","id":12,"method":"prompts/list"}""",
            """{"jsonrpc":"2.0","id":13,"method":"ping"}""");

        Assert.Equal(
            [11L, 12L, 13L],
            answers.Select(answer => Root(answer).GetProperty("id").GetInt64()));
    }

    private static async Task<(IReadOnlyList<string> Answers, int Exit, List<string> Log)> ServeAsync(
        params string[] lines)
    {
        var body = lines.Length == 0 ? string.Empty : string.Join('\n', lines) + "\n";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(body));
        using var output = new MemoryStream();
        var log = new List<string>();

        int exit;
        await using (var session = new McpServerSession(input, output, new McpServerOptions(), log.Add))
        {
            exit = await session.RunAsync(TestContext.Current.CancellationToken);
        }

        output.Position = 0;
        using var reader = new StreamReader(output, Encoding.UTF8);
        var answers = new List<string>();
        while (await reader.ReadLineAsync(TestContext.Current.CancellationToken) is { } answer)
        {
            answers.Add(answer);
        }

        return (answers, exit, log);
    }

    private static JsonElement Root(string answer)
    {
        // Cloned, because the document a JsonElement came from is disposed before the caller asserts on it.
        using var document = JsonDocument.Parse(answer);
        return document.RootElement.Clone();
    }
}
