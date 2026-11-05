using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SharpCoreAgent.Cli;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Indexing;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// <c>mcp serve</c> end to end, minus the OS: the container is built, the ready line is written, the loop answers
/// and the process exits at the documented code — over memory streams instead of the process's own stdio.
/// </summary>
/// <remarks>
/// The streams are the command's parameters precisely so this can exist: a test that had to launch a child
/// process could assert the same things, but only by waiting on a pipe, where a failure arrives as a timeout
/// rather than as a wrong line. The one thing that truly needs a second process — a build taking the index while a
/// server is alive — is asserted against the real index in <c>LeasedCodeIndexTests</c> and was also measured
/// against the built binary during the phase.
/// </remarks>
public sealed class McpServeCommandTests
{
    private const string CircleSource = """
        namespace Demo.Geometry;

        public sealed class Circle
        {
            private readonly double _radius;

            public double Circumference() => 2 * 3.14 * _radius;
        }
        """;

    [Fact]
    public async Task TheReadyLineGoesToStderrAndStdoutStaysEmpty()
    {
        using var directory = TempDirectory.Create();

        var (exit, answers, diagnostics) = await ServeAsync(directory.Path, input: string.Empty);

        Assert.Equal(0, exit);

        // Not one byte on stdout: a client that read a banner there would spend the rest of the session
        // resynchronising on JSON it never asked for.
        Assert.Empty(answers);

        var line = Assert.Single(Lines(diagnostics));
        Assert.StartsWith("sharpcoreagent mcp serve: ", line, StringComparison.Ordinal);
        Assert.Contains("index missing", line, StringComparison.Ordinal);
        Assert.Contains("· 6 tools", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoRefreshSaysSoOnceRatherThanOnEveryCall()
    {
        using var directory = TempDirectory.Create();

        var (_, _, diagnostics) = await ServeAsync(directory.Path, input: string.Empty, noRefresh: true);

        Assert.Equal(2, Lines(diagnostics).Length);
        Assert.Contains("refresh is off (--no-refresh)", Lines(diagnostics)[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHandshakeIsAnsweredAndEndOfInputExitsZero()
    {
        using var directory = TempDirectory.Create();

        var (exit, answers, _) = await ServeAsync(
            directory.Path,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");

        Assert.Equal(0, exit);
        var result = Root(Assert.Single(answers)).GetProperty("result");
        Assert.Equal("2025-06-18", result.GetProperty("protocolVersion").GetString());
        Assert.Equal("sharpcoreagent", result.GetProperty("serverInfo").GetProperty("name").GetString());
    }

    [Fact]
    public async Task ACallIsAnsweredFromTheIndexThatIsAlreadyThere()
    {
        using var directory = TempDirectory.Create();
        var file = Path.Combine(directory.Path, "src", "Circle.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, CircleSource, Token);

        // A build in this process, then released: the server reads the file the way `sharpcoreagent index` left it,
        // which is also the shape the lease-per-call design has to cope with.
        await using (var index = SharpCoreAgentIndex.Open(Workspace.Create(directory.Path)))
        {
            await index.EnsureFreshAsync(TimeSpan.FromSeconds(60), Token);
        }

        // A pipe rather than a fixed body, because this call is real work: stdin has to stay open until the answer
        // exists, and a memory stream would report end of input while the search was still running — which cancels
        // the call, correctly, and would prove nothing about the tool.
        await using var serving = new Serving(directory.Path);
        serving.Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");
        serving.Send("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"search_code","arguments":{"query":"Circumference"}}}""");

        var (answers, exit, _) = await serving.FinishAsync(2);

        Assert.Equal(0, exit);
        var text = Root(answers[1]).GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("src/Circle.cs:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLoopKeepsAnsweringAfterACall()
    {
        using var directory = TempDirectory.Create();

        var (exit, answers, _) = await ServeAsync(
            directory.Path,
            string.Join(
                '\n',
                """{"jsonrpc":"2.0","id":1,"method":"initialize"}""",
                """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
                """{"jsonrpc":"2.0","id":3,"method":"ping"}"""));

        Assert.Equal(0, exit);
        Assert.Equal([1L, 2L, 3L], answers.Select(answer => Root(answer).GetProperty("id").GetInt64()));
        Assert.Equal(6, Root(answers[1]).GetProperty("result").GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public async Task AWorkspaceThatIsNotThereIsAUsageErrorBeforeAnyAnswer()
    {
        using var directory = TempDirectory.Create();

        var (exit, answers, diagnostics) = await ServeAsync(
            Path.Combine(directory.Path, "not-a-directory"),
            """{"jsonrpc":"2.0","id":1,"method":"initialize"}""");

        // Nothing is served from a guess, and the client hears nothing rather than a handshake for a workspace that
        // does not exist.
        Assert.Equal(2, exit);
        Assert.Empty(answers);
        Assert.StartsWith("error: ", diagnostics, StringComparison.Ordinal);
    }

    private static async Task<(int Exit, IReadOnlyList<string> Answers, string Diagnostics)> ServeAsync(
        string workspace,
        string input,
        bool noRefresh = false)
    {
        string[] arguments = noRefresh
            ? ["mcp", "serve", "--workspace", workspace, "--no-refresh"]
            : ["mcp", "serve", "--workspace", workspace];

        var options = CliOptions.Parse(arguments, _ => null);

        using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(input.Length == 0 ? string.Empty : input + '\n'));
        using var stdout = new MemoryStream();
        using var diagnostics = new StringWriter();

        var exit = await McpCommand.ExecuteAsync(options, stdin, stdout, diagnostics, Token);

        return (exit, Lines(Encoding.UTF8.GetString(stdout.ToArray())), diagnostics.ToString());
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string[] Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static JsonElement Root(string answer)
    {
        using var document = JsonDocument.Parse(answer);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// One <c>mcp serve</c> the test talks to a line at a time, over a pipe, so a call can be waited for instead of
    /// raced against the end of a fixed body.
    /// </summary>
    private sealed class Serving : IAsyncDisposable
    {
        private readonly AnonymousPipeServerStream _stdin = new(PipeDirection.In);
        private readonly MemoryStream _stdout = new();
        private readonly StringWriter _diagnostics = new();
        private readonly StreamWriter _client;
        private readonly Task<int> _running;

        public Serving(string workspace)
        {
            var options = CliOptions.Parse(
                ["mcp", "serve", "--workspace", workspace, "--no-refresh"],
                _ => null);

            _client = new StreamWriter(
                new AnonymousPipeClientStream(PipeDirection.Out, _stdin.ClientSafePipeHandle),
                new UTF8Encoding(false))
            {
                AutoFlush = true,
            };

            _running = McpCommand.ExecuteAsync(options, _stdin, _stdout, _diagnostics, TestContext.Current.CancellationToken);
        }

        public void Send(string line) => _client.WriteLine(line);

        /// <summary>Closes stdin once every expected answer has arrived, then reports how the command finished.</summary>
        public async Task<(IReadOnlyList<string> Answers, int Exit, string Diagnostics)> FinishAsync(int expected)
        {
            await WaitsForAsync(expected).ConfigureAwait(false);
            await _client.DisposeAsync().ConfigureAwait(false);

            var exit = await _running.ConfigureAwait(false);

            // Read after the loop has stopped: the last answer is only complete once nobody is writing.
            return (CompleteAnswers(), exit, _diagnostics.ToString());
        }

        public async ValueTask DisposeAsync()
        {
            // Closing the client's end is what ends the session, even when a test failed before it could finish.
            await _client.DisposeAsync().ConfigureAwait(false);

            try
            {
                await _running.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The test's own cancellation: cleanup is not the place to report it.
            }

            await _stdin.DisposeAsync().ConfigureAwait(false);
            await _stdout.DisposeAsync().ConfigureAwait(false);
            await _diagnostics.DisposeAsync().ConfigureAwait(false);
        }

        private async Task WaitsForAsync(int expected)
        {
            for (var attempt = 0; attempt < 500; attempt++)
            {
                if (CompleteAnswers().Count >= expected)
                {
                    return;
                }

                await Task.Delay(20, TestContext.Current.CancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"expected {expected} answers, saw {CompleteAnswers().Count}: {_diagnostics}"));
        }

        /// <summary>Reads only the lines the server has finished writing, so a poll never returns half an answer.</summary>
        private IReadOnlyList<string> CompleteAnswers()
        {
            var text = Encoding.UTF8.GetString(_stdout.ToArray());
            var end = text.LastIndexOf('\n');
            return end < 0 ? [] : [.. text[..end].Split('\n')];
        }
    }
}
