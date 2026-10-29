using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpCoreAgent.Application;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Tools;
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
/// What this type serves is a <see cref="McpServerPublishedSet"/>, never a registry: a name that is not published
/// is an error rather than a lookup, so the set is the only door to a tool. It knows nothing about the index, the
/// workspace's tools or the CLI — it answers for whatever set it was handed, which is what keeps the protocol
/// provable without an index and the publishing decision provable without a socket. The one thing a host may add
/// is an <see cref="IMcpCallGate"/>, run before each call and described no further here.
/// </para>
/// <para>
/// The one place the loop is not strictly sequential is a call in flight: while a tool runs, the next line is read
/// so that <c>notifications/cancelled</c> can reach the call it names. Any other message read during a call is
/// answered afterwards, in arrival order, which is the same order a client sees if it waits.
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
    private readonly McpServerPublishedSet _published;
    private readonly Workspace? _workspace;
    private readonly IMcpCallGate? _gate;
    private readonly Action<string>? _log;
    private readonly Queue<string> _deferred = new();
    private Task<string?>? _pendingRead;
    private bool _endOfInput;
    private bool _disposed;

    /// <summary>
    /// Creates a session over an already-open pair of streams that publishes no tools: the envelope alone.
    /// </summary>
    /// <param name="input">What the client writes; read line by line until it ends.</param>
    /// <param name="output">Where answers are written, one line each.</param>
    /// <param name="options">The versions and budgets this server works within.</param>
    /// <param name="log">Receives diagnostics for stderr, or null to stay quiet.</param>
    /// <exception cref="ArgumentException">The options do not validate.</exception>
    /// <remarks>
    /// A server that publishes nothing is still a server: the handshake and <c>ping</c> answer, <c>tools/list</c> is an
    /// empty page, and every <c>tools/call</c> is refused as an unknown tool. That is the honest reading of a session
    /// nobody gave a catalogue to, and it is what lets the envelope be tested without an index.
    /// </remarks>
    public McpServerSession(Stream input, Stream output, McpServerOptions options, Action<string>? log = null)
        : this(input, output, McpServerPublishedSet.Empty, workspace: null, options, gate: null, log)
    {
    }

    /// <summary>
    /// Creates a session that publishes the read-only evidence tools of a registry.
    /// </summary>
    /// <param name="input">What the client writes; read line by line until it ends.</param>
    /// <param name="output">Where answers are written, one line each.</param>
    /// <param name="registry">The host's registry; the published set is a filtered view of it.</param>
    /// <param name="workspace">The workspace every <c>tools/call</c> answers within.</param>
    /// <param name="options">The versions and budgets this server works within.</param>
    /// <param name="log">Receives diagnostics for stderr, or null to stay quiet.</param>
    /// <param name="gate">
    /// Runs before every <c>tools/call</c> and before nothing else; null answers straight from the index that is
    /// there.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
    /// <exception cref="ArgumentException">The options do not validate, or a published schema is not JSON.</exception>
    public McpServerSession(
        Stream input,
        Stream output,
        IToolRegistry registry,
        Workspace workspace,
        McpServerOptions options,
        Action<string>? log = null,
        IMcpCallGate? gate = null)
        : this(input, output, new McpServerPublishedSet(registry), workspace, options, gate, log)
    {
    }

    private McpServerSession(
        Stream input,
        Stream output,
        McpServerPublishedSet published,
        Workspace? workspace,
        McpServerOptions options,
        IMcpCallGate? gate,
        Action<string>? log)
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
        _published = published;
        _workspace = workspace;
        _gate = gate;
        _log = log;
    }

    /// <summary>Gets the options this session was created with, after validation.</summary>
    public McpServerOptions Options { get; }

    /// <summary>Gets how many tools this session publishes, which is what the ready line reports.</summary>
    public int PublishedToolCount => _published.Count;

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
            $"serving {Options.ServerName} {Options.ServerVersion} on stdio, protocol {Options.ProtocolVersion}, {PublishedToolCount} tools"));

        while (!cancellationToken.IsCancellationRequested && !_endOfInput)
        {
            string? line;
            try
            {
                // A line that arrived while a call was in flight is answered now, before anything further is
                // read: the order a client wrote is the order it hears back.
                line = _deferred.Count > 0
                    ? _deferred.Dequeue()
                    : await NextLineAsync(cancellationToken).ConfigureAwait(false);
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

        if (string.Equals(method, "tools/call", StringComparison.Ordinal))
        {
            // Answered by the call path, which may be cancelled before it has an answer to give and therefore
            // writes its own line at the moment it knows the outcome.
            await CallToolAsync(request, id, cancellationToken).ConfigureAwait(false);
            return;
        }

        var answer = method switch
        {
            "initialize" => Success(id, Initialize(request)),
            "tools/list" => Success(id, ListTools(request)),
            "ping" => Success(id, new JsonObject()),
            _ => Failure(
                id,
                MethodNotFound,
                $"'{method}' is not implemented by this server, which serves the code index only"),
        };

        await AnswerAsync(answer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the next line, keeping the read that is already in flight when there is one.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read, which abandons the wait rather than the stream.</param>
    /// <returns>The line, or <see langword="null"/> at end of input.</returns>
    /// <remarks>
    /// The task is cached because the call path races a read against a running tool with
    /// <see cref="Task.WhenAny(Task, Task)"/>: a second read started while the first is still awaited would be a
    /// second reader on one stream, which is exactly the bug the cache exists to prevent.
    /// </remarks>
    private async Task<string?> NextLineAsync(CancellationToken cancellationToken)
    {
        var read = _pendingRead ??= _input.ReadLineAsync(cancellationToken).AsTask();
        try
        {
            return await read.ConfigureAwait(false);
        }
        finally
        {
            // Cleared even when the read failed or was cancelled: the next caller must start a fresh one rather
            // than await a finished task and read one line for the rest of the session.
            if (ReferenceEquals(_pendingRead, read))
            {
                _pendingRead = null;
            }
        }
    }

    /// <summary>
    /// Answers <c>tools/list</c> with one page holding every published tool.
    /// </summary>
    /// <param name="request">The request, read only for a cursor the set is too small to need.</param>
    /// <returns>The result object: a <c>tools</c> array, and no <c>nextCursor</c> because there is no next page.</returns>
    private JsonObject ListTools(JsonObject request)
    {
        if ((request["params"] as JsonObject)?["cursor"] is not null)
        {
            // Refusing would fail a client that paginates by habit, and paging a five-tool list would invent a
            // second page and a cursor nobody can follow. The cursor is ignored, and said to be ignored.
            _log?.Invoke("tools/list: a cursor was ignored; one page holds the whole set");
        }

        return new JsonObject { ["tools"] = new JsonArray([.. _published.Tools.Select(Describe)]) };
    }

    /// <summary>
    /// Renders one tool the way the specification's <c>Tool</c> object wants it.
    /// </summary>
    /// <param name="published">The published tool.</param>
    /// <returns>The tool's name, its description, its own schema, and a read-only hint.</returns>
    /// <remarks>
    /// The schema is the tool's own text, parsed: not a copy maintained here, not a subset, and not a second
    /// description of the same arguments. A client therefore validates arguments exactly as the provider does, and a
    /// change to a tool's schema reaches the client without a change to this server.
    /// </remarks>
    private static JsonObject Describe(McpServerPublishedTool published) => new()
    {
        ["name"] = published.Tool.Definition.Name,
        ["description"] = published.Tool.Definition.Description,
        ["inputSchema"] = published.InputSchema.DeepClone(),
        ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
    };

    /// <summary>
    /// Answers <c>tools/call</c>: runs one published tool and renders what it returned.
    /// </summary>
    /// <param name="request">The request, whose params name the tool and carry its arguments.</param>
    /// <param name="id">The id the answer or the error is written with.</param>
    /// <param name="cancellationToken">Ends the session; a call cancelled by the client is abandoned, not answered.</param>
    /// <returns>A task that completes once the answer, the error, or the decision to say nothing has been made.</returns>
    /// <remarks>
    /// Only a request that names no published tool is a protocol error, and it is refused before anything runs. A tool
    /// that fails answers with <c>isError: true</c> and its own text, because the model can read that sentence and act
    /// on it, whereas a JSON-RPC error belongs to the client library and never reaches the model.
    /// </remarks>
    private async Task CallToolAsync(JsonObject request, JsonNode? id, CancellationToken cancellationToken)
    {
        if (request["params"] is not JsonObject parameters)
        {
            await AnswerAsync(
                Failure(id, InvalidParameters, "the request's params must be an object naming a tool and its arguments"),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var name = ReadString(parameters, "name");
        var published = _published.Find(name);

        if (published is null || _workspace is null)
        {
            _log?.Invoke($"tools/call: refused '{name ?? "an unnamed tool"}'");
            await AnswerAsync(
                Failure(
                    id,
                    InvalidParameters,
                    $"unknown tool '{name ?? string.Empty}'; this server publishes {_published.Count} evidence tools"),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // Absent arguments are an empty object rather than a parse error: `index_status` takes none, and a client
        // that omits the member asks the same question as one that sends {}.
        var argumentsJson = parameters["arguments"] is JsonNode arguments ? arguments.ToJsonString() : "{}";
        var toolCall = new ToolCall(RenderId(id), published.Tool.Definition.Name, argumentsJson);

        // ReadOnly is asserted rather than assumed: every tool here is published because it reads, and a tool that
        // ever asks about the mode is told what this session is.
        var context = new ToolInvocationContext(_workspace) { Approval = ApprovalMode.ReadOnly };

        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var call = InvokeAsync(published.Tool, toolCall, context, _gate, scope.Token);

        ToolResult? result;
        try
        {
            result = await PumpAsync(call, id, scope, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client cancelled this call and nothing else. The specification says such a request gets no answer,
            // and the id is already in the client's records as abandoned.
            _log?.Invoke($"call {toolCall.Id} was cancelled; it will not be answered");
            return;
        }

        if (result is null)
        {
            return;
        }

        await AnswerAsync(Success(id, AnswerBody(result)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a tool while watching for the two things that can matter before it finishes: a cancellation of this
    /// call, and the end of input.
    /// </summary>
    /// <param name="call">The tool's task, which is the answer when it completes.</param>
    /// <param name="id">The id a cancellation has to name to reach this call.</param>
    /// <param name="scope">The call's own token source, cancelled when the client asks for it.</param>
    /// <param name="cancellationToken">Ends the session; the scope is what cancels the call.</param>
    /// <returns>The tool's result.</returns>
    /// <remarks>
    /// Only a matching <c>notifications/cancelled</c> cancels the call. The notification names a request id, and a
    /// server that cancelled whatever happened to be running would let one abandoned call stop another, so a
    /// cancellation for any other id is treated as an ordinary message and logged as one that found nothing.
    /// <para>
    /// End of input mid-call cancels the call too: nobody is left to receive the answer, and the process is about to
    /// exit, so letting a long tool run would be work done for a client that is gone.
    /// </para>
    /// </remarks>
    private async Task<ToolResult?> PumpAsync(
        Task<ToolResult?> call,
        JsonNode? id,
        CancellationTokenSource scope,
        CancellationToken cancellationToken)
    {
        while (!call.IsCompleted)
        {
            var reading = NextLineAsync(cancellationToken);
            if (await Task.WhenAny(call, reading).ConfigureAwait(false) == call)
            {
                break;
            }

            var line = await reading.ConfigureAwait(false);
            if (line is null)
            {
                _endOfInput = true;
                _log?.Invoke("stdin closed while a call was running; the call is cancelled");
                await scope.CancelAsync().ConfigureAwait(false);
                break;
            }

            if (IsCancellationFor(line, id))
            {
                _log?.Invoke("the client cancelled the call in flight");
                await scope.CancelAsync().ConfigureAwait(false);
                continue;
            }

            // Anything else is answered once the call is done, in the order it arrived.
            _deferred.Enqueue(line);
        }

        return await call.ConfigureAwait(false);
    }

    /// <summary>
    /// Decides whether a line is the notification that cancels one call.
    /// </summary>
    /// <param name="line">The raw line, parsed here because the loop must not answer it.</param>
    /// <param name="id">The id of the call in flight.</param>
    /// <returns><see langword="true"/> when the line cancels exactly this call.</returns>
    private static bool IsCancellationFor(string line, JsonNode? id)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return false;
        }

        // A request that happens to be named notifications/cancelled is a request: it has an id and expects an
        // answer, so it waits like anything else.
        if (parsed is not JsonObject note || note.ContainsKey("id") || !TryReadMethod(note, out var method))
        {
            return false;
        }

        return string.Equals(method, "notifications/cancelled", StringComparison.Ordinal)
            && JsonNode.DeepEquals((note["params"] as JsonObject)?["requestId"], id);
    }

    /// <summary>
    /// Runs one tool, turning the failures a model can read into results instead of exceptions.
    /// </summary>
    /// <param name="tool">The published tool.</param>
    /// <param name="call">The call being answered.</param>
    /// <param name="context">The workspace, the approval mode and the output sink.</param>
    /// <param name="gate">What the host wants done before the tool runs, or null.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result, which may be a failure.</returns>
    /// <remarks>
    /// The gate runs inside the call rather than before it so that it shares the call's cancellation: a client
    /// that cancels while the index is being refreshed stops the refresh, instead of waiting for it and then
    /// being told the call was abandoned.
    /// <para>
    /// The catch list is the agent loop's, deliberately: a bad argument is the model's mistake and belongs in the
    /// transcript, a missing file is the workspace's state and belongs there too, and anything else is a bug that
    /// should surface rather than be retried forever as a tool error. Cancellation always escapes.
    /// </para>
    /// </remarks>
    private static async Task<ToolResult?> InvokeAsync(
        ITool tool,
        ToolCall call,
        ToolInvocationContext context,
        IMcpCallGate? gate,
        CancellationToken cancellationToken)
    {
        try
        {
            if (gate is not null)
            {
                await gate.PrepareAsync(cancellationToken).ConfigureAwait(false);
            }

            return await tool.ExecuteAsync(call, context, cancellationToken).ConfigureAwait(false);
        }
        catch (ToolArgumentException ex)
        {
            return ToolResult.Failure(call.Id, call.Name, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SharpCoreAgentException)
        {
            return ToolResult.Failure(call.Id, call.Name, $"{call.Name} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Renders a result as the <c>CallToolResult</c> the specification describes.
    /// </summary>
    /// <param name="result">What the tool returned.</param>
    /// <returns>One text content block, plus <c>isError</c> when the tool failed.</returns>
    /// <remarks>
    /// One text block holding the tool's content unchanged. A structured result would be a second rendering of text the
    /// tool already wrote for a model, and the client this server is built for is a model.
    /// </remarks>
    private static JsonObject AnswerBody(ToolResult result)
    {
        var body = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.Content }),
        };

        // Present only when true: the specification's default is false, and a client that reads the absence and a
        // client that reads false must agree.
        if (result.IsError)
        {
            body["isError"] = true;
        }

        return body;
    }

    /// <summary>
    /// Renders a request id as the call id a tool echoes back into its result.
    /// </summary>
    /// <param name="id">The request's id, which may be a string, a number, or absent.</param>
    /// <returns>The id as text.</returns>
    private static string RenderId(JsonNode? id) => id switch
    {
        null => "0",
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => id.ToJsonString(),
    };

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

