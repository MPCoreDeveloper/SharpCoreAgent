using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// JSON-RPC over the MCP streamable-HTTP transport: one POST per message, JSON or SSE back.
/// </summary>
/// <remarks>
/// The endpoint is explicit and the scheme is limited to <c>https</c>, or <c>http</c> for a loopback host, because a
/// token and a socket are a different threat model from a child process. The channel keeps the <c>Mcp-Session-Id</c>
/// the server returned, never follows a redirect, and serialises requests so one answer can be attributed to the one
/// request that asked for it. It does not implement OAuth, proxy discovery or the legacy HTTP+SSE handshake.
/// </remarks>
internal sealed class HttpJsonRpcChannel : IJsonRpcChannel
{
    private const int MaxBodyBytes = 8 * 1024 * 1024;

    private readonly Uri _endpoint;
    private readonly HttpClient _client;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _sessionId;
    private long _nextId;
    private bool _disposed;

    /// <summary>Creates the production channel over an explicit endpoint.</summary>
    /// <param name="endpoint">The MCP endpoint.</param>
    /// <param name="log">Receives diagnostics.</param>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is null.</exception>
    /// <exception cref="ArgumentException">The endpoint is not an allowed http or https URL.</exception>
    public HttpJsonRpcChannel(Uri endpoint, Action<string>? log = null)
        : this(endpoint, log, handler: null)
    {
    }

    /// <summary>Creates a channel over a supplied handler, for tests and embedders.</summary>
    /// <param name="endpoint">The MCP endpoint.</param>
    /// <param name="log">Receives diagnostics.</param>
    /// <param name="handler">The handler, or null for the production socket handler.</param>
    internal HttpJsonRpcChannel(Uri endpoint, Action<string>? log, HttpMessageHandler? handler)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme != Uri.UriSchemeHttps &&
            !(endpoint.Scheme == Uri.UriSchemeHttp && IsLoopback(endpoint)))
        {
            throw new ArgumentException(
                "the MCP URL must be https, or http on a loopback host",
                nameof(endpoint));
        }

        _endpoint = endpoint;
        _log = log;
        _client = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: true)
            : new HttpClient(handler, disposeHandler: true);
        _client.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <inheritdoc />
    public bool IsClosed => _disposed;

    /// <inheritdoc />
    public Func<string, JsonElement, CancellationToken, Task<JsonObject?>>? ServerRequestHandler { get; set; }

    /// <inheritdoc />
    public async Task<JsonElement> RequestAsync(
        string method,
        JsonObject? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = Interlocked.Increment(ref _nextId);
            var message = Envelope(id, method, parameters);
            using var request = BuildRequest(message);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(timeout);

            HttpResponseMessage response;
            try
            {
                response = await _client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new McpTransportException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"no answer to '{method}' within {timeout.TotalSeconds:0.#} seconds"));
            }

            using (response)
            {
                CaptureSession(response);
                if (!response.IsSuccessStatusCode)
                {
                    throw new McpTransportException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"the MCP endpoint answered {method} with HTTP {(int)response.StatusCode} {response.ReasonPhrase}"));
                }

                var media = response.Content.Headers.ContentType?.MediaType;
                return string.Equals(media, "text/event-stream", StringComparison.OrdinalIgnoreCase)
                    ? await ReadSseAsync(response, id, budget.Token).ConfigureAwait(false)
                    : await ReadJsonAsync(response, id, budget.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
 
    /// <inheritdoc />
    public async Task NotifyAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var request = BuildRequest(Envelope(null, method, parameters));
            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            CaptureSession(response);

            if (!response.IsSuccessStatusCode)
            {
                throw new McpTransportException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"the MCP endpoint answered {method} with HTTP {(int)response.StatusCode} {response.ReasonPhrase}"));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _client.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private static JsonObject Envelope(long? id, string method, JsonObject? parameters)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };

        if (id is { } value)
        {
            message["id"] = value;
        }

        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        return message;
    }

    private HttpRequestMessage BuildRequest(JsonObject message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(message.ToJsonString(), new UTF8Encoding(false), "application/json"),
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (_sessionId is { Length: > 0 } session)
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        }

        return request;
    }

    private void CaptureSession(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
        {
            var value = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(value))
            {
                _sessionId = value;
                _log?.Invoke(string.Create(CultureInfo.InvariantCulture, $"MCP session {value}"));
            }
        }
    }

 
    private static async Task<JsonElement> ReadJsonAsync(
        HttpResponseMessage response,
        long id,
        CancellationToken cancellationToken)
    {
        var body = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        return FindResult(document.RootElement, id);
    }

    private static async Task<JsonElement> ReadSseAsync(
        HttpResponseMessage response,
        long id,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        var data = new StringBuilder();

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    using var document = JsonDocument.Parse(data.ToString());
                    var result = FindResult(document.RootElement, id);
                    if (result.ValueKind != JsonValueKind.Undefined)
                    {
                        return result;
                    }

                    data.Clear();
                }

                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line.AsSpan(5).TrimStart());
            }
        }

        if (data.Length > 0)
        {
            using var document = JsonDocument.Parse(data.ToString());
            return FindResult(document.RootElement, id);
        }

        throw new McpTransportException("the MCP event stream ended without an answer");
    }

    private static JsonElement FindResult(JsonElement root, long id)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                var found = FindResult(item, id);
                if (found.ValueKind != JsonValueKind.Undefined)
                {
                    return found;
                }
            }

            return default;
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("id", out var answered) ||
            answered.ValueKind != JsonValueKind.Number ||
            answered.GetInt64() != id)
        {
            return default;
        }

        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var number) && number.ValueKind == JsonValueKind.Number
                ? number.GetInt32()
                : 0;
            var message = error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;
            throw new JsonRpcException(code, message ?? "the server reported an error");
        }

        return root.TryGetProperty("result", out var result) ? result.Clone() : default;
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                throw new McpTransportException("the MCP response is larger than the client's 8 MiB ceiling");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static bool IsLoopback(Uri endpoint)
    {
        if (endpoint.IsLoopback)
        {
            return true;
        }

        return IPAddress.TryParse(endpoint.Host, out var address) && IPAddress.IsLoopback(address);
    }
}

