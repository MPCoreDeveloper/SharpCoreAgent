using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SharpCoreAgent.Application;
using SharpCoreAgent.DependencyInjection;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.McpServer;
using SharpCoreAgent.Infrastructure.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The two tool methods on the wire: what <c>tools/list</c> advertises, what <c>tools/call</c> answers, and the two
/// ways a call can be ended from the client's side.
/// </summary>
/// <remarks>
/// A real tool's text is compared with the text that same tool returned in process, never with a literal written here:
/// the promise is that the server adds nothing and removes nothing, and a literal in a test would keep passing after
/// the tool's wording changed. A stand-in tool is used where the tool's own answer is not the subject — the rendering
/// of a success, the shape of a cancellation — so that one rule fails one test. The cancellation tests use a pipe
/// rather than a memory stream because the notification has to arrive while the call is still running, which a fixed
/// body cannot express.
/// </remarks>
public sealed class McpServerToolsTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "sca-mcp-tools-" + Guid.NewGuid().ToString("N"));

    /// <summary>Initialises the test with an empty workspace directory the session may answer within.</summary>
    public McpServerToolsTests() => Directory.CreateDirectory(_workspace);

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
    public async Task ToolsListAdvertisesTheSixToolsInOnePage()
    {
        using var provider = Build();

        var (answers, exit, _) = await ServeAsync(
            provider.GetRequiredService<IToolRegistry>(),
            """{"jsonrpc":"2.0","id":30,"method":"tools/list"}""");

        Assert.Equal(0, exit);
        var result = Root(Assert.Single(answers)).GetProperty("result");
        var tools = result.GetProperty("tools");

        Assert.Equal(6, tools.GetArrayLength());
        Assert.Equal(
            ["code_context", "find_references", "find_symbol", "index_status", "search_code", "view_image"],
            tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()));

        // No nextCursor, because one page holds the set: a client that paged anyway would ask for a page that does
        // not exist.
        Assert.False(result.TryGetProperty("nextCursor", out _));

        Assert.All(
            tools.EnumerateArray(),
            tool =>
            {
                Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
                Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString()));
                Assert.Equal("object", tool.GetProperty("inputSchema").GetProperty("type").GetString());
            });
    }

    [Fact]
    public async Task ACursorIsIgnoredRatherThanRefused()
    {
        const string Paged = """{"jsonrpc":"2.0","id":31,"method":"tools/list","params":{"cursor":"page-2"}}""";
        using var provider = Build();

        var (answers, _, log) = await ServeAsync(provider.GetRequiredService<IToolRegistry>(), Paged);

        var result = Root(Assert.Single(answers)).GetProperty("result");
        Assert.Equal(6, result.GetProperty("tools").GetArrayLength());
        Assert.False(result.TryGetProperty("nextCursor", out _));
        Assert.Contains(log, line => line.Contains("cursor was ignored", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASuccessfulCallPassesTheToolsTextThroughUnchanged()
    {
        // Two lines of text in one JSON string: the transport must escape the newline rather than break the frame.
        const string TwoLines = "fresh, lean tier\n412 files, 0 stale";
        var tool = new CannedTool(IndexStatusTool.Name, TwoLines);

        var (answers, _, _) = await ServeAsync(new ToolRegistry([tool]), Call(40, IndexStatusTool.Name));

        var result = Root(Assert.Single(answers)).GetProperty("result");
        var content = Assert.Single(result.GetProperty("content").EnumerateArray());

        Assert.Equal("text", content.GetProperty("type").GetString());
        Assert.Equal(TwoLines, content.GetProperty("text").GetString());

        // Absent rather than false: the specification's default is false, and a client that reads the absence and a
        // client that reads false must agree.
        Assert.False(result.TryGetProperty("isError", out _));
    }

    [Fact]
    public async Task AToolFailureIsAnAnswerWithTheToolsOwnWording()
    {
        // The real tool, with no index: its refusal is the contract's text, and the server must not reword it.
        var tool = new SearchCodeTool(index: null);
        var expected = await tool.ExecuteAsync(
            new ToolCall("x", SearchCodeTool.Name, """{"query":"neighbourhood"}"""),
            new ToolInvocationContext(Workspace.Create(_workspace)),
            TestContext.Current.CancellationToken);

        var (answers, _, _) = await ServeAsync(
            new ToolRegistry([tool]),
            Call(42, SearchCodeTool.Name, """{"query":"neighbourhood"}"""));

        var answer = Root(Assert.Single(answers));

        // Not a JSON-RPC error: the model can read a sentence about the index and act on it, and a protocol error
        // would stop at the client library.
        Assert.False(answer.TryGetProperty("error", out _));
        Assert.True(answer.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal(expected.Content, Text(answers[0]));
    }

    [Fact]
    public async Task ArgumentsTheToolRejectsBecomeTheToolsOwnFailure()
    {
        var (answers, _, _) = await ServeAsync(
            new ToolRegistry([new SearchCodeTool(index: null)]),
            Call(43, SearchCodeTool.Name, "\"not an object\""));

        var answer = Root(Assert.Single(answers));

        Assert.False(answer.TryGetProperty("error", out _));
        Assert.True(answer.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains("must be a JSON object", Text(answers[0]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnImageToolAnswerCarriesAnImagePart()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        await File.WriteAllBytesAsync(
            Path.Combine(_workspace, "shot.png"),
            bytes,
            TestContext.Current.CancellationToken);

        var (answers, _, _) = await ServeAsync(
            new ToolRegistry([new ViewImageTool(allowWithoutModel: true)]),
            Call(48, ViewImageTool.Name, """{"path":"shot.png"}"""));

        var result = Root(Assert.Single(answers)).GetProperty("result");
        var content = result.GetProperty("content");

        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());

        var image = content[1];
        Assert.Equal("image", image.GetProperty("type").GetString());
        Assert.Equal("image/png", image.GetProperty("mimeType").GetString());
        Assert.Equal(Convert.ToBase64String(bytes), image.GetProperty("data").GetString());
    }

    [Fact]
    public async Task AToolOutsideTheAllowListIsInvalidParametersAndTheLoopSurvives()
    {
        using var provider = Build();

        var (answers, _, _) = await ServeAsync(
            provider.GetRequiredService<IToolRegistry>(),
            Call(44, WriteFileTool.Name, """{"path":"a.txt","content":"hi"}"""),
            """{"jsonrpc":"2.0","id":45,"method":"ping"}""");

        var refused = Root(answers[0]);
        Assert.Equal(44, refused.GetProperty("id").GetInt64());
        Assert.Equal(-32602, refused.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(
            "unknown tool 'write_file'; this server publishes 6 evidence tools",
            refused.GetProperty("error").GetProperty("message").GetString());

        Assert.Equal(45, Root(answers[1]).GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task ParamsThatAreNotAnObjectAreInvalidParameters()
    {
        using var provider = Build();

        var (answers, _, _) = await ServeAsync(
            provider.GetRequiredService<IToolRegistry>(),
            """{"jsonrpc":"2.0","id":46,"method":"tools/call","params":["search_code"]}""");

        var error = Root(Assert.Single(answers)).GetProperty("error");
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
        Assert.Contains("params must be an object", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledCallIsNeverAnsweredAndTheSessionKeepsServing()
    {
        await using var live = Connect(new ToolRegistry([new BlockingTool()]));

        live.Send(Call(50, IndexStatusTool.Name));
        live.Send("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":50,"reason":"stop"}}""");
        live.Send("""{"jsonrpc":"2.0","id":51,"method":"ping"}""");

        var answers = await live.WaitForAsync(1);
        var (all, exit) = await live.FinishAsync();

        // One answer for one live request: the call was abandoned silently, which is what the specification says a
        // cancelled request gets, and the session carried on rather than treating the notification as a message.
        Assert.Equal(0, exit);
        Assert.Equal([51L], all.Select(answer => Root(answer).GetProperty("id").GetInt64()));
        Assert.Single(answers);
    }

    [Fact]
    public async Task ACancellationThatNamesAnotherCallLeavesThisOneRunning()
    {
        var tool = new GatedTool();
        await using var live = Connect(new ToolRegistry([tool]));

        live.Send(Call(60, IndexStatusTool.Name));
        live.Send("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":61}}""");
        live.Send("""{"jsonrpc":"2.0","id":62,"method":"ping"}""");

        // Released after the cancellation has had every chance to arrive: a server that cancelled whatever was
        // running would never answer 60 at all.
        await Task.Delay(200, TestContext.Current.CancellationToken);
        tool.Release();

        await live.WaitForAsync(2);
        var (all, exit) = await live.FinishAsync();

        Assert.Equal(0, exit);
        Assert.Equal([60L, 62L], all.Select(answer => Root(answer).GetProperty("id").GetInt64()));
    }

    [Fact]
    public async Task ArgumentsThatAreAbsentMeanAnEmptyObject()
    {
        var tool = new CannedTool(IndexStatusTool.Name, "answered");

        var (answers, _, _) = await ServeAsync(
            new ToolRegistry([tool]),
            """{"jsonrpc":"2.0","id":41,"method":"tools/call","params":{"name":"index_status"}}""");

        Assert.Single(answers);
        Assert.Equal("answered", Text(answers[0]));
        Assert.Equal("{}", tool.LastArguments);
    }

    [Fact]
    public async Task ACallIsInvokedReadOnlyInTheSessionsOwnWorkspace()
    {
        var tool = new CannedTool(IndexStatusTool.Name, "answered");

        // The arguments arrive with their values intact; the text is re-serialised rather than copied byte for
        // byte, so a client's whitespace is not what a tool's JSON reader sees.
        var (answers, _, _) = await ServeAsync(
            new ToolRegistry([tool]),
            Call(47, IndexStatusTool.Name, """{ "query" : "a neighbourhood" }"""));

        Assert.Single(answers);
        Assert.Equal("""{"query":"a neighbourhood"}""", tool.LastArguments);

        // The context is what a tool hands to anything it delegates to, so what it says about approval and the root
        // is this session's promise and not the tool's guess.
        Assert.Equal(ApprovalMode.ReadOnly, tool.LastApproval);
        Assert.Equal(Path.GetFullPath(_workspace), tool.LastWorkspace, ignoreCase: true);
    }

    private static string Call(int id, string name, JsonNode? arguments = null)
    {
        var parameters = new JsonObject { ["name"] = name };
        if (arguments is not null)
        {
            parameters["arguments"] = arguments;
        }

        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "tools/call",
            ["params"] = parameters,
        }.ToJsonString();
    }

    private static string Call(int id, string name, string arguments) =>
        Call(id, name, JsonNode.Parse(arguments));

    private static string Text(string answer) =>
        Root(answer).GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    private static JsonElement Root(string answer)
    {
        using var document = JsonDocument.Parse(answer);
        return document.RootElement.Clone();
    }

    private static IReadOnlyList<string> Read(MemoryStream output) =>
        [.. Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>Runs a session over a fixed body, so what it answers is the whole of what it was asked.</summary>
    private async Task<(IReadOnlyList<string> Answers, int Exit, List<string> Log)> ServeAsync(
        IToolRegistry registry,
        params string[] lines)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => line + "\n"))));
        using var output = new MemoryStream();
        var log = new List<string>();

        int exit;
        await using (var session = new McpServerSession(
            input,
            output,
            registry,
            Workspace.Create(_workspace),
            new McpServerOptions(),
            log.Add))
        {
            exit = await session.RunAsync(TestContext.Current.CancellationToken);
        }

        return (Read(output), exit, log);
    }

    /// <summary>
    /// Builds the host's container, so the registry a session is given is the one the product registers rather than a
    /// list written here: the filter has to be shown rejecting real tools, not toys.
    /// </summary>
    private ServiceProvider Build()
    {
        var services = new ServiceCollection();

        // Web access is on purpose: those tools are read-only too, and none of them may reach a client.
        services.AddSharpCoreAgent(new SharpCoreAgentOptions
        {
            WorkspaceRoot = _workspace,
            WebAccessEnabled = true,
            WebAllowedHosts = ["localhost"],
        });

        return services.BuildServiceProvider();
    }

    /// <summary>Opens a session the test can talk to line by line.</summary>
    private Live Connect(IToolRegistry registry) => new(registry, _workspace);

    /// <summary>
    /// A session fed one line at a time over a pipe, so a notification can be sent at the moment that matters.
    /// </summary>
    /// <remarks>
    /// A fixed body cannot express the race the call path exists for: the cancellation has to arrive while the tool is
    /// still running, which means the client must still be holding a live end of stdin when it writes.
    /// </remarks>
    private sealed class Live : IAsyncDisposable
    {
        private readonly AnonymousPipeServerStream _stdin = new(PipeDirection.In);
        private readonly MemoryStream _stdout = new();
        private readonly StreamWriter _client;
        private readonly McpServerSession _session;
        private readonly Task<int> _running;

        public Live(IToolRegistry registry, string workspace)
        {
            _client = new StreamWriter(
                new AnonymousPipeClientStream(PipeDirection.Out, _stdin.ClientSafePipeHandle),
                new UTF8Encoding(false))
            {
                AutoFlush = true,
            };

            _session = new McpServerSession(
                _stdin,
                _stdout,
                registry,
                Workspace.Create(workspace),
                new McpServerOptions());
            _running = _session.RunAsync(TestContext.Current.CancellationToken);
        }

        public void Send(string line) => _client.WriteLine(line);

        public async Task<IReadOnlyList<string>> WaitForAsync(int count)
        {
            for (var attempt = 0; attempt < 500; attempt++)
            {
                var answers = CompleteAnswers();
                if (answers.Count >= count)
                {
                    return answers;
                }

                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            throw new TimeoutException(
                string.Create(CultureInfo.InvariantCulture, $"expected {count} answers, saw {CompleteAnswers().Count}"));
        }

        /// <summary>Closes the client's end, which is the end of input, and reports how the session finished.</summary>
        /// <returns>Every answer, and the exit code.</returns>
        public async Task<(IReadOnlyList<string> Answers, int Exit)> FinishAsync()
        {
            await _client.DisposeAsync().ConfigureAwait(false);

            var exit = await _running;

            // Read after the session has finished: the last answer is only complete once the loop has stopped.
            return (CompleteAnswers(), exit);
        }

        public async ValueTask DisposeAsync()
        {
            // Closing the client's end first is what ends the session, even when a test failed before it could
            // finish: the loop must not be left waiting on a pipe nobody will write to.
            await _client.DisposeAsync().ConfigureAwait(false);

            try
            {
                await _running.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The test's own cancellation: cleanup is not the place to report it.
            }

            await _session.DisposeAsync().ConfigureAwait(false);
            await _stdin.DisposeAsync().ConfigureAwait(false);
            await _stdout.DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>Reads only the lines the session has finished writing, so a poll never returns half an answer.</summary>
        private IReadOnlyList<string> CompleteAnswers()
        {
            var text = Encoding.UTF8.GetString(_stdout.ToArray());
            var end = text.LastIndexOf('\n');
            return end < 0 ? [] : [.. text[..end].Split('\n')];
        }
    }

    /// <summary>A published tool whose answer the test wrote, so the server's rendering of a success is provable.</summary>
    private sealed class CannedTool(string name, string text) : ITool
    {
        public string? LastArguments { get; private set; }

        public ApprovalMode? LastApproval { get; private set; }

        public string? LastWorkspace { get; private set; }

        public ToolDefinition Definition { get; } =
            new(name, "an evidence tool a test stood in for", """{"type":"object","properties":{},"required":[]}""");

        public ToolSafety Safety => ToolSafety.ReadOnly;

        public ValueTask<ToolResult> ExecuteAsync(
            ToolCall toolCall,
            ToolInvocationContext context,
            CancellationToken cancellationToken)
        {
            LastArguments = toolCall.ArgumentsJson;
            LastApproval = context.Approval;
            LastWorkspace = context.Workspace.Root;
            return ValueTask.FromResult(ToolResult.Success(toolCall.Id, toolCall.Name, text));
        }
    }

    /// <summary>A call that never ends on its own, so a cancellation is the only way it can be finished.</summary>
    private sealed class BlockingTool : ITool
    {
        public ToolDefinition Definition { get; } =
            new(IndexStatusTool.Name, "a call that never ends on its own", """{"type":"object","properties":{},"required":[]}""");

        public ToolSafety Safety => ToolSafety.ReadOnly;

        public async ValueTask<ToolResult> ExecuteAsync(
            ToolCall toolCall,
            ToolInvocationContext context,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return ToolResult.Success(toolCall.Id, toolCall.Name, "unreachable");
        }
    }

    /// <summary>A call that ends when the test says so, so a cancellation can be shown not to reach it.</summary>
    private sealed class GatedTool : ITool
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ToolDefinition Definition { get; } =
            new(IndexStatusTool.Name, "a call the test releases", """{"type":"object","properties":{},"required":[]}""");

        public ToolSafety Safety => ToolSafety.ReadOnly;

        /// <summary>Lets the call return as though the tool had finished its work.</summary>
        public void Release() => _released.TrySetResult();

        public async ValueTask<ToolResult> ExecuteAsync(
            ToolCall toolCall,
            ToolInvocationContext context,
            CancellationToken cancellationToken)
        {
            await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return ToolResult.Success(toolCall.Id, toolCall.Name, "released");
        }
    }
}

