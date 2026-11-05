using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// JSON-RPC over the legacy MCP HTTP+SSE transport: a GET event stream carries an endpoint and every answer, while
/// requests are POSTed to the endpoint the server named.
/// </summary>
/// <remarks>
/// This is the transport older remote servers still expose. It is separate from <see cref="HttpJsonRpcChannel"/>
/// because the two disagree about where an answer arrives: the streamable transport answers a POST, while this one
/// opens a GET stream first and then expects the answer as a later SSE event. The endpoint rule, redirect refusal and
/// session handling are the same; OAuth remains out of scope.
/// </remarks>
internal sealed class SseJsonRpcChannel : IJsonRpcChannel
{
    private const int MaxEventBytes = 8 * 1024 * 1024;

    private readonly Uri _endpoint;
    private readonly HttpClient _client;
    private readonly Action<string>? _log;
    private readonly Func<CancellationToken, Task<string?>>? _tokenProvider;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _waiters = new();
    private readonly TaskCompletionSource<Uri> _postEndpoint =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Not disposed on purpose: the pump and a late Dispose can both reach Fail(), and a cancellation source that
    // throws ObjectDisposedException from a background task would replace a readable failure with an unobserved one.
#pragma warning disable S2930 // Cancellation source is cancelled, never disposed; see the remark above.
    private readonly CancellationTokenSource _closed = new();
#pragma warning restore S2930
    private readonly Task _pump;
    private long _nextId;
    private bool _disposed;

    /// <summary>Creates the production channel over an explicit SSE endpoint.</summary>
    /// <param name="endpoint">The URL that opens the event stream.</param>
    /// <param name="log">Receives diagnostics.</param>
    /// <param name="accessToken">The OAuth bearer token, or null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is null.</exception>
    /// <exception cref="ArgumentException">The endpoint is not an allowed http or https URL.</exception>
    public SseJsonRpcChannel(Uri endpoint, Action<string>? log = null, string? accessToken = null)
        : this(endpoint, log, handler: null, Constant(accessToken))
    {
    }

    /// <summary>Creates a channel over a supplied handler, for tests and embedders.</summary>
    /// <param name="endpoint">The URL that opens the event stream.</param>
    /// <param name="log">Receives diagnostics.</param>
    /// <param name="handler">The handler, or null for the production socket handler.</param>
    /// <param name="accessToken">The OAuth bearer token, or null.</param>
    internal SseJsonRpcChannel(
        Uri endpoint,
        Action<string>? log,
        HttpMessageHandler? handler,
        string? accessToken = null)
        : this(endpoint, log, handler, Constant(accessToken))
    {
    }

    internal SseJsonRpcChannel(
        Uri endpoint,
        Action<string>? log,
        HttpMessageHandler? handler,
        Func<CancellationToken, Task<string?>>? tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme != Uri.UriSchemeHttps &&
            !(endpoint.Scheme == Uri.UriSchemeHttp && IsLoopback(endpoint)))
        {
            throw new ArgumentException(
                "the MCP SSE URL must be https, or http on a loopback host",
                nameof(endpoint));
        }

        _endpoint = endpoint;
        _log = log;
        _tokenProvider = tokenProvider;
        _client = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: true)
            : new HttpClient(handler, disposeHandler: true);
        _client.Timeout = Timeout.InfiniteTimeSpan;
        _pump = Task.Run(PumpAsync, _closed.Token);
    }

    private static Func<CancellationToken, Task<string?>>? Constant(string? accessToken) =>
        accessToken is { Length: > 0 } token
            ? _ => Task.FromResult<string?>(token)
            : null;

    /// <inheritdoc />
    public bool IsClosed => _closed.IsCancellationRequested;

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

        var id = Interlocked.Increment(ref _nextId);
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[id] = waiter;

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            budget.CancelAfter(timeout);

            Uri post;
            try
            {
                post = await _postEndpoint.Task.WaitAsync(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new McpTransportException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"the MCP SSE server did not name a POST endpoint for '{method}' within {timeout.TotalSeconds:0.#} seconds"));
            }

            await PostAsync(post, Envelope(id, method, parameters), budget.Token).ConfigureAwait(false);
            try
            {
                return await waiter.Task.WaitAsync(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw IsClosed
                    ? new McpTransportException("the MCP SSE stream is closed")
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

    /// <inheritdoc />
    public async Task NotifyAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        budget.CancelAfter(TimeSpan.FromSeconds(20));

        Uri post;
        try
        {
            post = await _postEndpoint.Task.WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpTransportException("the MCP SSE server did not name a POST endpoint in time");
        }

        await PostAsync(post, Envelope(null, method, parameters), budget.Token).ConfigureAwait(false);
    }

 
    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _closed.CancelAsync().ConfigureAwait(false);
        _client.Dispose();
        Fail("the MCP SSE connection was closed");

        try
        {
            await _pump.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // The stream is held open by the server; the client is gone and the socket will follow.
        }
    }

    private async Task PumpAsync()
    {
        var data = new StringBuilder();
        var eventName = "message";
        try
        {
            var token = _tokenProvider is null
                ? null
                : await _tokenProvider(_closed.Token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (token is { Length: > 0 })
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _closed.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new McpTransportException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"the MCP SSE endpoint answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}"));
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(_closed.Token)
                .ConfigureAwait(false);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));

            while (await reader.ReadLineAsync(_closed.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    Dispatch(eventName, data.ToString());
                    data.Clear();
                    eventName = "message";
                    continue;
                }

                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    eventName = line.AsSpan(6).TrimStart().ToString();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }

                    data.Append(line.AsSpan(5).TrimStart());
                    if (data.Length > MaxEventBytes)
                    {
                        throw new McpTransportException("the MCP SSE event is larger than the client's 8 MiB ceiling");
                    }
                }
            }

            Fail("the MCP SSE stream ended");
        }
        catch (OperationCanceledException)
        {
            Fail("the MCP SSE connection was closed");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonException or McpTransportException)
        {
            Fail(string.Create(CultureInfo.InvariantCulture, $"the MCP SSE stream failed: {ex.Message}"));
        }
    }

 
    private void Dispatch(string eventName, string data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return;
        }

        if (string.Equals(eventName, "endpoint", StringComparison.Ordinal))
        {
            ResolvePostEndpoint(data);
            return;
        }

        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            _log?.Invoke("ignored an SSE event that is not a JSON object");
            return;
        }

        var hasId = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number;
        if (!hasId)
        {
            return;
        }

        var key = id.GetInt64();
        if (root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String)
        {
            var parameters = root.TryGetProperty("params", out var given) && given.ValueKind == JsonValueKind.Object
                ? given.Clone()
                : default;
            _ = AnswerServerRequestAsync(method.GetString() ?? string.Empty, parameters, key);
            return;
        }

        if (!_waiters.TryRemove(key, out var waiter))
        {
            _log?.Invoke(string.Create(CultureInfo.InvariantCulture, $"ignored an SSE answer to unknown request {key}"));
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
            waiter.TrySetResult(result.Clone());
        }
        else
        {
            waiter.TrySetException(new JsonRpcException(0, "the answer carries neither result nor error"));
        }
    }

    private void ResolvePostEndpoint(string data)
    {
        if (!Uri.TryCreate(data, UriKind.RelativeOrAbsolute, out var candidate))
        {
            Fail($"the MCP SSE server named an invalid endpoint '{data}'");
            return;
        }

        var endpoint = candidate.IsAbsoluteUri ? candidate : new Uri(_endpoint, candidate);
        _log?.Invoke(string.Create(CultureInfo.InvariantCulture, $"MCP SSE endpoint {endpoint}"));
        _postEndpoint.TrySetResult(endpoint);
    }

    private async Task AnswerServerRequestAsync(string method, JsonElement parameters, long id)
    {
        var handler = ServerRequestHandler;
        if (handler is null)
        {
            return;
        }

        try
        {
            var result = await handler(method, parameters, CancellationToken.None).ConfigureAwait(false);
            if (result is null)
            {
                return;
            }

            var post = await _postEndpoint.Task.ConfigureAwait(false);
            await PostAsync(
                post,
                new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result },
                _closed.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke(string.Create(CultureInfo.InvariantCulture, $"could not answer the server's '{method}': {ex.Message}"));
        }
    }

    private async Task PostAsync(Uri endpoint, JsonObject message, CancellationToken cancellationToken)
    {
        var token = _tokenProvider is null
            ? null
            : await _tokenProvider(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(message.ToJsonString(), new UTF8Encoding(false), "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (token is { Length: > 0 })
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }


        using var response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new McpTransportException(string.Create(
                CultureInfo.InvariantCulture,
                $"the MCP SSE endpoint answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}"));
        }
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

    private void Fail(string reason)
    {
        _postEndpoint.TrySetException(new McpTransportException(reason));
        foreach (var waiter in _waiters)
        {
            waiter.Value.TrySetException(new McpTransportException(reason));
        }
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

