# Plan — 011 `task`: delegation with an isolated transcript

## Shape

One tool, one refusing approver, one forwarded effect list. The loop does not learn about delegation.

```text
model calls task(prompt, description?, max_iterations?)
                    |
              TaskTool                       Infrastructure/Tools — the whole round's code
                    |
   +----------------+------------------+
   |                                   |
child IToolRegistry                 child AgentSession
(parent registry minus "task",      (same Workspace, context.Model,
 read at execution time)             ReadOnly unless the parent is AllowAll,
                                     PromptLibrary.SubAgentPrompt)
   |                                   |
   +----------------+------------------+
                    |
        new CodingAgent(models, childTools, NoPromptApprover, options with MaxIterations)
                    |
          RunTurnAsync(child, prompt, observer: null, token)
                    |
              AgentReport        FinalText · Usage · Iterations · ToolResults(Effects) · IsUnverified
                    |
        result = "task · <description> · N model calls · T tokens" + FinalText
        effects = child effects, minus Read     -> parent VerifyGate, FilesTouched, CommandsRun
```

## Layers, and why each one exists

**Infrastructure/Tools — `TaskTool`.** Arguments in, one child turn, one rendering out. It is the only file in the
round that is not a registration, a prompt line or a test. Its constructor takes `IChatModelResolver`, a
`Func<IToolRegistry>` and `AgentOptions`, and holds no mutable state, so one instance serves the process.

**Infrastructure/Tools — `TaskFormat`.** The answer's header and its counters, in one place, for the reason
`CodeFormat` exists in `015`: so the tool, the tests and the contract document cannot drift on words like
"4 model calls".

**Infrastructure/Tools — `NoPromptApprover`.** An `IToolApprover` that refuses everything. Unreachable by
construction (a child is `AllowAll` or `ReadOnly`, never `Ask`), and it exists precisely so that a later change to
the child's mode cannot start a modal prompt from inside a tool call: the prompt would arrive while the developer
is watching the parent turn and cannot tell which of two nested turns is asking.

**Application.** One new operating rule in `PromptLibrary.SystemPrompt` (rule 8). Nothing else. `CodingAgent`,
`AgentSession`, `ToolRegistry`, `AgentReport` and `SubAgentPrompt` were already the right shape — the draft's own
remarks say so, and the probe confirmed the context already carries the model and the approval mode.

**DependencyInjection.** One line in `AddTools`:

```csharp
services.AddSingleton<ITool>(provider => new TaskTool(
    provider.GetRequiredService<IChatModelResolver>(),
    () => provider.GetRequiredService<IToolRegistry>(),
    provider.GetRequiredService<AgentOptions>()));
```

The `Func` is what makes the registration possible at all (probe `F-1`), and `IToolRegistry` rather than
`GetServices<ITool>()` is what makes the child see `--mcp` tools too (probe `F-2`).

**Docs.** `specs/011-subagent-task/contracts/task-tool.md`, the roadmap row and section, `README.md`.

## Decisions

- **The draft's constructor is wrong for a container.** It takes `IEnumerable<ITool>`, which cannot be resolved:
  a tool that is part of the `ITool` set cannot be handed that set while the set is being built. Measured, not
  guessed — see `F-1`. It becomes a `Func<IToolRegistry>`, resolved when the tool executes.
- **Effects are forwarded, and that is the round's real invariant.** A child that edits `Foo.cs` and never verifies
  would otherwise let the parent end `Completed` with a clean report: `VerifyGate` reads the parent's own tool
  results, and a `task` result that reports nothing launders the edit. Copying the child's non-read effects is what
  keeps `012` true through one level of delegation — and it makes `FilesTouched`, `CommandsRun` and the console's
  closing line honest for free.
- **`Read` effects are dropped.** The parent did not read those files; forwarding them would claim it did, and a
  `list_directory`-heavy child would inflate the parent's report into a lie.
- **The child never prompts.** `AllowAll` only when the parent is `AllowAll`, otherwise `ReadOnly`, and a refusing
  approver behind it. The three existing approval answers still mean what they meant: `ApprovedForSession` on an
  earlier prompt already flips the session to `AllowAll`, so a developer who wants writing children has already said
  so. No new permission model, no fourth answer, no `--subagent-approval` flag.
- **`Destructive`, so a read-only session cannot delegate at all.** The alternative — call it `ReadOnly` and rely on
  the child's mode — would advertise `task` in plan mode and give a planning turn a writing child, because the loop's
  plan-mode filter is `Safety`, not behaviour.
- **No events from the child.** `IAgentObserver` has no nesting identifier; a child's `TurnStarted`/`ToolRequested`
  inside a parent turn would make the console's spinner, its tool lines and the transcript disagree about what is
  running. One line at each end, through the tool's own `IToolOutputSink`, is the honest amount of narration.
- **The child's model is the session's model** (`context.Model`), not the host's startup default: `/model` moves it
  mid-conversation, and a child answering on a model the developer switched away from is a surprise, not a feature.
- **The answer is bounded by the caller's budget** (`AgentOptions.MaxToolResultChars`) inside the tool, so
  `IsTruncated` is set by the tool that did the cutting rather than by the loop's trimmer afterwards.
- **Spend is reported, not folded** (FR-014). The child's `Iterations` and `Usage` are printed; the parent's token
  ceiling does not include them. Bounded instead by `MaxSubAgentIterations` per call.

## Phase 0 — what the probe actually measured

Two questions could only be answered by running something, and both changed the code. The probe was a throwaway
`net11.0` console app outside the repository (`%TEMP%\sca-011-probe`) with the same `Microsoft.Extensions.DependencyInjection`
11.0.0-rc.2 the product uses, modelling the shape rather than the product's types.

**F-1 — the draft's constructor cannot be registered.** A tool registered as `ITool` whose factory calls
`GetServices<ITool>()`, with a registry built from `GetServices<ITool>()`:

```text
mode=a exit=-532462766
Unhandled exception. System.InvalidOperationException: A circular dependency was detected for the service of type
'System.Collections.Generic.IEnumerable<ITool>'.
   at Microsoft.Extensions.DependencyInjection.ServiceLookup.CallSiteRuntimeResolver.VisitRootCache(...)
```

Startup failure, not a stack overflow: the container detects it. `IEnumerable<ITool>` is therefore not a shape
`TaskTool` can have, and the fail is a *startup* crash for every host — the CLI would not run at all.

**F-2 — the deferred source works, and the name filter does what it says.** The same container with
`Func<IReadOnlyList<ITool>>` resolved at execution time:

```text
mode=b exit=0
registry holds 2: plain,deferred
child sees: plain
```

Both halves of US2 in four lines: the registry is built, and the child's view has the other tool and not itself.

**What reading answered, and why that was enough.** The four remaining questions are answered by one line each in
the product, so running them would have proved what the code says:

| Question | Answer | Where |
|---|---|---|
| does a tool learn the conversation's model and approval? | yes, both, defaulting to `ReadOnly` | `CodingAgent.ExecuteAsync`, `ToolInvocationContext` |
| does a completed turn carry the child's final text? | yes, `FinalText` is set on `Completed` | `CodingAgent.AdvanceAsync` |
| can a child see `--mcp` tools? | only through `IToolRegistry`, which `AddMcp` replaces with a superset | `ServiceCollectionExtensions.AddMcp` |
| does the parent's gate read effects? | yes, `VerifyGate.EditsDotnetSource(ToolResult)` reads `result.Effects` | `VerifyGate`, `CodingAgent.TurnState.Add` |

## The answer format

```text
task · find every caller of PriceCalculator · 4 model calls · 18,940 tokens
Three callers, all in Billing: src/Billing/Invoice.cs:42, src/Billing/Quote.cs:118,
src/Billing/Recurring.cs:7. All three read PriceCalculator.Rate directly; none goes through the
repository. Nothing outside Billing references it.
```

Two properties are deliberate. The first line is the only machine-ish part, and it is what makes a delegation
visible in a transcript a week later: which instruction, how many calls, how much it cost. Everything after it is
the child's own final message, verbatim — the tool adds no summary of its own, because a second summary is a second
place to be wrong.

When the child does not complete, the same shape names the outcome:

```text
task · count every TODO under src · 12 model calls · 41,002 tokens
the sub-agent ran out of its 12 model calls without answering (BudgetExhausted). Ask for a narrower
question, or raise max_iterations.
```

## Risks, and what bounds each one

- **Spend multiplication.** A model that delegates instead of working spends twice: the child's tokens plus the
  parent's. Bounded by `MaxSubAgentIterations` per call (24) and by the parent's iteration ceiling; **not** bounded by
  `MaxTotalTokens`, which cannot see the child (FR-014). The header line is what makes the cost visible while it is
  happening, and `/budget` still ends the parent turn.
- **A child that cannot do the job it was given.** In the default `Ask` mode a child may read and not write, so a
  delegated edit burns iterations on refusals. The tool's description says this in one sentence, and `US3` prefers a
  refusal to a silent write.
- **A vague prompt.** The child sees the instruction and nothing else; a prompt like "fix the bug" gets a confident
  wrong answer. `SubAgentPrompt` already tells the child to decide or say what it could not determine, and the
  tool's description tells the parent to state the goal, the paths it knows and what done looks like.
- **A child that runs long.** The child's own `TurnTimeout` and the parent's cancellation token both apply, so Ctrl+C
  stops the pair; a child that outlives the parent's wall clock is cancelled by the parent's budget, not left behind.
- **A registry that changes under the child.** `--mcp` starts servers at container build; the registry is read once,
  when the tool executes, and the child's copy is a `ToolRegistry` built from that read. Nothing reconnects mid-child.

## What this round does not touch

- `SharpCoreAgent.Domain` — no new type, no new port. The child is a tool result, and a tool result is already a
  record of text plus effects.
- The turn loop — `CodingAgent` is used, not changed. If the round needed a hook in the loop, the tool contract would
  be the wrong one to hang delegation on.
- `SharpCoreAgent.Indexing` and `Domain/Index` — untouched; a child reads through the same tools the parent has.
- Providers, credentials, the CLI's options and help text — no flag, so no row in `--help`.





