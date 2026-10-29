using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpCoreAgent.Infrastructure.Mcp;

namespace SharpCoreAgent.Infrastructure.McpServer;

/// <summary>
/// Answers JSON-RPC 2.0 over a pair of streams: the server half of the protocol our own <c>--mcp</c> client
/// speaks.
/// </summary>
/// <remarks>
/// The loop is deliberately the smallest thing that can be correct. One reader owns the input stream, one
/// writer owns the output stream, and one request is handled at a time in arrival order — a file-backed index
/// behind a single writer lock is not a throughput problem to solve, and a deterministic order is one a test
/// can assert. Nothing here touches <see cref="Console"/>: the streams are handed in, which is what lets every
/// rule below be tested over two memory streams instead of a child process.
/// <para>
/// This type knows nothing about tools yet. It owns the envelope, the handshake, the two methods every server
/// must answer, and the error codes — <c>tools/list</c> and <c>tools/call</c> arrive with the published set, so
/// the wire can be proven before anything is published on it.
/// </para>
/// </remarks>
public sealed class McpServerSession : IAsyncDisposable
{
    /// <summary>The error code for a body that is not JSON. The only one answered with a null id.</summary>
    internal const int ParseError = -32700;

    /// <summary>The error code for a JSON body that is not a request.</summary>
    internal const int InvalidRequest = -32600;

    /// <summary>The error code for a method this server does not implement.</summary>
    internal const int MethodNotFound = -32601;

    /// <summary>The error code for parameters this server cannot use.</summary>
    internal const int InvalidParameters = -32602;

    private readonly TextReader _input;
    private readonly StdioRpcWriter _output;
    private readonly Action<string>? _log;
    private bool _disposed;

    /// <summary>
    /// Creates a session over an already-open pair of streams.
    /// </summary>
    /// <param name="input">What the client writes; read line by line until it ends.</param>
    /// <param name="output">Where answers are written, one line each.</param>
    /// <param name="options">The versions and budgets this server works within.</param>
    /// <param name="log">Receives diagnostics for stderr, or null to stay quiet.</param>
    /// <exception cref="ArgumentException">The options do not validate.</exception>
    public McpServerSession(Stream input, Stream output, McpServerOptions options, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);

        Options = options.Validate();

        // No BOM detection, and both streams stay open: stdin and stdout belong to the process, and closing
        // them here would make disposal of the session a change to the process's own handles.
        _input = new StreamReader(
            input,
            new UTF8Encoding(false),
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        _output = new StdioRpcWriter(output);
        _log = log;
    }

    /// <summary>Gets the options this session was created with, after validation.</summary>
    public McpServerOptions Options { get; }


    /// <summary>
    /// Reads requests until stdin ends or the caller cancels, answering each one in arrival order.
    /// </summary>
    /// <param name="cancellationToken">Cancels the loop; an in-flight call is cancelled with it.</param>
    /// <returns>The process exit code, which is 0 for end of input and for cancellation.</returns>
    /// <exception cref="ObjectDisposedException">The session was disposed.</exception>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _log?.Invoke(string.Create(
            CultureInfo.InvariantCulture,
            $"serving {Options.ServerName} {Options.ServerVersion} on stdio, protocol {Options.ProtocolVersion}"));

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                _log?.Invoke($"stdin stopped: {ex.Message}");
                break;
            }

            if (line is null)
            {
                _log?.Invoke("stdin closed; the server is done");
                break;
            }

            // A blank line is a client's stray newline, not a message. Answering it with -32700 would punish
            // a harmless habit that no specification forbids.
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                await HandleLineAsync(line, cancellationToken).ConfigureAwait(false);
            }
            catch (McpTransportException ex)
            {
                // The client stopped reading: there is nobody left to answer, and a failure per line would
                // fill stderr for a pipe that will never drain.
                _log?.Invoke($"the client stopped listening: {ex.Message}");
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        return 0;
    }

    /// <summary>Closes the writer. The streams themselves stay open, because the process owns them.</summary>
    /// <returns>A completed task.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _output.DisposeAsync().ConfigureAwait(false);
        _input.Dispose();
    }

    private async Task HandleLineAsync(string line, CancellationToken cancellationToken)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(line);
        }
        catch (JsonException ex)
        {
            // The one error with a null id: nothing was parsed far enough to know which request failed.
            _log?.Invoke($"a line was not JSON: {ex.Message}");
            await AnswerAsync(Failure(null, ParseError, "the body is not JSON"), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (parsed is not JsonObject request)
        {
            await AnswerAsync(
                Failure(null, InvalidRequest, "a JSON object is expected"),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // A notification is a message with no "id" member. An explicit null id is a request whose answer
        // carries a null id, which is what the specification says and what a strict client expects back.
        var isNotification = !request.ContainsKey("id");
        var id = request["id"];

        if (!TryReadMethod(request, out var method))
        {
            if (!isNotification)
            {
                await AnswerAsync(
                    Failure(id, InvalidRequest, "the request names no method"),
                    cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (isNotification)
        {
            HandleNotification(method);
            return;
        }

        var answer = method switch
        {
            "initialize" => Success(id, Initialize(request)),
            "ping" => Success(id, new JsonObject()),
            _ => Failure(
                id,
                MethodNotFound,
                $"'{method}' is not implemented by this server, which serves the code index only"),
        };

        await AnswerAsync(answer, cancellationToken).ConfigureAwait(false);
    }

    private JsonObject Initialize(JsonObject request)
    {
        var asked = ReadProtocolVersion(request);
        var version = asked is not null && Options.KnownProtocolVersions.Contains(asked, StringComparer.Ordinal)
            ? asked
            : Options.ProtocolVersion;

        _log?.Invoke(string.Create(
            CultureInfo.InvariantCulture,
            $"initialize: asked for {asked ?? "nothing"}, answering {version}, from {ReadClientName(request) ?? "an unnamed client"}"));

        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = Options.ServerName,
                ["version"] = Options.ServerVersion,
            },
        };
    }

    private void HandleNotification(string method)
    {
        // Both of these are expected and neither is answered. Nothing else a client sends as a notification
        // can change anything here, so it is logged and dropped rather than refused: a notification has no
        // id, and an error with no id is an error nobody can attribute.
        var note = method switch
        {
            "notifications/initialized" => "the client is ready",
            "notifications/cancelled" => "a cancellation arrived with no call in flight",
            _ => $"ignored the notification '{method}'",
        };

        _log?.Invoke(note);
    }

    private async Task AnswerAsync(JsonObject message, CancellationToken cancellationToken) =>
        await _output.WriteAsync(message, cancellationToken).ConfigureAwait(false);

    private static bool TryReadMethod(JsonObject request, out string method)
    {
        method = string.Empty;

        if (request["method"] is not JsonValue value || !value.TryGetValue<string>(out var name))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        method = name;
        return true;
    }

    private static string? ReadProtocolVersion(JsonObject request) =>
        ReadString(request["params"] as JsonObject, "protocolVersion");

    private static string? ReadClientName(JsonObject request) =>
        ReadString((request["params"] as JsonObject)?["clientInfo"] as JsonObject, "name");

    private static string? ReadString(JsonObject? holder, string property) =>
        holder?[property] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonObject Success(JsonNode? id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    };

    private static JsonObject Failure(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    };
}

