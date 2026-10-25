# Contract: Model Port

**Scope**: what `SharpCoreAgent.Domain` promises a provider adapter and what an adapter must promise
back. Applies to `DeepSeekChatModel`, `GrokChatModel`, `OpenAiCompatibleChatModel`,
`DemoChatModel`, and to the `RetryingChatModel` decorator.

## Port surface

```csharp
public interface IStreamingChatModel
{
    ModelDescriptor Model { get; }
    IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, CancellationToken ct);
}

public interface IChatModel
{
    ModelDescriptor Model { get; }
    Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct);
}
```

## Adapter obligations

1. **Last event**: `StreamAsync` yields exactly one `StreamCompleted` and it is the final event.
   Zero or two completions is a defect in the adapter.
2. **No text before the stream**: nothing is yielded until the HTTP response status is a success
   code. A failed response throws instead of yielding an error text.
3. **Cancellation**: `ct` aborts the HTTP read within one chunk; the exception surfaced is
   `OperationCanceledException`.
4. **Usage honesty**: when the provider omits usage, `ChatResponse.Usage` is
   `TokenUsage.Unknown` — never `0`.
5. **Tool calls**: `ChatMessage.ToolCalls` carries complete calls only; `ArgumentsJson` is the raw
   provider string, unmodified, even when it is not valid JSON.
6. **Model identity**: `ChatResponse` is attributed to `Model.QualifiedName` in the caller's report,
   so an adapter must not rewrite `ModelDescriptor`.
7. **No ambient state**: the adapter holds no request-scoped mutable state between calls; two
   concurrent `StreamAsync` calls on one instance are safe.

## Request mapping (OpenAI-compatible)

| Domain | Wire |
|---|---|
| `ChatRequest.Model.Id` | `model` |
| `ChatRequest.Messages` (`System`/`User`/`Assistant`) | `messages[].role` = `system`/`user`/`assistant`, `content` |
| `ChatMessage.ToolCalls` on an assistant message | `messages[].tool_calls[]` = `{id, type:"function", function:{name, arguments}}` |
| `ChatMessage` with `Role = Tool` | `{role:"tool", tool_call_id, content}` |
| `ChatRequest.Tools` | `tools[]` = `{type:"function", function:{name, description, parameters}}`, `parameters` parsed from the schema string |
| `ChatRequest.Temperature` | `temperature` (omitted when the provider rejects it, see quirks) |
| `ChatRequest.MaxOutputTokens` | `max_tokens` |
| streaming | `stream: true`, `stream_options: {include_usage: true}` |

## Response mapping

| Wire | Domain |
|---|---|
| `choices[0].delta.content` | `TextDelta` |
| `choices[0].delta.reasoning_content` | `ThinkingDelta` |
| `choices[0].delta.tool_calls[i]` | accumulated by `index` into `ToolCall` |
| `choices[0].finish_reason` | `stop`→`Stop`, `length`→`Length`, `tool_calls`→`ToolCalls`, `content_filter`→`ContentFilter`, else `Unknown` |
| `usage.prompt_tokens` / `usage.completion_tokens` | `TokenUsage.InputTokens` / `OutputTokens` |
| `data: [DONE]` | end of stream |

An SSE event split across TCP chunks must be reassembled; a `data:` line containing invalid JSON is
skipped (counted, not fatal) so one malformed frame cannot kill a turn.

## Error mapping

| Condition | Exception | Transient |
|---|---|---|
| missing credential | `ModelCredentialException` (names the config key) | no |
| HTTP 408, 409, 429, 5xx | `ModelRequestException` | yes |
| HTTP 400, 401, 403, 404, 422 | `ModelRequestException` | no |
| socket/DNS failure, `IOException` before first delta | `ModelException` | yes |
| `IOException` after first delta | `ModelException` | no (retry would duplicate text) |
| response body over 64 KiB | `ModelException` (body read is bounded) | no |

`ModelRequestException` carries `Provider`, `ModelId`, `StatusCode` and a **redacted** message:
status, provider request id when present, and the provider's `error.message` truncated to 512
characters. The `Authorization` header and API key never appear in any message, log line or report.

## Retry decorator

`RetryingChatModel(IStreamingChatModel inner, RetryPolicy policy, TimeProvider time)`:

- retries only when `IsTransient` **and** no `TextDelta`/`ThinkingDelta` has been yielded yet;
- maximum 3 attempts in total; delay `min(2^attempt seconds, 8s)` plus 0–250 ms jitter;
- `Retry-After` (delta-seconds or HTTP-date) overrides the computed delay when it is larger;
- reports each attempt through `Action<RetryAttempt>?` so the CLI can say "retrying (2/3)";
- the decorator is the *only* place that retries; adapters never retry internally.
