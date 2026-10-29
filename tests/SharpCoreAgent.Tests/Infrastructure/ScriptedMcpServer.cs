using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// Two in-memory pipes, wired in both directions.
/// </summary>
/// <remarks>
/// The MCP client speaks over plain streams, so the protocol can be exercised without spawning anything. This
/// is the seam that keeps the suite fast: <see cref="ScriptedMcpServer"/> is built on the same pair, which is
/// why the connection and the adapter are tested against the same bytes a real server would send.
/// </remarks>
internal sealed class PipePair : IDisposable
{
    private readonly Pipe _toClient = new();
    private readonly Pipe _toServer = new();
    private bool _disposed;

    /// <summary>Gets the stream the client reads: the server writes here.</summary>
    public Stream ClientInput => _toClient.Reader.AsStream();

    /// <summary>Gets the stream the client writes: the server reads here.</summary>
    public Stream ClientOutput => _toServer.Writer.AsStream();

    /// <summary>Gets the stream a scripted server writes to.</summary>
    public Stream ServerOutput => _toClient.Writer.AsStream();

    /// <summary>Gets the stream a scripted server reads from.</summary>
    public Stream ServerInput => _toServer.Reader.AsStream();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _toClient.Writer.Complete();
        _toServer.Writer.Complete();
    }
}

/// <summary>
/// One request a scripted server received.
/// </summary>
/// <param name="Method">The JSON-RPC method, or a blank string when the line was not a request.</param>
/// <param name="Parameters">The <c>params</c> object, detached from the line it arrived in.</param>
/// <param name="Id">The request id, or null for a notification.</param>
internal sealed record ScriptedRequest(string Method, JsonElement? Parameters, long? Id)
{
    /// <summary>Gets the <c>cursor</c> parameter, when the request carried one.</summary>
    public string? Cursor => Parameters is { } parameters &&
                             parameters.TryGetProperty("cursor", out var cursor) &&
                             cursor.ValueKind == JsonValueKind.String
        ? cursor.GetString()
        : null;

    /// <summary>Gets a named parameter's raw text, for asserting on what was forwarded.</summary>
    /// <param name="name">The parameter name.</param>
    /// <returns>The raw JSON, or null when the parameter is absent.</returns>
    public string? Raw(string name) =>
        Parameters is { } parameters && parameters.TryGetProperty(name, out var value)
            ? value.GetRawText()
            : null;
}

/// <summary>
/// A server that answers a script, line by line, over an in-memory pipe.
/// </summary>
/// <remarks>
/// Notifications are recorded and not answered, which is what the protocol says, and the responder may return
/// null to stay silent — that is how a handshake that never completes is tested. Every request received is kept
/// so an assertion can be made about the order of <c>initialize</c>, <c>notifications/initialized</c> and
/// <c>tools/list</c> rather than only about the answers.
/// </remarks>
internal sealed class ScriptedMcpServer : IAsyncDisposable
{
    private readonly PipePair _pipes = new();
    private readonly List<ScriptedRequest> _received = [];
    private readonly object _gate = new();
    private readonly Task _loop;

    /// <summary>
    /// Starts a server that answers every request the responder names.
    /// </summary>
    /// <param name="respond">Turns a request into the response to send, or null to send nothing.</param>
    public ScriptedMcpServer(Func<ScriptedRequest, JsonObject?> respond)
    {
        ArgumentNullException.ThrowIfNull(respond);
        _loop = Task.Run(() => RunAsync(respond));
    }

    /// <summary>Gets the stream the client reads.</summary>
    public Stream Input => _pipes.ClientInput;

    /// <summary>Gets the stream the client writes.</summary>
    public Stream Output => _pipes.ClientOutput;

    /// <summary>Gets every request received so far, in order.</summary>
    public IReadOnlyList<ScriptedRequest> Received
    {
        get
        {
            lock (_gate)
            {
                return [.. _received];
            }
        }
    }

    /// <summary>Gets the methods received so far, in order.</summary>
    public IReadOnlyList<string> Methods => [.. Received.Select(request => request.Method)];

    /// <summary>
    /// Closes the server's end of both pipes, the way a crash would.
    /// </summary>
    public void Close() => _pipes.Dispose();

    /// <summary>Builds a successful JSON-RPC response.</summary>
    /// <param name="id">The request id to echo.</param>
    /// <param name="result">The result node.</param>
    /// <returns>The response line.</returns>
    public static JsonObject Answer(long? id, JsonNode? result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id ?? 0,
        ["result"] = result,
    };

    /// <summary>Builds a JSON-RPC error response.</summary>
    /// <param name="id">The request id to echo.</param>
    /// <param name="code">The error code.</param>
    /// <param name="message">The message.</param>
    /// <returns>The response line.</returns>
    public static JsonObject Fail(long? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id ?? 0,
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _pipes.Dispose();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // A loop stuck on a pipe the client abandoned is the test's problem to report, not disposal's.
        }
    }

    private async Task RunAsync(Func<ScriptedRequest, JsonObject?> respond)
    {
        var reader = new StreamReader(_pipes.ServerInput, new UTF8Encoding(false));
        var writer = new StreamWriter(_pipes.ServerOutput, new UTF8Encoding(false)) { AutoFlush = true };

        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var request = Read(line);
                lock (_gate)
                {
                    _received.Add(request);
                }

                if (respond(request) is { } answer)
                {
                    await writer.WriteLineAsync(answer.ToJsonString()).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or JsonException)
        {
            // The test ended, or the client closed its end: either way there is nothing left to answer.
        }
    }

    private static ScriptedRequest Read(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        return new ScriptedRequest(
            root.TryGetProperty("method", out var method) ? method.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("params", out var parameters) ? parameters.Clone() : null,
            root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : null);
    }
}

