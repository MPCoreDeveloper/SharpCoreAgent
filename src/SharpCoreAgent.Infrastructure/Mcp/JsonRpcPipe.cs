using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// JSON-RPC 2.0 over two streams, one JSON object per line.
/// </summary>
/// <remarks>
/// This is the whole protocol, and it deliberately knows nothing about processes: it is handed an input
/// stream and an output stream, which is what lets every rule below be tested against an in-memory pipe
/// instead of a child process.
/// <para>
/// Three invariants make the rest simple. One pump task owns the input stream, so no two pieces of code
/// parse a line. One semaphore owns the output stream, so two concurrent tool calls cannot interleave
/// halves of two messages. One counter owns the ids, and a dictionary of waiters holds the requests that
/// are still unanswered — which is what makes "the server died" a failed call instead of a hung one.
/// </para>
/// </remarks>
internal sealed class JsonRpcPipe : IAsyncDisposable
{
    private readonly StreamReader _reader;
    private readonly Stream _output;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _waiters = new();
    // Not disposed on purpose: the pump and a late close can both reach Fail(), and a cancellation source
    // that throws ObjectDisposedException from a background task would replace a readable failure with an
    // unobserved one. One source per pipe is not a leak worth that trade.
#pragma warning disable S2930 // Cancellation source is cancelled, never disposed; see the remark above.
    private readonly CancellationTokenSource _closed = new();
#pragma warning restore S2930
    private readonly Task _pump;
    private long _nextId;
    private string? _closeReason;
    private bool _disposed;

    /// <summary>
    /// Gets or sets the handler for a request the server sends to this client.
    /// </summary>
    /// <remarks>
    /// Null keeps the old behaviour: every server request is answered <c>-32601</c>. A connection sets this only while a
    /// tool call is in flight, so a sampling request cannot outlive the turn that authorized it.
    /// </remarks>
    internal Func<string, JsonElement, CancellationToken, Task<JsonObject?>>? ServerRequestHandler { get; set; }

    /// <summary>
    /// Creates a pipe over an already-open pair of streams.
    /// </summary>
    /// <param name="input">Where the server writes; read line by line until it ends.</param>
    /// <param name="output">Where requests are written, one line each.</param>
    /// <param name="log">Receives diagnostics a developer may want under <c>--verbose</c>.</param>
    public JsonRpcPipe(Stream input, Stream output, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        // No BOM detection: the protocol is delimiter-based JSON, and a server that writes a byte-order
        // mark would otherwise fail the first parse for no reason a developer could act on.
        _reader = new StreamReader(input, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        _output = output;
        _log = log;
        _pump = Task.Run(PumpAsync, _closed.Token);
    }

    /// <summary>Gets a value indicating whether the pipe has stopped accepting requests.</summary>
    public bool IsClosed => _closed.IsCancellationRequested;

    /// <summary>
    /// Sends one request and waits for its answer.
    /// </summary>
    /// <param name="method">The JSON-RPC method.</param>
    /// <param name="parameters">The parameters object, or null for a request that takes none.</param>
    /// <param name="timeout">The wall-clock ceiling for this request alone.</param>
    /// <param name="cancellationToken">Cancels the wait and the request.</param>
    /// <returns>The <c>result</c> member of the response, detached from its document.</returns>
    /// <exception cref="McpTransportException">The pipe is closed, the write failed, or the answer did not arrive in time.</exception>
    /// <exception cref="JsonRpcException">The server answered with an error object.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public async Task<JsonElement> RequestAsync(
        string method,
        JsonObject? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        if (IsClosed)
        {
            throw new McpTransportException(_closeReason ?? "the connection is closed");
        }

        var id = Interlocked.Increment(ref _nextId);
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[id] = waiter;

        try
        {
            await WriteAsync(Envelope(id, method, parameters), cancellationToken).ConfigureAwait(false);

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            budget.CancelAfter(timeout);

            try
            {
                return await waiter.Task.WaitAsync(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Two ways to arrive here, and they read very differently to a developer: the pipe closed
                // under us, or the server simply did not answer in time.
                throw IsClosed
                    ? new McpTransportException(_closeReason ?? "the connection is closed")
                    : new McpTransportException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"no answer to '{method}' within {timeout.TotalSeconds:0.#} seconds"));
            }
        }
        finally
        {
            _waiters.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Sends one notification, which by definition has no answer.
    /// </summary>
    /// <param name="method">The JSON-RPC method.</param>
    /// <param name="parameters">The parameters object, or null.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The write.</returns>
    /// <exception cref="McpTransportException">The pipe is closed or the write failed.</exception>
    public Task NotifyAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        return WriteAsync(message, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Fail("the connection was closed");

        // Bounded: a pipe whose input never ends must not hold disposal open, and the process kill that
        // follows is what actually stops the server.
        try
        {
            await _pump.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // The pump is stuck on a stream the server no longer feeds; the caller is killing it anyway.
        }

        _writeGate.Dispose();
        _reader.Dispose();
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                var line = await _reader.ReadLineAsync(_closed.Token).ConfigureAwait(false);
                if (line is null)
                {
                    Fail("the server closed its output");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    Dispatch(line);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Fail("the connection was closed");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Fail(string.Create(CultureInfo.InvariantCulture, $"the server's output could not be read: {ex.Message}"));
        }
    }

    private void Dispatch(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            _log?.Invoke(string.Create(
                CultureInfo.InvariantCulture,
                $"ignored a line that is not JSON-RPC ({ex.Message}): {Head(line)}"));
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                _log?.Invoke($"ignored a line that is not a JSON object: {Head(line)}");
                return;
            }

            var hasMethod = root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String;
            var hasId = root.TryGetProperty("id", out var id) &&
                        id.ValueKind is JsonValueKind.Number or JsonValueKind.String;

            if (hasMethod && hasId)
            {
                // A request from the server (sampling, elicitation, roots). The connection may install a handler for
                // sampling while a tool call is in flight; otherwise this client implements tools only.
                var methodName = method.GetString() ?? string.Empty;
                var parameters = root.TryGetProperty("params", out var paramsElement) && paramsElement.ValueKind == JsonValueKind.Object
                    ? paramsElement.Clone()
                    : default;
                _ = HandleServerRequestAsync(methodName, parameters, id.Clone());
                return;
            }

            if (hasMethod)
            {
                var methodName = method.GetString() ?? string.Empty;
                if (string.Equals(methodName, "notifications/message", StringComparison.Ordinal)
                    && root.TryGetProperty("params", out var parameters)
                    && parameters.ValueKind == JsonValueKind.Object)
                {
                    _log?.Invoke(ServerLog(parameters));
                }
                else
                {
                    _log?.Invoke(string.Create(CultureInfo.InvariantCulture, $"notification: {methodName}"));
                }

                return;
            }

            if (!hasId || !TryGetId(id, out var key))
            {
                _log?.Invoke($"ignored a message with no usable id: {Head(line)}");
                return;
            }

            if (!_waiters.TryRemove(key, out var waiter))
            {
                _log?.Invoke(string.Create(CultureInfo.InvariantCulture, $"ignored an answer to unknown request {key}"));
                return;
            }

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var code = error.TryGetProperty("code", out var number) && number.ValueKind == JsonValueKind.Number
                    ? number.GetInt32()
                    : 0;
                var message = error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String
                    ? text.GetString()
                    : null;
                waiter.TrySetException(new JsonRpcException(code, message ?? "the server reported an error"));
                return;
            }

            if (root.TryGetProperty("result", out var result))
            {
                // Cloned because the document is disposed when Dispatch returns and the caller reads the
                // element long after that.
                waiter.TrySetResult(result.Clone());
                return;
            }

            waiter.TrySetException(new JsonRpcException(0, "the answer carries neither result nor error"));
        }
    }

    private static string ServerLog(JsonElement parameters)
    {
        var level = parameters.TryGetProperty("level", out var levelValue) && levelValue.ValueKind == JsonValueKind.String
            ? levelValue.GetString() ?? "info"
            : "info";
        var logger = parameters.TryGetProperty("logger", out var loggerValue) && loggerValue.ValueKind == JsonValueKind.String
            ? loggerValue.GetString()
            : null;
        string message;
        if (!parameters.TryGetProperty("data", out var data))
        {
            message = string.Empty;
        }
        else
        {
            message = data.ValueKind == JsonValueKind.String ? data.GetString() ?? string.Empty : data.GetRawText();
        }

        if (message.Length > 500)
        {
            message = message[..500] + "…";
        }

        return logger is { Length: > 0 }
            ? $"server log [{level}] {logger}: {message}"
            : $"server log [{level}]: {message}";
    }

    private async Task HandleServerRequestAsync(string method, JsonElement parameters, JsonElement id)
    {
        var handler = ServerRequestHandler;
        if (handler is null)
        {
            Answer(method, id);
            return;
        }

        try
        {
            var result = await handler(method, parameters, CancellationToken.None).ConfigureAwait(false);
            if (result is null)
            {
                Answer(method, id);
                return;
            }

            await SafeWriteAsync(Success(id, result)).ConfigureAwait(false);
        }
        catch (McpSamplingException ex)
        {
            await SafeWriteAsync(Error(id, -32600, ex.Message)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke(string.Create(
                CultureInfo.InvariantCulture,
                $"failed to answer the server's '{method}': {ex.Message}"));
            await SafeWriteAsync(Error(id, -32603, ex.Message)).ConfigureAwait(false);
        }
    }

    private void Answer(string method, JsonElement id)
    {
        _log?.Invoke(string.Create(
            CultureInfo.InvariantCulture,
            $"answered the server's '{method}' with -32601: this client calls tools only"));
        _ = SafeWriteAsync(Error(
            id,
            -32601,
            $"'{method}' is not implemented by this client, which calls tools only"));
    }

    private static JsonObject Success(JsonElement id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = Id(id),
        ["result"] = result,
    };

    private static JsonObject Error(JsonElement id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = Id(id),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    };

    private static JsonValue? Id(JsonElement id) =>
        id.ValueKind == JsonValueKind.Number ? JsonValue.Create(id.GetInt64()) : JsonValue.Create(id.GetString());

    private async Task SafeWriteAsync(JsonObject message)
    {
        try
        {
            await WriteAsync(message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is McpTransportException or OperationCanceledException)
        {
            _log?.Invoke($"could not answer the server: {ex.Message}");
        }
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        if (IsClosed)
        {
            throw new McpTransportException(_closeReason ?? "the connection is closed");
        }

        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new McpTransportException($"the server's input could not be written: {ex.Message}");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void Fail(string reason)
    {
        lock (_waiters)
        {
            _closeReason ??= reason;
        }

        if (!_closed.IsCancellationRequested)
        {
            _closed.Cancel();
        }

        var failure = new McpTransportException(_closeReason);
        foreach (var waiter in _waiters.Values)
        {
            waiter.TrySetException(failure);
        }
    }

    private static JsonObject Envelope(long id, string method, JsonObject? parameters)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        return message;
    }

    private static bool TryGetId(JsonElement id, out long key)
    {
        key = 0;
        return id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out key);
    }

    private static string Head(string line) =>
        line.Length <= 120 ? line : line[..120] + "…";
}


