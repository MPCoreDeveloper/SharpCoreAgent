# Data Model: Coding Agent Core

**Feature**: `001-coding-agent-core` | **Date**: 2026-10-23

Types below are the implementation target. `Domain` types are `record`s with `required`/`init`
members so a captured value cannot drift after it is observed. Namespaces mirror projects
(`SharpCoreAgent.Domain`, `SharpCoreAgent.Application`, …).

## Domain

### Workspace

```csharp
public sealed class Workspace
{
    public string Root { get; }                       // absolute, normalised, trailing separator trimmed
    public static Workspace Create(string root);      // throws DirectoryNotFoundException when missing
    public bool TryResolve(string path, out string fullPath, out string? error);
    public string ToRelative(string fullPath);
}
```

Rules: a relative path resolves against `Root`; `Path.GetFullPath` normalises `.`/`..` before
comparison; containment is ordinal on Linux and `OrdinalIgnoreCase` on Windows; a resolved path
equal to `Root` is allowed for `list_directory` only; the empty string and any path containing a
null byte are refused. Link resolution is the tool layer's duty (it needs IO).

### Conversation

```csharp
public enum ChatRole { System, User, Assistant, Tool }

public sealed record ChatMessage
{
    public required ChatRole Role { get; init; }
    public string Content { get; init; } = string.Empty;
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];
    public string? ToolCallId { get; init; }          // set for Role == Tool
    public string? ToolName { get; init; }
    public bool IsError { get; init; }
    // factories: System(text), User(text), Assistant(text, toolCalls?), ToolResult(call, result)
}

public sealed record ToolCall(string Id, string Name, string ArgumentsJson);
```

Invariant: an `Assistant` message with `ToolCalls` may have empty `Content`; a `Tool` message always
has `ToolCallId` and `ToolName`, and its `Content` is already truncated.

### Tools

```csharp
public enum ToolSafety { ReadOnly, Destructive }

public sealed record ToolDefinition(string Name, string Description, string ParametersSchemaJson);

public enum ToolEffectKind { Read, Created, Modified, Deleted, CommandRun }
public sealed record ToolEffect(ToolEffectKind Kind, string Target);

public sealed record ToolResult
{
    public required string CallId { get; init; }
    public required string ToolName { get; init; }
    public string Content { get; init; } = string.Empty;
    public bool IsError { get; init; }
    public IReadOnlyList<ToolEffect> Effects { get; init; } = [];
    public bool IsTruncated { get; init; }
    public static ToolResult Success(string callId, string tool, string content, params ToolEffect[] effects);
    public static ToolResult Failure(string callId, string tool, string message);
    public static ToolResult Refused(string callId, string tool, string reason);
}

public sealed record ToolInvocationContext(string WorkspaceRoot);

public interface ITool
{
    ToolDefinition Definition { get; }
    ToolSafety Safety { get; }
    ValueTask<ToolResult> ExecuteAsync(ToolCall call, ToolInvocationContext context, CancellationToken ct);
}
```

`IsError` is true for validation failure, refusal, timeout and unexpected exception; the orchestrator
never rethrows a tool failure.

### Model port

```csharp
public sealed record ModelDescriptor(
    string Provider,          // "deepseek" | "grok" | "openai-compatible" | "demo"
    string Id,                // "deepseek-chat", "grok-4", …
    string DisplayName,
    int? ContextWindow = null,
    bool SupportsTools = true,
    bool SupportsStreaming = true,
    bool EmitsThinking = false)
{
    public string QualifiedName => $"{Provider}:{Id}";
}

public sealed record ChatRequest(
    ModelDescriptor Model,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ToolDefinition> Tools,
    double Temperature = 0.2,
    int? MaxOutputTokens = null);

public enum FinishReason { Stop, Length, ToolCalls, ContentFilter, Unknown }

public sealed record TokenUsage(int? InputTokens, int? OutputTokens)
{
    public static TokenUsage Unknown { get; } = new(null, null);
    public int? Total => InputTokens is null || OutputTokens is null ? null : InputTokens + OutputTokens;
    public static TokenUsage operator +(TokenUsage left, TokenUsage right);
}

public sealed record ChatResponse(
    ChatMessage Message,
    TokenUsage Usage,
    FinishReason FinishReason);

public abstract record ChatStreamEvent;
public sealed record TextDelta(string Text) : ChatStreamEvent;
public sealed record ThinkingDelta(string Text) : ChatStreamEvent;
public sealed record StreamCompleted(ChatResponse Response) : ChatStreamEvent;

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

public interface IChatModelResolver
{
    IStreamingChatModel Resolve(ModelDescriptor model);
}
```

Contract details: `StreamCompleted` is always the last event and appears exactly once; `Usage` is
`TokenUsage.Unknown` (never zero) when a provider omits it; a model whose `SupportsTools` is false
never receives `Tools` and the orchestrator refuses an agentic turn on it.

### Events, approval, report

```csharp
public abstract record AgentEvent;
public sealed record TurnStarted(string ModelId) : AgentEvent;
public sealed record AgentThinkingDelta(string Text) : AgentEvent;
public sealed record AgentTextDelta(string Text) : AgentEvent;
public sealed record ToolRequested(string CallId, string Tool, string ArgumentsJson) : AgentEvent;
public sealed record ToolFinished(string CallId, string Tool, bool IsError, string Summary) : AgentEvent;
public sealed record TurnFinished(AgentReport Report) : AgentEvent;

public interface IAgentObserver
{
    ValueTask OnEventAsync(AgentEvent @event, CancellationToken ct);
}

public enum ApprovalMode { ReadOnly, Ask, AllowAll }
public enum ApprovalOutcome { Approved, Denied }

public sealed record ToolApprovalRequest(string Tool, ToolSafety Safety, string Summary, string ArgumentsJson);

public interface IToolApprover
{
    ValueTask<ApprovalOutcome> DecideAsync(ToolApprovalRequest request, CancellationToken ct);
}

public enum AgentOutcome { Completed, ToolRefused, BudgetExhausted, Cancelled, Failed }

public sealed record AgentReport(
    AgentOutcome Outcome,
    string ModelId,
    string? FinalText,
    TokenUsage Usage,
    int Iterations,
    IReadOnlyList<ToolResult> ToolResults,
    string? Detail)
{
    public IReadOnlyList<string> FilesTouched { get; }   // derived: Created/Modified/Deleted targets, ordered, distinct
    public IReadOnlyList<string> CommandsRun { get; }    // derived: CommandRun targets, in order
}
```

## Application

### AgentOptions

```csharp
public sealed record AgentOptions
{
    public int MaxIterations { get; init; } = 24;
    public int MaxOutputTokensPerTurn { get; init; } = 8_000;
    public long MaxTotalTokens { get; init; } = 250_000;
    public int MaxToolResultChars { get; init; } = 24_000;
    public double Temperature { get; init; } = 0.2;
    public ApprovalMode Approval { get; init; } = ApprovalMode.Ask;
    public TimeSpan TurnTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public string SystemPrompt { get; init; } = PromptLibrary.CodingAgent;
    public AgentOptions Validate();   // throws ArgumentOutOfRangeException; all bounds must be > 0
}
```

Validation rules: every budget is `> 0`; `MaxToolResultChars >= 512`; `Temperature` in `[0, 2]`;
`TurnTimeout` in `[1s, 2h]`; `MaxTotalTokens >= MaxOutputTokensPerTurn`.

### CodingAgent turn algorithm

```
RunTurnAsync(session, prompt, observer, ct):
  model = session.ActiveModel
  if !model.SupportsTools -> return Failed("model <x> does not support tool use")
  session.Add(ChatMessage.User(prompt))
  emit TurnStarted(model.QualifiedName)
  linked = ct + TurnTimeout
  usage = Unknown; results = []; iterations = 0
  loop:
     if iterations == MaxIterations -> BudgetExhausted("iterations")
     iterations++
     (response, interrupted) = StreamOnce(request over session.Messages)
     if interrupted -> session.Add(partial assistant text); return Cancelled
     session.Add(response.Message); usage += response.Usage
     if response.Message.ToolCalls is empty -> return Completed(final text)
     foreach call:
        tool = registry.Find(name) ?? error result "unknown tool; available: …"
        emit ToolRequested(call.Id, name, call.ArgumentsJson)
        decision = gate(tool, call)                  // ReadOnly/Ask/AllowAll + IToolApprover
        if denied -> result = Refused(...)
        else result = execute with try/catch (ToolArgumentException, OperationCanceledException→timeout, Exception)
        result = Truncate(result, MaxToolResultChars)
        session.Add(ChatMessage.ToolResult(call, result))
        emit ToolFinished(call.Id, name, result.IsError, summary)
     if session.TotalTokens > MaxTotalTokens -> BudgetExhausted("tokens")
```

`gate` rules: `AllowAll` approves everything; `ReadOnly` approves `ToolSafety.ReadOnly` and refuses
`Destructive`; `Ask` auto-approves read-only and asks the `IToolApprover` for destructive calls.
A **denied** call appends a tool message telling the model the developer refused and why, and the
loop continues — a refusal is never fatal (FR-008, SC-005).

### ToolRegistry

```csharp
public sealed class ToolRegistry : IToolRegistry
{
    public ToolRegistry(IEnumerable<ITool> tools);                  // duplicate names throw
    public IReadOnlyList<ToolDefinition> Definitions { get; }
    public ITool? Find(string name);
}
```

Definitions are ordered by name so the prompt and the tests see a stable order.

### SessionSnapshot (persistence DTO, Domain)

```csharp
public sealed record SessionSnapshot(
    int Version,                       // 1
    string WorkspaceRoot,
    string Provider,
    string ModelId,
    ApprovalMode Approval,
    string CreatedAtUtc,               // ISO-8601
    string UpdatedAtUtc,
    IReadOnlyList<SessionMessage> Messages,
    IReadOnlyList<ModelUsage> Usage);

public sealed record SessionMessage(ChatRole Role, string Content, string? ToolCallId, string? ToolName, bool IsError, IReadOnlyList<ToolCall> ToolCalls);
public sealed record ModelUsage(string ModelId, int? InputTokens, int? OutputTokens, int Turns);
```

Round-trip requirement: save → load → save produces byte-identical JSON (property order fixed,
newline `\n`), and no `Authorization`, key or environment value appears anywhere in the document.

## State transitions

```
Session:  Created ──prompt──► Running ──Completed/BudgetExhausted/ToolRefused──► Created
                                  │
                                  ├──Cancelled──► Created (transcript kept)
                                  └──Failed─────► Created (exception surfaced)
Turn:     Started ─► Streaming ─► (ToolPending ─► ToolRunning ─► ToolFinished)* ─► Finished
```

`Session.ActiveModel` may change between turns and never during one; usage totals are keyed by
`QualifiedName` so a mid-session switch keeps both models' figures (FR-006).

## Validation rules captured as tests

| Rule | Test |
|---|---|
| Path containment: absolute outside, `..`, escaping link | `WorkspaceTests`, `ToolPathSafetyTests` |
| Truncation marks `IsTruncated` | `TruncationTests`, tool tests |
| Unknown tool message names available tools | `CodingAgentTests` |
| Denied call keeps the session alive | `CodingAgentTests` |
| Budgets reported by name | `CodingAgentTests` |
| Usage unknown ≠ zero | `TokenUsageTests`, transport tests |
| No credential in exception text | `RedactionTests` |


