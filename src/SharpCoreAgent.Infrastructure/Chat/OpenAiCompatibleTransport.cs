using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Sessions;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Chat;

/// <summary>
/// Speaks the OpenAI-compatible <c>POST {base}/chat/completions</c> protocol and nothing else.
/// </summary>
/// <remarks>
/// Every provider that documents this surface — DeepSeek, Grok, and any local server that imitates it
/// — is served by this one class. Provider identity lives in the endpoint, the credential names and
/// the model descriptor, all supplied by the adapter that constructs it, so a provider that starts
/// diverging gets a subclass or its own transport without any caller noticing.
/// <para>
/// The transport holds no request-scoped state: two concurrent calls on one instance each get their
/// own <see cref="StreamState"/>.
/// </para>
/// </remarks>
public sealed class OpenAiCompatibleTransport : IStreamingChatModel, IChatModel
{
    /// <summary>The largest error body that is read, so a hostile HTML page cannot be buffered.</summary>
    private const int MaxErrorBodyBytes = 64 * 1024;

    /// <summary>The provider's own explanation is quoted at most this far.</summary>
    private const int MaxErrorDetailChars = 512;

    private readonly HttpClient _http;
    private readonly IReadOnlyList<string> _credentialNames;
    private readonly ICredentialStore _credentials;

    /// <summary>
    /// Creates a transport.
    /// </summary>
    /// <param name="http">The client to send on. Its timeout must allow a long stream; the caller owns it.</param>
    /// <param name="model">The descriptor of the model served by this endpoint.</param>
    /// <param name="endpoint">The absolute <c>/chat/completions</c> URL.</param>
    /// <param name="credentialNames">
    /// The configuration keys to try, most specific first; the first one that is set is used.
    /// </param>
    /// <param name="credentials">The store the key is read from.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The endpoint is not absolute or no credential name is given.</exception>
    public OpenAiCompatibleTransport(
        HttpClient http,
        ModelDescriptor model,
        Uri endpoint,
        IReadOnlyList<string> credentialNames,
        ICredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credentialNames);
        ArgumentNullException.ThrowIfNull(credentials);

        if (!endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException($"Endpoint '{endpoint}' must be absolute.", nameof(endpoint));
        }

        if (credentialNames.Count == 0 || credentialNames.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one credential name is required.", nameof(credentialNames));
        }

        _http = http;
        _credentialNames = credentialNames;
        _credentials = credentials;
        Model = model;
        Endpoint = endpoint;
    }

    /// <summary>Gets the model this transport speaks to.</summary>
    public ModelDescriptor Model { get; }

    /// <summary>Gets the endpoint requests are posted to.</summary>
    public Uri Endpoint { get; }

    /// <summary>
    /// Describes the endpoint without its credential, for <c>--verbose</c> output.
    /// </summary>
    /// <returns>Method, host and path, for example <c>POST api.deepseek.com/v1/chat/completions</c>.</returns>
    public string DescribeRequest() => string.Create(
        CultureInfo.InvariantCulture,
        $"POST {Endpoint.Host}{Endpoint.AbsolutePath}");

    /// <summary>
    /// Sends one request and streams the response.
    /// </summary>
    /// <param name="request">The request to send.</param>
    /// <param name="cancellationToken">Aborts the request and the read.</param>
    /// <returns>
    /// Text and thinking deltas as they arrive, then exactly one <see cref="StreamCompleted"/>.
    /// </returns>
    /// <exception cref="ModelCredentialException">No credential is configured for this provider.</exception>
    /// <exception cref="ModelRequestException">The provider returned a non-success status.</exception>
    /// <exception cref="ModelException">The connection failed or the stream broke.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired.</exception>
    public IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return StreamCoreAsync(request, cancellationToken);
    }

    /// <summary>
    /// Streams one request, from the credential lookup to the terminal event.
    /// </summary>
    /// <param name="request">The request to send; already checked for null.</param>
    /// <param name="cancellationToken">Aborts the request and the read.</param>
    /// <returns>The events of one response.</returns>
    /// <remarks>
    /// The null check lives in <see cref="StreamAsync"/>: an iterator body does not run before the
    /// caller asks for the first item, so a caller's bug would otherwise look like an empty stream.
    /// </remarks>
    private async IAsyncEnumerable<ChatStreamEvent> StreamCoreAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var credential = ResolveCredential();
        using var response = await SendAsync(request, credential, cancellationToken).ConfigureAwait(false);

        var state = new StreamState();

        // The enumerator is driven by hand so an IOException can be classified outside the iterator:
        // "before the first token" is retryable, "after" is not, and a retry that duplicated half an
        // answer would be worse than the failure itself.
        await using var source = ParseAsync(response, state, request, credential, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            ChatStreamEvent next;
            try
            {
                if (!await source.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }

                next = source.Current;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is IOException or HttpRequestException)
            {
                throw new ModelException(
                    state.SawAnyChunk
                        ? $"{Model.QualifiedName} lost the connection after the answer had started; the text was not resent."
                        : $"{Model.QualifiedName} could not be reached or the connection failed before the first token.",
                    isTransient: !state.SawAnyChunk,
                    innerException: ex);
            }

            yield return next;
        }

        if (!state.SawAnyChunk)
        {
            throw new ModelException($"{Model.QualifiedName} closed the stream without sending a single event.");
        }

        yield return new StreamCompleted(state.ToResponse());
    }

    /// <summary>
    /// Sends one request and returns the whole response.
    /// </summary>
    /// <param name="request">The request to send.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <returns>The completed response, identical to the one the stream ends with.</returns>
    /// <remarks>
    /// Implemented on top of the stream on purpose: there is one parser, one error mapping and one
    /// tool-call assembler, so buffered and streaming callers cannot disagree about a response.
    /// </remarks>
    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await foreach (var streamEvent in StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (streamEvent is StreamCompleted completed)
            {
                return completed.Response;
            }
        }

        throw new ModelException($"{Model.QualifiedName} completed without producing a response.");
    }

    /// <summary>
    /// Reads the response body as SSE frames and turns them into domain events.
    /// </summary>
    private static async IAsyncEnumerable<ChatStreamEvent> ParseAsync(
        HttpResponseMessage response,
        StreamState state,
        ChatRequest request,
        string credential,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        await foreach (var payload in SseReader.ReadDataAsync(body, cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(payload.Trim(), "[DONE]", StringComparison.Ordinal))
            {
                state.SawDone = true;
                yield break;
            }

            var chunk = TryParseChunk(payload);
            if (chunk is null)
            {
                // One malformed frame must not end a turn that is otherwise going well: it is counted
                // and skipped, and the count is reported only if nothing usable ever arrives.
                state.SkippedFrames++;
                continue;
            }

            ApplyChunk(chunk, state, request, credential);

            if (chunk.Choices is null)
            {
                continue;
            }

            foreach (var choice in chunk.Choices)
            {
                foreach (var streamEvent in ChoiceEvents(choice, state))
                {
                    yield return streamEvent;
                }
            }
        }

        if (state.SkippedFrames > 0 && !state.SawAnyChunk)
        {
            throw new ModelException(
                $"{request.Model.QualifiedName} sent {state.SkippedFrames.ToString(CultureInfo.InvariantCulture)} frame(s) that were not valid JSON.");
        }
    }

    /// <summary>
    /// Applies one frame's bookkeeping to the response being assembled.
    /// </summary>
    /// <param name="chunk">One decoded frame.</param>
    /// <param name="state">The response under construction.</param>
    /// <param name="request">The request that produced the frame, for the error message.</param>
    /// <param name="credential">The secret to scrub from a provider's message.</param>
    /// <exception cref="ModelException">The frame reported a provider error.</exception>
    private static void ApplyChunk(OpenAiStreamChunk chunk, StreamState state, ChatRequest request, string credential)
    {
        if (chunk.Error is not null)
        {
            state.SawAnyChunk = true;
            throw new ModelException(
                $"{request.Model.QualifiedName} reported an error mid-stream: {Sanitize(chunk.Error.Message, credential)}");
        }

        state.SawAnyChunk = true;
        state.RequestId ??= chunk.Id;

        if (chunk.Usage is { } usage)
        {
            state.Usage = new TokenUsage(usage.PromptTokens, usage.CompletionTokens);
        }
    }

    /// <summary>
    /// Turns one choice's delta into the events the caller sees.
    /// </summary>
    /// <param name="choice">The candidate's increment.</param>
    /// <param name="state">The response under construction, which collects tool-call fragments.</param>
    /// <returns>The deltas of this increment, reasoning before text.</returns>
    private static IEnumerable<ChatStreamEvent> ChoiceEvents(OpenAiStreamChoice choice, StreamState state)
    {
        var delta = choice.Delta;
        if (delta is not null)
        {
            // Reasoning first: a provider that puts both in one frame described the thinking
            // before the answer.
            var thinking = delta.ReasoningContent ?? delta.Reasoning;
            if (!string.IsNullOrEmpty(thinking))
            {
                yield return new ThinkingDelta(thinking);
            }

            if (!string.IsNullOrEmpty(delta.Content))
            {
                // Accumulated here, not by the caller: the terminal response is what the transcript
                // records, and a response whose text is empty would lose the answer the deltas delivered.
                state.Text.Append(delta.Content);
                yield return new TextDelta(delta.Content);
            }

            if (delta.ToolCalls is { Count: > 0 } fragments)
            {
                state.ApplyFragments(fragments);
            }
        }

        if (!string.IsNullOrEmpty(choice.FinishReason))
        {
            state.FinishReason = MapFinishReason(choice.FinishReason);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        ChatRequest request,
        string credential,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(ToWireRequest(request), OpenAiJsonContext.Default.OpenAiChatRequest);

        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        // TryAddWithoutValidation: a key with unusual characters must fail as "unauthorized", not as a
        // FormatException thrown before a request is even attempted.
        message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + credential);
        message.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or IOException or TaskCanceledException)
        {
            throw new ModelException(
                $"{request.Model.QualifiedName} could not be reached: {Sanitize(ex.Message, credential)}",
                isTransient: true,
                innerException: ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var failure = await CreateRequestExceptionAsync(response, request, credential, cancellationToken).ConfigureAwait(false);
            throw failure;
        }
    }

    private static async Task<ModelRequestException> CreateRequestExceptionAsync(
        HttpResponseMessage response,
        ChatRequest request,
        string credential,
        CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var detail = await ReadErrorDetailAsync(response, credential, cancellationToken).ConfigureAwait(false);

        if (response.Headers.TryGetValues("x-request-id", out var values))
        {
            var requestId = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(requestId))
            {
                detail = string.Create(CultureInfo.InvariantCulture, $"{detail} (request id {requestId})");
            }
        }

        // A rate limit, or a provider having a bad minute, is worth another attempt; a rejected
        // request is not, and retrying it would only spend the developer's quota on the same 400.
        var transient = status is 408 or 409 or 429 || status >= 500;

        return new ModelRequestException(
            request.Model.Provider,
            request.Model.Id,
            status,
            detail,
            transient,
            ReadRetryAfter(response));
    }

    private static async Task<string> ReadErrorDetailAsync(
        HttpResponseMessage response,
        string credential,
        CancellationToken cancellationToken)
    {
        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength > MaxErrorBodyBytes)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"the error body of {declaredLength} bytes was not read (limit {MaxErrorBodyBytes} bytes)");
        }

        using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var buffer = new byte[MaxErrorBodyBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await body.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, total);
        return Sanitize(ExtractProviderMessage(text) ?? text, credential);
    }

    private static string? ExtractProviderMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            var envelope = JsonSerializer.Deserialize(body, OpenAiJsonContext.Default.OpenAiErrorEnvelope);
            return string.IsNullOrWhiteSpace(envelope?.Error?.Message) ? null : envelope.Error.Message;
        }
        catch (JsonException)
        {
            // An HTML gateway page or plain text: quote it rather than pretend the provider said nothing.
            return null;
        }
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }

        if (header.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private static string Sanitize(string? text, string secret)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "no details";
        }

        var cleaned = string.IsNullOrEmpty(secret)
            ? text
            : text.Replace(secret, "[redacted]", StringComparison.Ordinal);

        cleaned = cleaned.Trim();
        return cleaned.Length <= MaxErrorDetailChars
            ? cleaned
            : string.Concat(cleaned.AsSpan(0, MaxErrorDetailChars), "…");
    }

    private string ResolveCredential()
    {
        foreach (var name in _credentialNames)
        {
            var value = _credentials.Find(name);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        // The exception names the primary key and never a value: a missing credential is a setup
        // instruction, not a secret.
        throw new ModelCredentialException(_credentialNames[0]);
    }

    private static FinishReason MapFinishReason(string value) => value switch
    {
        "stop" => FinishReason.Stop,
        "length" => FinishReason.Length,
        "tool_calls" or "function_call" => FinishReason.ToolCalls,
        "content_filter" => FinishReason.ContentFilter,
        _ => FinishReason.Unknown,
    };

    private static OpenAiStreamChunk? TryParseChunk(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize(payload, OpenAiJsonContext.Default.OpenAiStreamChunk);
        }
        catch (JsonException)
        {
            return null;
        }
    }


    private static OpenAiChatRequest ToWireRequest(ChatRequest request)
    {
        var messages = new OpenAiChatMessage[request.Messages.Count];
        for (var i = 0; i < messages.Length; i++)
        {
            messages[i] = ToWireMessage(request.Messages[i]);
        }

        OpenAiTool[]? tools = null;
        if (request.Tools.Count > 0)
        {
            tools = new OpenAiTool[request.Tools.Count];
            for (var i = 0; i < tools.Length; i++)
            {
                tools[i] = ToWireTool(request.Tools[i]);
            }
        }

        return new OpenAiChatRequest
        {
            Model = request.Model.Id,
            Messages = messages,
            Tools = tools,
            Temperature = request.Temperature,
            MaxTokens = request.MaxOutputTokens,
            Stream = true,
            StreamOptions = new OpenAiStreamOptions(),
        };
    }

    private static OpenAiChatMessage ToWireMessage(ChatMessage message)
    {
        var wire = new OpenAiChatMessage
        {
            Role = message.Role switch
            {
                ChatRole.System => "system",
                ChatRole.User => "user",
                ChatRole.Assistant => "assistant",
                ChatRole.Tool => "tool",
                _ => "user",
            },
            Content = message.Content,
        };

        if (message.Role == ChatRole.Tool)
        {
            wire.ToolCallId = message.ToolCallId ?? string.Empty;
            return wire;
        }

        if (!message.HasToolCalls)
        {
            return wire;
        }

        var calls = new OpenAiToolCallRequest[message.ToolCalls.Count];
        for (var i = 0; i < calls.Length; i++)
        {
            var call = message.ToolCalls[i];
            calls[i] = new OpenAiToolCallRequest
            {
                Id = call.Id,
                Function = new OpenAiFunctionCall
                {
                    Name = call.Name,
                    Arguments = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson,
                },
            };
        }

        wire.ToolCalls = calls;
        return wire;
    }

    private static OpenAiTool ToWireTool(ToolDefinition definition) => new()
    {
        Function = new OpenAiFunctionDefinition
        {
            Name = definition.Name,
            Description = definition.Description,
            Parameters = ParseSchema(definition),
        },
    };

    private static JsonElement ParseSchema(ToolDefinition definition)
    {
        try
        {
            using var document = JsonDocument.Parse(definition.ParametersSchemaJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // A tool with a broken schema is a programming error, but dropping the tool would hide it
            // from the model with no trace. An empty object schema is the honest fallback.
            using var empty = JsonDocument.Parse("""{"type":"object","properties":{}}""");
            return empty.RootElement.Clone();
        }
    }

    /// <summary>
    /// The per-call accumulator. Nothing in it outlives the request, which is what makes the transport
    /// safe to share between concurrent calls.
    /// </summary>
    private sealed class StreamState
    {
        private readonly Dictionary<int, ToolCallBuilder> _toolCalls = [];

        public StringBuilder Text { get; } = new();

        public FinishReason FinishReason { get; set; } = FinishReason.Unknown;

        public TokenUsage? Usage { get; set; }

        public string? RequestId { get; set; }

        public bool SawAnyChunk { get; set; }

        public bool SawDone { get; set; }

        public int SkippedFrames { get; set; }

        public void ApplyFragments(IReadOnlyList<OpenAiToolCallDelta> fragments)
        {
            foreach (var fragment in fragments)
            {
                if (!_toolCalls.TryGetValue(fragment.Index, out var builder))
                {
                    builder = new ToolCallBuilder(fragment.Index);
                    _toolCalls[fragment.Index] = builder;
                }

                builder.Merge(fragment);
            }
        }

        public ChatResponse ToResponse()
        {
            List<ToolCall> calls = _toolCalls.Count == 0
                ? []
                : [.. _toolCalls.Values.OrderBy(builder => builder.Index).Select(builder => builder.Build())];

            // A model that asked for tools without naming a finish reason still stopped because of the
            // call; reporting Stop there would make the loop look as if it ignored the request.
            var finish = FinishReason == FinishReason.Unknown && calls.Count > 0
                ? FinishReason.ToolCalls
                : FinishReason;

            var message = ChatMessage.Assistant(Text.ToString(), calls.Count == 0 ? null : calls);
            return new ChatResponse(message, finish, Usage ?? TokenUsage.Unknown);
        }
    }

    private sealed class ToolCallBuilder(int index)
    {
        private readonly StringBuilder _name = new();
        private readonly StringBuilder _arguments = new();

        public int Index { get; } = index;

        public string? Id { get; private set; }

        public void Merge(OpenAiToolCallDelta fragment)
        {
            // The id arrives with the first fragment only; a later fragment that repeats it must not
            // overwrite the one the transcript already used.
            if (!string.IsNullOrEmpty(fragment.Id) && string.IsNullOrEmpty(Id))
            {
                Id = fragment.Id;
            }

            if (fragment.Function?.Name is { Length: > 0 } name && _name.Length == 0)
            {
                _name.Append(name);
            }

            if (fragment.Function?.Arguments is { Length: > 0 } arguments)
            {
                _arguments.Append(arguments);
            }
        }

        public ToolCall Build() => new(
            string.IsNullOrEmpty(Id) ? string.Create(CultureInfo.InvariantCulture, $"call_{Index}") : Id,
            _name.ToString(),
            _arguments.Length == 0 ? "{}" : _arguments.ToString());
    }
}

