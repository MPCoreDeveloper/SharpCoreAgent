# Tasks: Coding Agent Core

**Feature**: `001-coding-agent-core` | **Spec**: [spec.md](./spec.md) | **Plan**: [plan.md](./plan.md)

Ordering rule: foundation → domain seam → loop → providers → tools → wiring → CLI → hardening.
Every task is independently verifiable; tasks marked **[P]** touch disjoint files and may be done in
parallel.

Reconciled 2026-10-27: the boxes below were still open long after the work landed, so they were checked
against the tree rather than against memory. Two places where the repository differs from what this list
predicted: `Directory.Build.props` lives under `src/` rather than at the root, and several domain types
share a file with their neighbours (`ToolSafety` in `ToolDefinition.cs`, `ToolEffect` in `ToolResult.cs`,
`ChatResponse` in `ChatRequest.cs`, `ApprovalMode` in `Approval.cs`, `ISessionStore` in
`SessionSnapshot.cs`). The test tasks that named a file which was since renamed are covered by
`WorkspaceTests`, `TokenUsageTests`, `ToolRegistryTests`, `ToolResultTrimmerTests`,
`OpenAiCompatibleTransportTests` (SSE), `RetryingChatModelTests` (retries), `RetryPolicyTests` (backoff
arithmetic) and `ArgumentAndLimitsTests` (arguments, limits). **T093 is the only open task**: the
quickstart has not been walked end to end in one pass.

## Phase 1: Foundation

- [x] **T001** Create `global.json` (SDK 11.0.100, `rollForward: latestFeature`).
- [x] **T002** Create `Directory.Build.props`: `net11.0`, `LangVersion=preview`,
      `runtime-async=on`, `Nullable=enable`, `TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`,
      deterministic build, XML documentation required on public members. (Landed under `src/`, so the
      test project keeps its own copy without weakening the product build.)
- [x] **T003** Create `Directory.Packages.props` with central versions for
      `Microsoft.Extensions.DependencyInjection.Abstractions`, `.Http`, `.Logging.Abstractions`,
      `Microsoft.Extensions.DependencyInjection` (CLI), `xunit.v3`, `Microsoft.NET.Test.Sdk`.
- [x] **T004** Create `SharpCoreAgent.slnx` with the five source projects and the test project.
- [x] **T005** Create `.gitignore` covering `bin/`, `obj/`, `.vs/`, `.sharpcoreagent/`, `*.user`.

## Phase 2: Domain (the seam)

- [x] **T010** `SharpCoreAgent.Domain/Conversation/ChatRole.cs`, `ChatMessage.cs`, `ToolCall.cs`.
- [x] **T011** `SharpCoreAgent.Domain/Tools/ToolDefinition.cs`, `ToolSafety.cs`, `ToolEffect.cs`,
      `ToolResult.cs`, `ITool.cs`, `ToolInvocationContext.cs`.
- [x] **T012** `SharpCoreAgent.Domain/Workspace.cs` — containment, `TryResolve`, `ToRelative`.
- [x] **T013** `SharpCoreAgent.Domain/Model/ModelDescriptor.cs`, `ChatRequest.cs`, `ChatResponse.cs`,
      `FinishReason.cs`, `TokenUsage.cs`, `ChatStreamEvent.cs`, `IStreamingChatModel.cs`,
      `IChatModel.cs`, `IChatModelResolver.cs`.
- [x] **T014** `SharpCoreAgent.Domain/Agents/AgentEvent.cs`, `IAgentObserver.cs`, `ApprovalMode.cs`,
      `ApprovalOutcome.cs`, `ToolApprovalRequest.cs`, `IToolApprover.cs`, `AgentOutcome.cs`,
      `AgentReport.cs`.
- [x] **T015** `SharpCoreAgent.Domain/Sessions/SessionSnapshot.cs`, `ISessionStore.cs`,
      `ICredentialStore.cs`.
- [x] **T016** `SharpCoreAgent.Domain/Errors/` — `SharpCoreAgentException`, `ModelException`,
      `ModelRequestException`, `ModelCredentialException`, `ToolArgumentException`,
      `ToolNotFoundException`.
- [x] **T017** Enable the PublicAPI analyzer for Domain and add `PublicAPI.Shipped.txt` /
      `PublicAPI.Unshipped.txt`.

## Phase 3: Application (the loop)

- [x] **T020** `SharpCoreAgent.Application/AgentOptions.cs` + `Validate()`.
- [x] **T021** `SharpCoreAgent.Application/PromptLibrary.cs` — system prompt naming the tools, the
      workspace rule and the "stop and report" instruction.
- [x] **T022** `SharpCoreAgent.Application/ToolRegistry.cs` (`IToolRegistry`) — dedupe, stable order,
      `Find`.
- [x] **T023** `SharpCoreAgent.Application/AgentSession.cs` — transcript, model switch, per-model usage,
      `TotalTokens`, snapshot conversion.
- [x] **T024** `SharpCoreAgent.Application/CodingAgent.cs` — the turn loop of `data-model.md`:
      budgets, streaming forward, tool gate, refusal survival, cancellation, report.
- [x] **T025** `SharpCoreAgent.Application/ToolResultTrimmer.cs` — bounded results with `[truncated]`.

## Phase 4: Infrastructure — providers

- [x] **T030** `Infrastructure/Chat/OpenAiWire.cs` + `OpenAiJsonContext.cs` — source-generated
      request/response DTOs.
- [x] **T031** `Infrastructure/Chat/SseReader.cs` — incremental `data:` frames, `[DONE]`,
      mid-event splits.
- [x] **T032** `Infrastructure/Chat/OpenAiCompatibleTransport.cs` — request build, response parse,
      tool-call assembly, error mapping, redaction.
- [x] **T033** `Infrastructure/Chat/RetryingChatModel.cs` — bounded retry, `Retry-After`.
- [x] **T034** `Infrastructure/Providers/DeepSeekChatModel.cs` (+ catalogue, `SupportsTools=false`
      for `deepseek-reasoner`).
- [x] **T035** `Infrastructure/Providers/GrokChatModel.cs` (+ catalogue).
- [x] **T036** `Infrastructure/Providers/OpenAiCompatibleChatModel.cs` — configurable base URL.
- [x] **T037** `Infrastructure/Providers/DemoChatModel.cs` — deterministic scripted provider.
- [x] **T038** `Infrastructure/Providers/ChatModelResolver.cs` (`IChatModelResolver`) +
      `EnvironmentCredentialStore.cs` (`ICredentialStore`).

## Phase 5: Infrastructure — tools

- [x] **T040** `Infrastructure/Tools/ToolArgumentReader.cs` — typed, validated accessors.
- [x] **T041** `Infrastructure/Tools/PathSafety.cs` — link resolution, workspace containment.
- [x] **T042** `Infrastructure/Tools/TextLimits.cs` — binary sniff, line caps, `[truncated]`.
- [x] **T043** `Infrastructure/Tools/ListDirectoryTool.cs`.
- [x] **T044** `Infrastructure/Tools/ReadFileTool.cs`.
- [x] **T045** `Infrastructure/Tools/SearchTextTool.cs` (+ ignore list constant).
- [x] **T046** `Infrastructure/Tools/WriteFileTool.cs`.
- [x] **T047** `Infrastructure/Tools/DeleteFileTool.cs`.
- [x] **T048** `Infrastructure/Tools/RunShellCommandTool.cs` — launcher per OS, timeout, process
      tree kill, secret-stripped environment.

## Phase 6: Infrastructure — persistence

- [x] **T050** `Infrastructure/Sessions/JsonFileSessionStore.cs` + `SessionJsonContext.cs` —
      atomic write, stable property order, no secrets.

## Phase 7: Composition and CLI

- [x] **T060** `SharpCoreAgent.DependencyInjection/SharpCoreAgentOptions.cs` +
      `ServiceCollectionExtensions.AddSharpCoreAgent`.
- [x] **T061** `SharpCoreAgent.Cli/CliOptions.cs` + `CliOptions.Parse` — options, env defaults, usage.
- [x] **T062** `SharpCoreAgent.Cli/ConsoleObserver.cs` — streaming render, tool lines, `--json`, `--quiet`.
- [x] **T063** `SharpCoreAgent.Cli/ConsoleApprover.cs` — y/n prompt, non-interactive default `n`.
- [x] **T064** `SharpCoreAgent.Cli/Commands/RunCommand.cs`, `ChatCommand.cs`, `ModelsCommand.cs`.
- [x] **T065** `SharpCoreAgent.Cli/Program.cs` — dispatch, exit codes, cancellation wiring.

## Phase 8: Tests (evidence)

- [x] **T070** `WorkspaceTests` — containment matrix incl. absolute outside, `..`, case, root.
- [x] **T071** `TokenUsageTests` — unknown ≠ zero, operator `+`.
- [x] **T072** `AgentOptionsTests` — validation failures.
- [x] **T073** `ToolRegistryTests` — duplicates, order, `Find`.
- [x] **T074** `CodingAgentTests` — scripted model: text-only completion, tool round trip, unknown
      tool, denied call survival, iteration budget, token budget, cancellation, usage attribution.
- [x] **T075** `ToolResultTrimmerTests`.
- [x] **T076** `SseReaderTests` — split frames, `[DONE]`, malformed frame skipped.
- [x] **T077** `OpenAiCompatibleTransportTests` — request shape, text deltas, thinking deltas,
      tool-call assembly, usage unknown, error mapping (429 transient / 401 not), redaction.
- [x] **T078** `DeepSeekChatModelTests`, `GrokChatModelTests` — defaults, catalogue, base address.
- [x] **T079** `RetryingChatModelTests` — retries before first delta, not after, attempt cap,
      `Retry-After` honoured.
- [x] **T080** `ToolTests` — path escape refused, truncation flagged, write/delete effects, search
      ignore list.
- [x] **T081** `RunShellCommandToolTests` — exit code reported, timeout kills tree.
- [x] **T082** `SessionStoreTests` — round trip, atomic write, no secret in file.
- [x] **T083** `CliOptionsTests` — defaults, env fallback, unknown option, missing value.
- [x] **T084** `DemoProviderIntegrationTests` — full loop from CLI options through the agent.

## Phase 9: Hardening

- [x] **T090** `README.md` — what it is, providers, quickstart, approval modes, security notes.
- [x] **T091** Build clean: `dotnet build .\SharpCoreAgent.slnx -c Release` with zero warnings.
- [x] **T092** `dotnet test` offline, under 60 s.
- [ ] **T093** Verify quickstart steps 2, 3, 4, 7 and 9 end to end.
- [x] **T094** Keep the Principle VII deviation for the Application snapshot recorded in `plan.md`
      (drafted) and referenced under `## Deferred`.

## Deferred (not in this feature)

- Cost in currency (price tables) — token counts only, per the spec assumption.
- Multi-root workspaces, git-aware tools (`git_diff`, `apply_patch`), MCP servers.
- Application/Infrastructure/CLI PublicAPI snapshots (Principle VII deviation in `plan.md`).
- A JSON Schema validator replacing `ToolArgumentReader`.

