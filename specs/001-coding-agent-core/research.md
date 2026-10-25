# Research: Coding Agent Core

**Feature**: `001-coding-agent-core` | **Date**: 2026-10-23

Each decision below resolves a question the plan could not answer from the spec alone. Format is
decision → rationale → alternatives considered.

## D1. Provider protocol: one HTTP transport, two dedicated adapters

**Decision**: DeepSeek and Grok are both reached through their OpenAI-compatible
`POST {base}/chat/completions` surface, implemented once in
`Infrastructure/Chat/OpenAiCompatibleTransport`. `DeepSeekChatModel` and `GrokChatModel` are thin,
dedicated adapters that own their base address, credential variable, model catalogue and quirks.

**Rationale**: both providers document an OpenAI-compatible API, so a second transport would be
duplicated code and duplicated bugs. The adapters keep the constitution's "each provider ships a
dedicated, tested adapter" true without pretending the wire formats differ.

**Alternatives**: (a) one generic provider configured entirely by data — rejected: it hides the
per-provider quirks and makes "DeepSeek support" a config file rather than code with tests;
(b) `Microsoft.Extensions.AI` + a vendor SDK per provider — rejected: it puts a vendor abstraction
in the middle of a port we own, and the package set would grow faster than the product.

**Quirks the adapters own**: `deepseek-reasoner` streams `reasoning_content` and rejects tool
choice (`SupportsTools=false` in its descriptor); Grok's tool-call ids and `finish_reason` values
are normalised to the domain enum; both may omit `usage` on streamed responses, in which case usage
is reported as unknown.

## D2. Streaming: `IAsyncEnumerable` of typed events, SSE parsed incrementally

**Decision**: `IStreamingChatModel.StreamAsync` returns `IAsyncEnumerable<ChatStreamEvent>` with
`TextDelta`, `ThinkingDelta` and a final `Completed` carrying the aggregated message. The transport
parses `text/event-stream` line by line from `ReadAsStreamAsync`, buffering only an incomplete line.

**Rationale**: `IAsyncEnumerable` gives natural back-pressure and `await foreach` cancellation, and
it lets the orchestrator forward deltas immediately (Principle V). Buffering the response first
would defeat the point.

**Alternatives**: (a) `IAsyncObserver` callback — rejected: cancellation and error propagation get
murkier; (b) `Channel<T>` as the public shape — rejected: an implementation detail of the consumer
leaking into the port; (c) parsing with `JsonDocument` per chunk — rejected: allocations per token;
we parse one complete SSE event at a time, which is simple and fast enough at token rates.

## D3. Tool-call assembly

**Decision**: the transport accumulates `tool_calls[]` deltas by `index`, joining `arguments`
fragments, and emits the assembled calls once in the `Completed` event. A call with no `id` gets a
deterministic generated id (`call_<index>`); an unparsable argument string is preserved verbatim so
the tool layer can report a validation error to the model.

**Rationale**: the model never sees a half-parsed argument, and the tool layer stays the single
place that decides validity (Principle III).

**Alternatives**: execute a tool as soon as its name arrives — rejected: arguments are useless
before they are complete, and a partial execution is exactly the failure mode that costs a
developer their file.

## D4. Argument validation without a schema library

**Decision**: each tool declares a JSON Schema string for the model, and validates its own required
fields through `ToolArgumentReader` (`GetRequiredString`, `GetOptionalInt`, …) which throws
`ToolArgumentException` with a message safe to show the model. The orchestrator catches it, turns it
into an error `ToolResult`, and continues the turn.

**Rationale**: a full JSON Schema validator is a dependency with its own learning curve for six
tools; the tool that owns the contract is also the best place to enforce it.

**Alternatives**: a schema-validation package — deferred, not rejected: when the tool set grows past
~15 tools or schemas gain `oneOf`/`$ref`, the reader is replaced by a validator behind the same
`ITool` seam.

## D5. Credentials and redaction

**Decision**: `ICredentialStore` resolves a key by name from, in order: explicit overrides given to
`AddSharpCoreAgent`, then environment variables (`DEEPSEEK_API_KEY`, `XAI_API_KEY`,
`SHARPCOREAGENT_<PROVIDER>_API_KEY`). Missing credentials throw `ModelCredentialException` naming the
variable, before any HTTP call. `Authorization` is never logged; `DescribeRequest` returns method,
host and path only.

**Rationale**: names-not-values is the only rule that survives a screenshot (Principle VIII).

**Alternatives**: a settings file with plain-text keys — rejected as the default; it stays
supported through the override map for local use, and that file lives outside the repository.

## D6. Session persistence

**Decision**: a session is a single JSON document written by `JsonFileSessionStore` to
`.sharpcoreagent/sessions/<id>.json`, serialized with `System.Text.Json` source generation. It contains
the model per turn, the transcript, per-model usage and the approval mode — and no credential.

**Rationale**: a file needs no engine, survives a crash and is diffable; source generation keeps it
trim-friendly and keeps the wire format in one place.

**Alternatives**: SQLite (`SharpCoreDB`) — rejected for this feature: a dependency and a migration
story for a document written once per turn.

## D7. Retry policy

**Decision**: one bounded retry decorator over `IStreamingChatModel` (`RetryingChatModel`): up to 3
attempts for transient failures (HTTP 408/409/429/5xx, socket errors, `IOException` during streaming
before the first delta), honouring `Retry-After` when present, with exponential backoff and jitter.
Non-transient failures (400/401/403/404, credential errors) are not retried.

**Rationale**: a stream that already emitted text cannot be retried without duplicating output, so
the retry boundary is "before the first delta or not at all".

**Alternatives**: `Microsoft.Extensions.Http.Resilience`/Polly — not chosen here: the semantics we
need (no retry after first delta, `Retry-After` in both delta-seconds and HTTP-date) are a page of
code and a package we would then have to configure around.

## D8. CLI parsing without a parser package

**Decision**: a hand-written parser in `SharpCoreAgent.Cli` producing a `CliOptions` record, with usage
text and exit codes from `contracts/cli.md`.

**Rationale**: `System.CommandLine` is still a moving target and adds a dependency for a surface of
one verb plus two subcommands; a hand-written parser is small, fully testable, and never throws on a
typo.

**Alternatives**: `System.CommandLine` 2.0.x — acceptable later if the command surface grows; the
parser sits behind `CliOptions.Parse`, so the swap is local.

## D9. Offline demo provider

**Decision**: `DemoChatModel` implements the same ports and replays a deterministic script: it
answers a prompt, and requests one tool call when the prompt asks to list files. The CLI exposes it
as `--provider demo`.

**Rationale**: it makes the quickstart runnable without credentials, proves the port is not
DeepSeek-shaped, and gives the integration test a provider that behaves like a model without being a
mock object.

**Alternatives**: a mock in the test project only — rejected: then the CLI's first run requires a
key, and the seam is never exercised outside tests.

## D10. Language and framework level

**Decision**: `net11.0`, `LangVersion=preview`, `runtime-async=on`, nullable as errors, analyzers
`latest-recommended`, central package management.

**Rationale**: matches the sibling repositories and the product request (C# 15 / .NET 11); newer
language features are used where they remove ceremony, not for their own sake.

**Alternatives**: multi-targeting `net10.0;net11.0` — rejected: the constitution excludes
`net10.0`, and multi-targeting doubles build time for a product that ships one runtime.

