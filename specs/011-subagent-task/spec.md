# 011 — `task`: delegation with an isolated transcript

**Status**: complete 2026-10-30 — `T001`–`T020`, all four phases. `task` ships registered in `AddTools`, so a default
build advertises seventeen tools and `--web` makes eighteen, and the shipped prompt carries rule 8. The whole suite is
1190/1190 in `Debug` and again in `Release`, both builds at 0 warnings and 0 errors; it was 1167, and the 23 tests in
between are the ones the four phases wrote. SC-005 is recorded in `tasks.md` (`T020`) with the one caveat that makes a
literal "empty `git diff`" impossible here: `HEAD` predates `012` and `015`, so `Domain/Index` and
`SharpCoreAgent.Indexing` are untouched while `Domain` and `CodingAgent.cs` carry only those two rounds' still-uncommitted
lines, traced line for line and found to hold nothing this round wrote

**Depends on**: `001-coding-agent-core` (the turn loop, `AgentSession`, the tool and result contracts), `003-terminal-console-agent` (the report and the events the console prints), `012-compiler-oracle` (the gate a delegated edit must not be able to launder)

**Round**: 5 of `specs/000-parity-roadmap.md`. The design this round finishes is `TaskTool.cs.pending`, which the roadmap kept uncompiled on purpose and which has moved into this folder unchanged.

## Why this feature exists

A broad investigation is expensive in exactly the currency the agent is short of. *Which of these forty projects still reference the retired pricing client, and which would break if I changed its signature?* costs fifteen tool calls and thirty thousand tokens of intermediate output — file listings, grep hits, whole files read to check one line — and the transcript will never refer to any of it again. It sits in the context window for the rest of the session, pushing out the thing the developer actually said, and it is paid for again on every later request.

That is the gap Cline, Codex CLI and Claude CLI each close with a sub-agent, and it is the last of the four the roadmap names. `task` was drafted and never finished: `TaskTool.cs.pending` stops at its constructor. No `Definition`, no `ExecuteAsync`, no registration, no test.

What makes this round cheap is that almost nothing has to be invented. The four pieces a sub-agent needs already exist, each built for another reason:

| Piece | Already there | Built for |
|---|---|---|
| a turn the tool can run | `CodingAgent.RunTurnAsync(session, prompt, observer, token)` | the CLI, `--fsharp`, every test |
| a transcript of its own | `new AgentSession(workspace, model, approval, systemPrompt)` | `--resume`, `/model` |
| a prompt for a blind child | `PromptLibrary.SubAgentPrompt` | written with the draft, never shipped |
| the conversation's model and mode | `ToolInvocationContext.Model`, `.Approval` | the draft's own request, in 001 |

So the round adds one tool and one rule about permissions. It adds no provider, no flag, no new process, no new layer, and no second way to spend money.

## User stories

### US1 — Delegate the investigation, keep the context (P0, Phase 1)

As a developer whose session is filling up with grep output, I want to hand one self-contained question to a sub-agent, so that I get its answer and not its working.

Acceptance:

- `task` with a `prompt` runs one turn in a fresh session confined to the same workspace, on the same model, and answers with that turn's final message.
- The result is one bounded string. The child's transcript, tool results and reasoning are discarded when it returns — nothing reaches the parent's context except the answer and the counters.
- The result names what it cost: how many model calls the child used and how many tokens.
- A child that could not finish answers with the outcome and its own explanation, as an error result, so the model adapts instead of believing the work was done.

### US2 — Delegation cannot recurse (P0, Phase 1)

As a maintainer, I want a sub-agent to be structurally unable to delegate again, so that the spend and the depth of one `task` call are both bounded by the call itself.

Acceptance:

- The child's registry is every tool the session has, minus `task`, filtered by name rather than assumed absent.
- A turn whose only tools are `task` and `read_file` gives the child `read_file` only.
- A `max_iterations` above `TaskTool.MaxSubAgentIterations` (24) is refused with a readable error naming the range, and
  the child's budget is clamped to that constant in any case, so one call can never spend more than 24 model calls.

### US3 — A sub-agent cannot silently write (P0, Phase 2)

As the person who reads the diff, I want a child to have no more permission than the conversation that spawned it, so that delegation is never a way around a prompt I would have seen.

Acceptance:

- Parent `AllowAll` → child `AllowAll`. Any other parent mode → child `ReadOnly`.
- The child never prompts. There is no path from inside a tool call to a modal question the developer cannot attribute to the turn that raised it.
- With the parent in `Ask` (the default) a child `write_file` is refused, and the file on disk is unchanged.
- `task` is `ToolSafety.Destructive`, so plan mode and `--approval readonly` refuse the call itself — the tool can cause a write even though the child it spawns may not.

### US4 — The gate survives delegation (P0, Phase 2)

As a developer who trusts the closing line, I want a sub-agent's edit to count as an edit of my turn, so that the unverified-completion rule of `012` cannot be laundered by moving the edit one level down.

Acceptance:

- The child's `Created`, `Modified`, `Deleted` and `CommandRun` effects are forwarded onto the `task` result, in the order the child produced them. `Read` effects are dropped: the parent did not read those files.
- A parent turn whose child edited `.cs` source and did not verify ends `Completed` with `AgentReport.IsUnverified` set, exactly as if it had done the edit itself.
- A `dotnet_verify` that passes after the `task` result closes the gate.
- `AgentReport.FilesTouched` and `CommandsRun` list the child's work, so the console's closing line is true.

### US5 — The instruction and the tool ship together (P1, Phase 3)

As a maintainer, I want the prompt rule, the tool schema, the docs and the roadmap to describe one tool, so that a model is never told to call something that is not registered and a reader is never told about something that is.

Acceptance:

- `PromptLibrary.SystemPrompt` gains one operating rule naming `task` and when it is worth its cost.
- `SubAgentPrompt` ships as the child's system prompt — it already says the child sees nothing else and its final message is the whole result.
- `specs/011-subagent-task/contracts/task-tool.md` states the schema, the result format and the approval matrix, and `README.md` plus the roadmap row say the round landed.

## Functional requirements

- **FR-001** `task` is registered by `AddTools` unconditionally, beside `dotnet_verify`: the prompt names it, and a rule whose tool can be absent teaches the model that instructions are approximate.
- **FR-002** `task` is `ToolSafety.Destructive`. A read-only or planning session therefore refuses it without asking, because the tool's safety is a static property while a child's powers are not.
- **FR-003** Arguments, read through `ToolArgumentReader`: `prompt` (required, non-empty), `description` (optional, one line, at most 120 characters, defaulting to the prompt's first line), `max_iterations` (optional, 1–24, default 12). A value that cannot be read is a `ToolArgumentException`.
- **FR-004** The child session is `new AgentSession(context.Workspace, context.Model, mode, PromptLibrary.SubAgentPrompt)` where `mode` is `AllowAll` when `context.Approval == AllowAll` and `ReadOnly` otherwise. The conversation's model and approval come from the context, not from the host's startup options, because `/model` and `/approval` move them mid-session.
- **FR-005** The child's tool set is the `IToolRegistry` this tool was given — the session's, in the shipped container — minus `task`, read at execution time. The registry rather than a constructor argument, because the container cannot hand a tool the set that contains it (probe `F-1`) and because `--mcp` replaces the registry with a superset a tool-service enumeration would miss (probe `F-2`).
- **FR-006** The child's turn is `CodingAgent.RunTurnAsync(child, prompt, observer: null, cancellationToken)`. No events are forwarded: `IAgentObserver` events carry no nesting identifier, and a child's `TurnStarted` inside a parent turn would make the console's spinner and the transcript disagree about how many turns are running.
- **FR-007** The child's budget is `AgentOptions` with `MaxIterations` replaced by the clamped request. Its token ceiling is the session default, so the child cannot exceed the configured ceiling on its own; its wall clock is its own `TurnTimeout`, linked to the parent's cancellation token so Ctrl+C stops both.
- **FR-008** The answer is `AgentReport.FinalText` when the child completed with one, else the last assistant text in the child's transcript, bounded by `AgentOptions.MaxToolResultChars` with `IsTruncated` set honestly when something was cut.
- **FR-009** A child that ends `BudgetExhausted`, `Failed` or `Cancelled` yields `ToolResult.Failure` naming the outcome and the child's detail. Cancellation of the *parent* rethrows `OperationCanceledException`, as every tool does and as `ToolResult`'s contract says.
- **FR-010** The child's non-read effects are copied onto the result. This is what keeps `VerifyGate`, `AgentReport.FilesTouched`, `CommandsRun` and the console's record true through one level of delegation.
- **FR-011** `TaskTool` implements `IToolPreview`, so the approval prompt shows the delegated prompt and the iteration ceiling instead of a JSON line — the developer is approving an instruction, and the instruction is what should be on screen.
- **FR-012** The tool narrates through `context.Output`: one line when the child starts, one when it ends. Nothing from inside the child is streamed.
- **FR-013** Nothing in `SharpCoreAgent.Application` or `SharpCoreAgent.Domain` changes except one rule in `PromptLibrary`. The loop already provides the model, the approval mode, the child session and the report; a round that needed a new loop hook would mean the tool contracts were wrong.
- **FR-014** The child's spend is *reported, not folded*: it appears in the result text and in the child's `AgentReport.Usage`, and the parent's `MaxTotalTokens` does not see it. Nothing between `001` and `013` carries usage from a tool result back into session spend, and inventing that channel is a bigger change than this round.

## Non-goals (this round)

- **Parallel sub-agents.** No `Task.WhenAll`, no queue, no fan-out. One `task` call is one child run inline, because parallel children would need a merge rule for their effects and a transcript that shows two live turns.
- **Child transcripts on disk.** The child's session is discarded. Resuming a sub-agent is not a thing: the parent has the answer, which is the point.
- **Folding child spend into `/cost`.** FR-014 states the limitation rather than half-closing it.
- **A `--no-task` flag, or `--subagent-model`.** The child uses the session's model and the registry it was given. A flag per dimension of delegation is how a tool becomes a subsystem.
- **Streaming the child's tokens.** The console shows a `task` call like any other tool call, with one line at each end (FR-012).
- **Delegation from a read-only session.** Refused by FR-002. The remedy is a session that allows writes, which is the decision the developer should be making anyway.
- **Nesting deeper than one.** Structural, not policed: the child has no `task` (US2).

## Success criteria

- **SC-001** A scripted-model test proves the round trip: one `task` call returns the child's final message, the child's model calls and tokens are named in the result, and the parent's transcript contains no message from the child.
- **SC-002** The no-recursion invariant is proven by a failing run, not by a reading: deleting the name filter in `TaskTool` makes a test that asks a child for `task` fail.
- **SC-003** With the parent in `Ask`, a child `write_file` is refused and the file is absent afterwards; with the parent in `AllowAll`, the same child writes it. Both in one file, so the difference is the approval mode and nothing else.
- **SC-004** `CodingAgentTests` shows an unverified child edit turning the parent's turn `IsUnverified` (after exactly `MaxVerificationNudges` nudges) and a passing `dotnet_verify` after the `task` result closing the gate.
- **SC-005** Zero diff outside the files the plan names: `SharpCoreAgent.Domain`, the loop in `SharpCoreAgent.Application`, `Domain/Index` and `SharpCoreAgent.Indexing` are untouched.
- **SC-006** The full suite is green with no new warnings in `Debug` and `Release`, and the registered tool count moves by exactly one.

## Assumptions

- The model writes a self-contained prompt when the description says the child sees nothing else. If it does not, the cost is a poor answer, not a broken turn — which is why the child's failure is a result the model can react to.
- A sub-agent is worth its cost on investigations, not on edits: with the default `Ask` approval a child can read and not write (US3). The tool's description says so, so the model delegates research and does the edit itself.
- `MaxSubAgentIterations = 24` (the draft's constant) is the right ceiling for one investigation: twice the per-turn default `001` shipped with and a quarter of today's `MaxIterations`, which is the difference between "a sub-agent finishes a question" and "a sub-agent is a second session".


