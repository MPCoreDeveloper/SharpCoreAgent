using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SharpCoreAgent.Domain.Agents;

namespace SharpCoreAgent.Cli;

/// <summary>
/// The web UI's listener: a page, a server-sent event stream, and four small JSON endpoints on a loopback prefix.
/// </summary>
/// <remarks>
/// The same security posture as <see cref="UiServer"/> — <c>127.0.0.1</c> and no other host, a <c>Host</c> check before any
/// answer, <c>no-store</c> on everything, an empty body on a refusal — with one difference forced by the feature: the event
/// stream is a request that stays open, so the accept loop hands each request to its own task instead of answering one at
/// a time. That is the only concurrency in the class, and it is what lets a prompt arrive while the stream is holding the
/// connection. A turn still runs one at a time: a second <c>POST /api/turn</c> while one is in flight is refused 409, so the
/// transcript and the approval it is waiting on cannot interleave.
/// </remarks>
internal sealed class ServeServer : IDisposable
{
    private const string Html = "text/html; charset=utf-8";
    private const string Json = "application/json; charset=utf-8";
    private const int MaxBodyBytes = 1 << 20;
    private const int EphemeralAttempts = 5;
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

    private readonly HttpListener _listener;
    private readonly ServeHub _hub;
    private readonly WebApprover _approver;
    private readonly Func<string, CancellationToken, Task> _runTurn;
    private readonly Func<ServeState> _state;
    private readonly Action<ApprovalMode> _setApproval;
    private readonly Action<AgentMode> _setMode;
    private readonly TextWriter _error;
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private bool _disposed;

    private ServeServer(
        HttpListener listener,
        int port,
        ServeHub hub,
        WebApprover approver,
        Func<string, CancellationToken, Task> runTurn,
        Func<ServeState> state,
        Action<ApprovalMode> setApproval,
        Action<AgentMode> setMode,
        TextWriter error)
    {
        _listener = listener;
        Port = port;
        _hub = hub;
        _approver = approver;
        _runTurn = runTurn;
        _state = state;
        _setApproval = setApproval;
        _setMode = setMode;
        _error = error;
    }

    /// <summary>Gets the resolved port, which is never 0 once bound.</summary>
    internal int Port { get; }

    /// <summary>Gets the URL the page is published at, which is also the listener's prefix.</summary>
    /// <remarks>Loopback only, so there is no network to encrypt and no certificate a browser would trust.</remarks>
#pragma warning disable S5332
    internal string Url => string.Create(CultureInfo.InvariantCulture, $"http://{UiEndpoint.LoopbackHost}:{Port}/");
#pragma warning restore S5332

    /// <summary>Gets the value a request's <c>Host</c> header must carry to be answered.</summary>
    internal string Authority => string.Create(CultureInfo.InvariantCulture, $"{UiEndpoint.LoopbackHost}:{Port}");

    /// <summary>
    /// Binds the listener, choosing a port when none was named.
    /// </summary>
    /// <param name="requestedPort">The port from <c>--port</c>, or 0 for one chosen now.</param>
    /// <param name="hub">The fan-out the event stream reads.</param>
    /// <param name="approver">The approver the page answers.</param>
    /// <param name="runTurn">Runs one prompt against the shared session.</param>
    /// <param name="state">Projects the current mode and spend for <c>GET /api/state</c>.</param>
    /// <param name="setApproval">Applies an approval mode from <c>POST /api/settings</c>.</param>
    /// <param name="setMode">Applies a plan/act mode from <c>POST /api/settings</c>.</param>
    /// <param name="error">Where the reason goes when the port cannot be bound.</param>
    /// <param name="server">The bound server, or <see langword="null"/> when the port was refused.</param>
    /// <returns><see langword="true"/> when something is listening.</returns>
    internal static bool TryStart(
        int requestedPort,
        ServeHub hub,
        WebApprover approver,
        Func<string, CancellationToken, Task> runTurn,
        Func<ServeState> state,
        Action<ApprovalMode> setApproval,
        Action<AgentMode> setMode,
        TextWriter error,
        out ServeServer? server)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentNullException.ThrowIfNull(runTurn);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(error);

        server = null;
        var attempts = requestedPort == 0 ? EphemeralAttempts : 1;
        HttpListenerException? refusal = null;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var endpoint = UiEndpoint.Loopback(requestedPort == 0 ? UiEndpoint.ChoosePort() : requestedPort);
            var listener = new HttpListener();
            listener.Prefixes.Add(endpoint.Url);

            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                listener.Close();
                refusal = ex;
                continue;
            }

            server = new ServeServer(listener, endpoint.Port, hub, approver, runTurn, state, setApproval, setMode, error);
            return true;
        }

        error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"error: could not listen on http://{UiEndpoint.LoopbackHost}:{requestedPort}/: {refusal?.Message} (code {refusal?.ErrorCode})"));
        error.WriteLine("hint: name another port with --port, or use --port 0 for one the system picks.");
        return false;
    }

    /// <summary>
    /// Answers requests until the process is interrupted.
    /// </summary>
    /// <param name="cancellationToken">Cancelled by Ctrl+C, which ends the loop rather than the process.</param>
    /// <returns>A task that completes when the listener has stopped.</returns>
    /// <remarks>
    /// Each accepted request is answered on its own task, because the event stream holds its connection open for the life
    /// of the page. The listener is stopped and closed in the <see langword="finally"/> so an interrupted server leaves no
    /// half-open port behind it.
    /// </remarks>
    internal async Task ServeAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    break;
                }

                _ = HandleAsync(context, cancellationToken);
            }
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>Stops and closes the listener, once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listener.Stop();
        _listener.Close();
    }

    /// <summary>Answers one request: the page, the stream, the state, or a small JSON action.</summary>
    /// <param name="context">The request and its response.</param>
    /// <param name="cancellationToken">Cancels the answer when the server stops.</param>
    /// <returns>A task that completes when the response is closed.</returns>
    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var response = context.Response;
        try
        {
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";

            var request = context.Request;
            if (!string.Equals(request.Headers["Host"], Authority, StringComparison.Ordinal))
            {
                // A loopback listener that answers any Host is reachable from a hostile page by DNS rebinding.
                Refuse(response, 403);
                return;
            }

            var method = request.HttpMethod;
            var path = request.Url?.AbsolutePath ?? "/";

            if (string.Equals(method, "GET", StringComparison.Ordinal))
            {
                switch (path)
                {
                    case "/":
                        await WriteAsync(response, 200, Html, ServePage.Page, cancellationToken).ConfigureAwait(false);
                        return;
                    case "/api/events":
                        await StreamAsync(response, cancellationToken).ConfigureAwait(false);
                        return;
                    case "/api/state":
                        await WriteAsync(response, 200, Json, JsonSerializer.Serialize(State()), cancellationToken).ConfigureAwait(false);
                        return;
                    default:
                        Refuse(response, 404);
                        return;
                }
            }

            if (string.Equals(method, "POST", StringComparison.Ordinal))
            {
                switch (path)
                {
                    case "/api/turn":
                        await StartTurnAsync(request, response, cancellationToken).ConfigureAwait(false);
                        return;
                    case "/api/approval":
                        await AnswerApprovalAsync(request, response, cancellationToken).ConfigureAwait(false);
                        return;
                    case "/api/settings":
                        await SettingsAsync(request, response, cancellationToken).ConfigureAwait(false);
                        return;
                    default:
                        Refuse(response, 404);
                        return;
                }
            }

            Refuse(response, 404);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or IOException or ObjectDisposedException)
        {
            // The client hung up, or the listener stopped underneath the request.
        }
#pragma warning disable CA1031 // One failed request must not end the server: the failure is reported and the loop continues.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await _error.WriteLineAsync($"serve: {ex.Message}".AsMemory(), CancellationToken.None).ConfigureAwait(false);
            TryRefuse(response, 500);
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
            {
                // The client hung up before the response could be closed; nothing is left to write.
            }
        }
    }

    /// <summary>Starts one turn, or refuses because one is already running.</summary>
    /// <param name="request">The request carrying <c>{"prompt":"…"}</c>.</param>
    /// <param name="response">The answer: 202, 400 or 409.</param>
    /// <param name="cancellationToken">The server token the turn runs under.</param>
    /// <returns>A task that completes when the answer is written.</returns>
    private async Task StartTurnAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
        if (body is null || !TryReadPrompt(body, out var prompt))
        {
            await WriteAsync(response, 400, Json, JsonSerializer.Serialize(new { error = "a non-empty prompt is required" }), cancellationToken).ConfigureAwait(false);
            return;
        }

#pragma warning disable S6966, S8949 // A zero-timeout Wait is a non-blocking try-acquire; a blocking WaitAsync would serialise requests.
        var acquired = _turnGate.Wait(0, CancellationToken.None);
#pragma warning restore S6966, S8949
        if (!acquired)
        {
            await WriteAsync(response, 409, Json, JsonSerializer.Serialize(new { error = "a turn is already running" }), cancellationToken).ConfigureAwait(false);
            return;
        }

        _ = RunTurnAsync(prompt, cancellationToken);
        await WriteAsync(response, 202, Json, JsonSerializer.Serialize(new { accepted = true }), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the turn on a background task and releases the turn gate when it ends.</summary>
    /// <param name="prompt">The prompt.</param>
    /// <param name="cancellationToken">The server token.</param>
    /// <returns>A task that completes when the turn is over.</returns>
    private async Task RunTurnAsync(string prompt, CancellationToken cancellationToken)
    {
        try
        {
            await _runTurn(prompt, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The server stopped mid-turn; nothing to report and nothing left to save.
        }
#pragma warning disable CA1031 // A failed turn is reported to the page; it must not take the server down with it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _hub.Publish(ServeEvents.Error(ex.Message));
        }
        finally
        {
            _turnGate.Release();
        }
    }

    /// <summary>Answers a pending approval with the page's decision.</summary>
    /// <param name="request">The request carrying <c>{"id":n,"decision":"…"}</c>.</param>
    /// <param name="response">204 when a question was answered, 400 or 409 otherwise.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes when the answer is written.</returns>
    private async Task AnswerApprovalAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
        if (body is null || !TryReadApproval(body, out var id, out var decision))
        {
            await WriteAsync(response, 400, Json, JsonSerializer.Serialize(new { error = "an approval id and decision are required" }), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_approver.Resolve(id, decision))
        {
            await WriteAsync(response, 409, Json, JsonSerializer.Serialize(new { error = "that question is no longer waiting" }), cancellationToken).ConfigureAwait(false);
            return;
        }

        response.StatusCode = 204;
        response.ContentLength64 = 0;
    }

    /// <summary>Switches the approval mode or the plan/act mode.</summary>
    /// <param name="request">The request carrying <c>{"approval":…}</c> and/or <c>{"mode":…}</c>.</param>
    /// <param name="response">204 when applied, 400 otherwise.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes when the answer is written.</returns>
    private async Task SettingsAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
        if (body is null || !TryReadSettings(body, out var approval, out var mode))
        {
            await WriteAsync(response, 400, Json, JsonSerializer.Serialize(new { error = "send {\"approval\":...} or {\"mode\":...}" }), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (approval is not null)
        {
            if (!CliOptions.TryParseApproval(approval, out var parsedApproval))
            {
                await WriteAsync(response, 400, Json, JsonSerializer.Serialize(new { error = "approval must be readonly, ask or allow" }), cancellationToken).ConfigureAwait(false);
                return;
            }

            _setApproval(parsedApproval);
        }

        if (mode is not null)
        {
            if (!CliOptions.TryParseMode(mode, out var parsedMode))
            {
                await WriteAsync(response, 400, Json, JsonSerializer.Serialize(new { error = "mode must be plan or act" }), cancellationToken).ConfigureAwait(false);
                return;
            }

            _setMode(parsedMode);
        }

        response.StatusCode = 204;
        response.ContentLength64 = 0;
    }

    /// <summary>Projects the session's state for <c>GET /api/state</c>, in the page's own field names.</summary>
    /// <returns>An anonymous object the serializer writes as <c>{"model":…,"approval":…}</c>.</returns>
    private object State()
    {
        var state = _state();
        return new
        {
            model = state.Model,
            approval = state.Approval,
            mode = state.Mode,
            tokensSpent = state.TokensSpent,
            ceiling = state.Ceiling,
        };
    }

    /// <summary>Streams the agent's events to one page until the connection or the server ends.</summary>
    /// <param name="response">The response the frames are written to.</param>
    /// <param name="cancellationToken">Ends the stream when the server stops.</param>
    /// <returns>A task that completes when the stream ends.</returns>
    /// <remarks>
    /// Server-sent events rather than a poll: the page draws each increment as it arrives, and an idle stream sends a
    /// comment every fifteen seconds so a proxy or a tab that keeps the connection open does not time it out. A page that
    /// never reads is dropped by the hub's bounded channel rather than stalling the turn.
    /// </remarks>
    private async Task StreamAsync(HttpListenerResponse response, CancellationToken cancellationToken)
    {
        using var lease = _hub.Subscribe(out var reader);

        response.StatusCode = 200;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.SendChunked = true;
        response.KeepAlive = true;

        var output = response.OutputStream;
        try
        {
            await WriteRawAsync(output, ": connected\n\n", cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                var read = reader.ReadAsync(cancellationToken).AsTask();
                var beat = Task.Delay(Heartbeat, cancellationToken);
                var first = await Task.WhenAny(read, beat).ConfigureAwait(false);

                if (first == beat)
                {
                    await WriteRawAsync(output, ": ping\n\n", cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var frame = await read.ConfigureAwait(false);
                await WriteRawAsync(output, "data: " + frame + "\n\n", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or IOException or ObjectDisposedException or ChannelClosedException)
        {
            // The page closed the tab, or the server stopped: the lease unregisters the subscriber.
        }
    }

    private static async Task WriteRawAsync(Stream output, string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAsync(HttpListenerResponse response, int status, string contentType, string body, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = status;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Refuse(HttpListenerResponse response, int status)
    {
        response.StatusCode = status;
        response.ContentLength64 = 0;
    }

    private static void TryRefuse(HttpListenerResponse response, int status)
    {
        try
        {
            Refuse(response, status);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
        {
            // The socket was already gone; there is no response left to refuse.
        }
    }

    private static async Task<string?> ReadBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength64 > MaxBodyBytes)
        {
            return null;
        }

        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return text.Length > MaxBodyBytes ? null : text;
    }

    private static bool TryReadPrompt(string body, out string prompt)
    {
        prompt = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("prompt", out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                prompt = value.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(prompt);
            }
        }
        catch (JsonException)
        {
            // A malformed body is the same refusal as a missing field: the endpoint answers 400 below.
        }

        return false;
    }

    private static bool TryReadApproval(string body, out long id, out string decision)
    {
        id = 0;
        decision = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("id", out var idValue)
                && idValue.TryGetInt64(out id)
                && root.TryGetProperty("decision", out var decisionValue)
                && decisionValue.ValueKind == JsonValueKind.String)
            {
                decision = decisionValue.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(decision);
            }
        }
        catch (JsonException)
        {
            // A malformed body is the same refusal as a missing field: the endpoint answers 400 below.
        }

        return false;
    }

    private static bool TryReadSettings(string body, out string? approval, out string? mode)
    {
        approval = null;
        mode = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (document.RootElement.TryGetProperty("approval", out var approvalValue) && approvalValue.ValueKind == JsonValueKind.String)
            {
                approval = approvalValue.GetString();
            }

            if (document.RootElement.TryGetProperty("mode", out var modeValue) && modeValue.ValueKind == JsonValueKind.String)
            {
                mode = modeValue.GetString();
            }

            return approval is not null || mode is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
