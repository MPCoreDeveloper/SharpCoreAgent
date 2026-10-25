# Tasks: Coding Agent Core

**Feature**: `001-coding-agent-core` | **Spec**: [spec.md](./spec.md) | **Plan**: [plan.md](./plan.md)

Ordering rule: foundation → domain seam → loop → providers → tools → wiring → CLI → hardening.
Every task is independently verifiable; tasks marked **[P]** touch disjoint files and may be done in
parallel.

## Phase 1: Foundation

- [ ] **T001** Create `global.json` (SDK 11.0.100, `rollForward: latestFeature`).
- [ ] **T002** Create `Directory.Build.props`: `net11.0`, `LangVersion=preview`,
      `runtime-async=on`, `Nullable=enable`, `TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`,
      deterministic build, XML documentation required on public members.
- [ ] **T003** Create `Directory.Packages.props` with central versions for
      `Microsoft.Extensions.DependencyInjection.Abstractions`, `.Http`, `.Logging.Abstractions`,
      `Microsoft.Extensions.DependencyInjection` (CLI), `xunit.v3`, `Microsoft.NET.Test.Sdk`.
- [ ] **T004** Create `SharpCoreAgent.slnx` with the five source projects and the test project.
- [ ] **T005** Create `.gitignore` covering `bin/`, `obj/`, `.vs/`, `.sharpcoreagent/`, `*.user`.

## Phase 2: Domain (the seam)

- [ ] **T010** `SharpCoreAgent.Domain/Conversation/ChatRole.cs`, `ChatMessage.cs`, `ToolCall.cs`.
- [ ] **T011** `SharpCoreAgent.Domain/Tools/ToolDefinition.cs`, `ToolSafety.cs`, `ToolEffect.cs`,
      `ToolResult.cs`, `ITool.cs`, `ToolInvocationContext.cs`.
- [ ] **T012** `SharpCoreAgent.Domain/Workspace.cs` — containment, `TryResolve`, `ToRelative`.
- [ ] **T013** `SharpCoreAgent.Domain/Model/ModelDescriptor.cs`, `ChatRequest.cs`, `ChatResponse.cs`,
      `FinishReason.cs`, `TokenUsage.cs`, `ChatStreamEvent.cs`, `IStreamingChatModel.cs`,
      `IChatModel.cs`, `IChatModelResolver.cs`.
- [ ] **T014** `SharpCoreAgent.Domain/Agents/AgentEvent.cs`, `IAgentObserver.cs`, `ApprovalMode.cs`,
      `ApprovalOutcome.cs`, `ToolApprovalRequest.cs`, `IToolApprover.cs`, `AgentOutcome.cs`,
      `AgentReport.cs`.
- [ ] **T015** `SharpCoreAgent.Domain/Sessions/SessionSnapshot.cs`, `ISessionStore.cs`,
      `ICredentialStore.cs`.
- [ ] **T016** `SharpCoreAgent.Domain/Errors/` — `SharpCoreAgentException`, `ModelException`,
      `ModelRequestException`, `ModelCredentialException`, `ToolArgumentException`,
      `ToolNotFoundException`.
- [ ] **T017** Enable the PublicAPI analyzer for Domain and add `PublicAPI.Shipped.txt` /
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

- [ ] **T030** `Infrastructure/Chat/OpenAiWire.cs` + `OpenAiJsonContext.cs` — source-generated
      request/response DTOs.
- [ ] **T031** `Infrastructure/Chat/SseReader.cs` — incremental `data:` frames, `[DONE]`,
      mid-event splits.
- [ ] **T032** `Infrastructure/Chat/OpenAiCompatibleTransport.cs` — request build, response parse,
      tool-call assembly, error mapping, redaction.
- [ ] **T033** `Infrastructure/Chat/RetryingChatModel.cs` — bounded retry, `Retry-After`.
- [ ] **T034** `Infrastructure/Providers/DeepSeekChatModel.cs` (+ catalogue, `SupportsTools=false`
      for `deepseek-reasoner`).
- [ ] **T035** `Infrastructure/Providers/GrokChatModel.cs` (+ catalogue).
- [ ] **T036** `Infrastructure/Providers/OpenAiCompatibleChatModel.cs` — configurable base URL.
- [ ] **T037** `Infrastructure/Providers/DemoChatModel.cs` — deterministic scripted provider.
- [ ] **T038** `Infrastructure/Providers/ChatModelResolver.cs` (`IChatModelResolver`) +
      `EnvironmentCredentialStore.cs` (`ICredentialStore`).

## Phase 5: Infrastructure — tools

- [ ] **T040** `Infrastructure/Tools/ToolArgumentReader.cs` — typed, validated accessors.
- [ ] **T041** `Infrastructure/Tools/PathSafety.cs` — link resolution, workspace containment.
- [ ] **T042** `Infrastructure/Tools/TextLimits.cs` — binary sniff, line caps, `[truncated]`.
- [ ] **T043** `Infrastructure/Tools/ListDirectoryTool.cs`.
- [ ] **T044** `Infrastructure/Tools/ReadFileTool.cs`.
- [ ] **T045** `Infrastructure/Tools/SearchTextTool.cs` (+ ignore list constant).
- [ ] **T046** `Infrastructure/Tools/WriteFileTool.cs`.
- [ ] **T047** `Infrastructure/Tools/DeleteFileTool.cs`.
- [ ] **T048** `Infrastructure/Tools/RunShellCommandTool.cs` — launcher per OS, timeout, process
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

- [ ] **T070** `WorkspaceTests` — containment matrix incl. absolute outside, `..`, case, root.
- [ ] **T071** `TokenUsageTests` — unknown ≠ zero, operator `+`.
- [ ] **T072** `AgentOptionsTests` — validation failures.
- [ ] **T073** `ToolRegistryTests` — duplicates, order, `Find`.
- [ ] **T074** `CodingAgentTests` — scripted model: text-only completion, tool round trip, unknown
      tool, denied call survival, iteration budget, token budget, cancellation, usage attribution.
- [ ] **T075** `ToolResultTrimmerTests`.
- [ ] **T076** `SseReaderTests` — split frames, `[DONE]`, malformed frame skipped.
- [ ] **T077** `OpenAiCompatibleTransportTests` — request shape, text deltas, thinking deltas,
      tool-call assembly, usage unknown, error mapping (429 transient / 401 not), redaction.
- [ ] **T078** `DeepSeekChatModelTests`, `GrokChatModelTests` — defaults, catalogue, base address.
- [ ] **T079** `RetryingChatModelTests` — retries before first delta, not after, attempt cap,
      `Retry-After` honoured.
- [ ] **T080** `ToolTests` — path escape refused, truncation flagged, write/delete effects, search
      ignore list.
- [ ] **T081** `RunShellCommandToolTests` — exit code reported, timeout kills tree.
- [ ] **T082** `SessionStoreTests` — round trip, atomic write, no secret in file.
- [x] **T083** `CliOptionsTests` — defaults, env fallback, unknown option, missing value.
- [x] **T084** `DemoProviderIntegrationTests` — full loop from CLI options through the agent.

## Phase 9: Hardening

- [ ] **T090** `README.md` — what it is, providers, quickstart, approval modes, security notes.
- [x] **T091** Build clean: `dotnet build .\SharpCoreAgent.slnx -c Release` with zero warnings.
- [x] **T092** `dotnet test` offline, under 60 s.
- [ ] **T093** Verify quickstart steps 2, 3, 4, 7 and 9 end to end.
- [ ] **T094** Keep the Principle VII deviation for the Application snapshot recorded in `plan.md`
      (drafted) and referenced under `## Deferred`.

## Deferred (not in this feature)

- Cost in currency (price tables) — token counts only, per the spec assumption.
- Multi-root workspaces, git-aware tools (`git_diff`, `apply_patch`), MCP servers.
- Application/Infrastructure/CLI PublicAPI snapshots (Principle VII deviation in `plan.md`).
- A JSON Schema validator replacing `ToolArgumentReader`.

