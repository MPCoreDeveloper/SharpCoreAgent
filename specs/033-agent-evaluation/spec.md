# Feature Specification: Agent task evaluation

**Feature**: `033-agent-evaluation`
**Created**: 2026-11-04
**Status**: implemented 2026-11-04 — recorded baseline, independent grading, comparison and 15 focused tests landed

## Why this exists

The unit suite proves tools and turn mechanics, and the index has a retrieval-quality corpus. Neither answers whether an
agent turn completes a coding task without damaging the rest of the workspace. This feature supplies a reproducible
baseline before C# code intelligence and other agent improvements begin.

The first baseline uses recorded model responses. It measures the shipped loop, tool wiring, file effects and independent
grading. It does **not** claim to measure a live model's coding ability; a live-provider suite is a later slice.

## User stories

### US1 — Run one comparable coding-task baseline (P1)

A maintainer runs one command from a checkout. The harness copies each scenario into a new temporary workspace, drives
the production `CodingAgent` and tool registry with recorded responses, independently grades the result, and prints one
outcome per task plus an aggregate.

**Independent test**: run the C#, Python and JavaScript scenarios with their declared runtimes installed. Each must make
only its intended source edit, pass agent-facing verification and pass checks materialised after the turn ends.

### US2 — Detect regressions rather than admire output (P1)

A maintainer compares a run with the checked-in baseline. A lost task, new tool error, unintended file change, extra model
round trip or increased recorded token usage is printed as a regression and exits non-zero. An absent runtime is skipped
with a reason, never counted as a pass.

**Independent test**: compare an unchanged report, then deliberately break an expected source assertion and add an extra
turn. The unchanged comparison is empty; each regression is named.

### US3 — Inspect exactly what happened (P2)

A maintainer reads JSON to inspect final text, changed paths, commands, tool outcomes, recorded token usage, timing and
grading failures without receiving a temporary machine path or provider credential.

**Independent test**: the report parses as the versioned contract and contains the same outcomes and metrics as the
console summary. Temporary paths are replaced and command output is bounded.

## Functional requirements

- **FR-001**: Discover scenarios in ordinal identifier order, reject duplicates, and optionally run one named scenario.
- **FR-002**: Copy every fixture into a new temporary workspace. No agent tool may address the checked-in fixture or
  another scenario's workspace.
- **FR-003**: Replace only the model resolver and approval policy. Use the production loop, prompt, tools, verification
  gate and result limits; disable index, web, MCP and F# capabilities.
- **FR-004**: Stream recorded responses through `IStreamingChatModel`, with stable call identifiers and deterministic
  estimated usage labelled `recorded`, never live-provider usage or monetary cost.
- **FR-005**: Grade the filesystem independently of tool effects. Every created, modified or deleted non-generated path
  outside `allowedChanges` fails the task.
- **FR-006**: Required verification MUST follow the last edit, name the declared tool/command, and pass. `run_shell`
  requires its explicit `exit code: 0` line; a command that ran and failed is not verification.
- **FR-007**: Copy independent grading files only after the turn and change audit. Run the grading command through
  `RunShellCommandTool`; require exit code zero and every declared output fragment.
- **FR-008**: A failed runtime probe produces `skipped` with the probe named, never `passed`. Source or verification
  failures cannot skip.
- **FR-009**: Schema-versioned reports MUST include per-task status/outcome, verification, iterations, calls, errors,
  changes, usage, duration, commands, bounded tool results, failures and final text. Skip counts are separate and success
  is passed / attempted.
- **FR-010**: Baseline comparison ignores timestamps and durations, but compares identity and semantic task, error,
  change, iteration and token metrics. `--check` never writes a baseline and refuses an output path equal to its input.
- **FR-011**: Exit 0 means at least one task was attempted, every attempted task passed and no requested regression
  exists; 1 means failure/regression or no attempted task; 2 means bad arguments or data; 4 means cancellation.
- **FR-012**: Add no production `ITool`, provider, model API or `Process.Start` call site.

## Success criteria

- **SC-001**: Two complete recorded runs on one machine produce identical scenario/tool-call order, actual changes,
  pass/fail outcomes, iterations and token metrics.
- **SC-002**: The corpus contains at least one .NET/C# and two non-.NET coding tasks, each with independent behavioural
  grading; all attempted tasks pass on the authoring environment.
- **SC-003**: Focused tests prove that an incorrect edit, unexpected file, failed grading command and increased semantic
  metric cannot be reported as successful.
- **SC-004**: The normal evaluation needs no provider credential and performs no model or web request.
- **SC-005**: The solution builds in Release with zero warnings and the complete test suite passes.

## Out of scope

Live-provider calls, SWE-bench acquisition, monetary cost, parallel scenario execution, semantic diff review, IDE
integration, C#/VB compiler adapters, repository-wide CI requirements for Python or Node, and improvement targets without
a measured live-model baseline. The three recorded tasks are the harness baseline, not evidence of model intelligence.
