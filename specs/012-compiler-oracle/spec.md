# 012 — The compiler oracle: a turn cannot claim what it did not build

**Status**: implemented in this change (T001–T018; the reasoning that is still open is listed in `tasks.md`)
**Depends on**: `001-coding-agent-core` (the turn loop, the tool and result contracts), `003-terminal-console-agent`
(the report the console prints), `013-evidence-mcp-server` (the registry the gate reads, and the one command
line that must stay ungated)

## Why this feature exists

The prompt has told the model since `001` to build after it edits, and the transcript shows what actually
happens: on a turn that touches four files it runs `run_shell` with `dotnet build` once, reads the first
warning, and finishes with *"the changes compile"* — a sentence nothing in the turn supports. The developer
then runs the build themselves, which is precisely the work the agent was bought to do.

Two things are missing, and they are different things. The first is a **tool that answers the question
"does this compile, and do the tests pass"** with a shape a model can act on: the exit code alone is a
number, and 20 000 characters of MSBuild chatter is not a report. The second is **a rule that makes the
answer unavoidable**: a turn that changed .NET source may not end `Completed` on the model's word.

That is the whole round. It adds no flag, no provider, no dependency and no second way to start a process:
the compiler is a tool, the tool runs through the shell host that already exists, and the loop reads the
result it already records.

## User stories

### US1 — Ask the harness, get a report (P0, Phase 1)

As a developer who just watched a turn edit six files, I want the build's verdict in one labelled block, so
that I can read the outcome without scrolling through the compiler's own log.

Acceptance:

- `dotnet_verify` with `kind: "build"` runs `dotnet build` in the workspace and answers with the exit code,
  `passed`/`failed`, the warning count, the error lines, the duration and the summary line MSBuild printed.
- `kind: "test"` runs `dotnet test` and also reports how many tests passed and how many failed.
- A `project` argument narrows the run to one project or solution, relative to the workspace root, and is
  refused if it escapes the root or does not exist.
- A build that fails is an **error result**: the text is complete (every diagnostic is there), and the
  result is marked failed so the loop and the transcript say so.

### US2 — A turn cannot end on the model's word (P0, Phase 1)

As the person who reads `✻ completed` and believes it, I want a turn that edited .NET source to end only
after a passing `dotnet_verify`, so that "completed" means the harness ran rather than the model hoped.

Acceptance:

- A turn whose tool results include a created, modified or deleted `.cs`, `.vb`, `.fs`, `.csproj`, `.vbproj`,
  `.fsproj`, `.props`, `.targets`, `.sln` or `.slnx` file, with no passing `dotnet_verify` **after** that
  edit, is not allowed to end: the loop asks once, then twice, then accepts the answer with
  `AgentReport.IsUnverified` set and a `Detail` that says why.
- A `dotnet_verify` that passed *before* the edit does not count. The comparison is the order of the tool
  results, not the presence of a name.
- A turn that edited no .NET file, a turn in plan mode, and a host whose registry has no `dotnet_verify` are
  not gated at all: they end exactly as they do today.

### US3 — The console says which kind of "completed" this was (P1, Phase 1)

As a developer scanning a transcript, I want the closing line to tell me when a completion was not
verified, so that I do not have to trust the word either.

Acceptance:

- An unverified completion prints a `⚠ unverified:` line under the outcome, and the JSON report carries
  `"unverified": true`.
- A verified completion prints exactly what it prints today.
- No exit code changes: `Completed` is `0` whether or not it was verified.

### US4 — The prompt and the tool ship together (P0, Phase 1)

As a maintainer, I want the instruction that tells the model to build and the tool that builds it to be
the same sentence, so that a model is never told to call something that is not registered.

Acceptance:

- `PromptLibrary.SystemPrompt` rule 5 names `dotnet_verify` for builds and tests and no longer tells the
  model to prove a .NET edit with `run_shell`.
- The prompt names no tool the shipped container does not register: `dotnet_verify` is registered by
  `AddTools` unconditionally, beside `run_shell` and `shell_output`.
- The nudge the loop appends (`PromptLibrary.VerifyNudge`) names the tool and the obligation.

## Functional requirements

- **FR-001** `dotnet_verify` is a `ToolSafety.Destructive` tool, because it starts a compiler and writes
  `obj/` and `bin/`. In `readonly` approval it is refused like any other destructive tool, and the refusal
  leaves the gate open — an unverified completion, not a silent pass.
- **FR-002** Arguments: `kind` (`build` or `test`, required), `project` (optional, workspace-relative),
  `timeout_seconds` (optional, 300 by default, at most 900). Anything else is a `ToolArgumentException`.
- **FR-003** The process is started with `ProcessStartInfo.ArgumentList` — never through a shell — so a path
  with a space needs no quoting and there is no command string to break out of.
- **FR-004** The child's environment has every `*_API_KEY`, `*_TOKEN`, `*_SECRET` and `*_PASSWORD` removed,
  by the same helper `run_shell` uses, so the compiler cannot read the key that pays for the conversation.
- **FR-005** The child is told `DOTNET_CLI_UI_LANGUAGE=en`, because the parser reads English. A localized
  build would otherwise be reported as `exit code 1` with no summary and no error list.
- **FR-006** The timeout kills the process tree and answers with a failure naming the seconds and pointing
  at `run_shell` + `shell_output` for a build that legitimately outlives one call.
- **FR-007** One effect on every run that finished: `CommandRun` with the command line, so the turn's
  `CommandsRun` and the console's JSON record what was built — whether it passed or not.
- **FR-008** The gate reads `ToolResult.ToolName`, `IsError`, `IsRefused` and `Effects` only. It adds no
  port to the domain and no reference from `Application` to `Infrastructure`; the tool's name lives in
  `SharpCoreAgent.Domain.Tools.ToolNames` for exactly that reason.
- **FR-009** At most two nudges per turn (`CodingAgent.MaxVerificationNudges`), so a genuinely broken tree
  cannot spend the iteration budget being told to build again.
- **FR-010** The gate is satisfied by a `dotnet_verify` result that is neither an error nor a refusal. A
  failing build leaves it open: the model is asked to fix and re-run, which is the behaviour the round
  exists to produce.

## Non-goals (this round)

- **Roslyn.** It is already here, behind `file_diagnostics`, and it stays there: one file can type-check while
  the project does not build, because only the build restores, resolves and links. A clean front-end answer
  must not close a gate whose question is "does this build". `SCA0001` is likewise untouched: this feature
  reads .NET 11 code, it does not require it.
- **A build server, a file watcher or an incremental cache.** `dotnet build` already does all three.
- **Language detection.** The list of extensions is a constant, not a syntax-table lookup; a `.cs` file in a
  project that is not .NET is still a .NET edit, and the build says so.
- **A second `Process.Start`.** The tool goes through `ShellProcessRegistry`, the constitution's shell host,
  so the count in Principle III does not change.
- **Scripts, SQL and notebooks.** Each needs its own oracle, and none is here: shell syntax checks do not
  prove a script runs, SQLite would reject valid SQL written for another dialect, and a notebook is code plus
  outputs plus a kernel. Until one of those runtimes is chosen and wired through the shared process host, an
  edit to `.ps1`, `.sh`, `.sql` or `.ipynb` is not gated rather than gated by a weaker answer.
- **A `--no-verify` flag.** The gate is the point of the round; a flag that turns it off would be the first
  thing in a script and the last thing anyone remembers. `--mode plan` is the existing, honest passthrough.
- **Retrying the build automatically.** Two nudges, then the truth about what happened.

## Measures

| Measure | Target | How it is checked |
|---|---|---|
| A turn that edits source and never verifies is not `Completed` silently | 100% | `CodingAgentTests`: `ATurnThatEditsSourceAndNeverVerifiesIsCompletedButUnverified` |
| A passing verify after the edit closes the gate on the next answer | 100% | `CodingAgentTests`: `ATurnThatEditsSourceAndVerifiesCompletesVerified` |
| A verify before the edit does not count | 100% | `CodingAgentTests`: `AVerificationThatRanBeforeTheEditDoesNotCloseTheGate` |
| A turn with no .NET edit costs not one extra model call | 0 nudges | `CodingAgentTests`: `ATurnThatEditsNoDotnetSourceIsNotGated` |
| The report names the diagnostics, not just the exit code | at least one error line per failing build | `DotnetVerifyReportParserTests`, `DotnetVerifyToolTests` |
| The suite stays green with zero build warnings | 0 warnings, 0 failures | `dotnet build .\SharpCoreAgent.slnx -c Release`, `dotnet test` |

## Exit codes

| Code | Meaning |
|---|---|
| 0 | `Completed`, verified or not. `unverified` is report data, not a process outcome. |
| 1 | Unchanged: `Failed`, `BudgetExhausted`, `Cancelled`. |

No new number: an unverified turn is a turn that ran, and the developer's remedy is the transcript
(`⚠ unverified:`), not a shell script branching on `$?`.

## Risks

- **Two nudges is a guess.** A model that ignores both ends `Completed` with `IsUnverified`, which is the
  honest outcome; a model that obeys needs one. What is not acceptable is an unbounded loop, so the ceiling
  is a constant with a name rather than a policy.
- **The gate is a string match on `Effects`.** A tool that edits a `.cs` file without reporting the effect
  slips through. Every writing tool in this build reports one — `write_file`, `replace_in_file`,
  `delete_file` — and the effect list is what `FilesTouched` already depends on, so a tool that lies there
  has already broken the report.
- **`dotnet test` output is not stable.** The counts are read from the `Passed!`/`Failed!` summary and the
  exit code decides the verdict, so a new log shape costs the counts and not the outcome.
- **A build can be slow.** 300 s is the default and 900 s the ceiling; beyond that the answer is a failure
  that names `run_shell` + `shell_output`, which is the tool designed for a command that outlives its call.
- **A `Directory.Build.props` above the workspace** can make `dotnet build` restore packages this machine
  does not have, and the failure is the compiler's message — which the report prints verbatim.

## Decisions

1. **A report, not an exit code.** The tool answers with labelled facts; the model reads a report, the
   developer reads a column, and neither has to parse MSBuild's prose.
2. **A failed build is an error result.** `run_shell` reports a non-zero exit code as success because the
   command *ran*; `dotnet_verify` says a build *failed* because that is the fact the turn is gated on.
3. **The gate compares order, not presence.** The verify has to come after the last edit, which is the only
   reading that cannot be satisfied by a stale result.
4. **The gate lives in the loop, not in the tool.** `Application` decides whether a turn may end; a tool
   that tried to gate its caller would be a tool that knows too much.
5. **`ToolNames.DotnetVerify` in the domain.** One string, in the layer both sides can see, so the loop and
   the tool cannot disagree about the name that decides whether a turn may end.
6. **The gate is a gate, not a veto.** After two nudges the turn completes and says it is unverified. A turn
   that can never end is worse than one that ends honestly, because the developer loses the report too.
