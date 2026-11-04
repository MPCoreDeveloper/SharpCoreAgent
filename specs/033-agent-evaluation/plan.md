# Implementation Plan: Agent task evaluation

**Feature**: `033-agent-evaluation`
**Status**: implemented 2026-11-04

## Architecture

A new executable under `eng/SharpCoreAgent.Evaluation` references Infrastructure and DependencyInjection. It is a
maintainer tool, not a command in `sharpcoreagent` and not a new production provider. The solution includes it under
`/eng/`; the existing test project references it to exercise its internal contract.

`EvaluationScenarioLoader` reads each `scenario.json` and validates the fixture's static policy. `RecordedChatModel`
plays response turns through the normal streaming protocol. `EvaluationRunner` creates a production service provider,
replacing only `IChatModelResolver` with the recording and `IToolApprover` with an always-approve policy scoped to the
new disposable workspace. `EvaluationWorkspace` owns fixture copying, before/after hashes and cleanup.

After the turn, the runner audits bytes, checks source assertions and verification evidence, materialises independent
grading files, and invokes the existing `RunShellCommandTool` directly. Direct invocation is the harness's explicit
approval boundary, not a model capability. It creates no process API or launcher.

`BaselineComparer` compares only semantic metrics. `EvaluationCli` handles arguments, output and exit codes. JSON source
generation covers scenario and report documents.

## Scenario contract

Each `eng/evals/scenarios/<id>/` contains:

- `scenario.json`: identifier, ecosystem, prompt, recorded turns, allowed source changes, verification and grade.
- `workspace/`: everything available to the model, including its visible checks.
- optional `grading/`: independent tests copied only after the agent's turn and filesystem audit.

The initial corpus fixes a C# division bug, a Python cart-total bug and a JavaScript discount bug. Python and Node probes
make unavailable optional runtimes explicit. The .NET fixture has no package references and clears package sources.

## Metrics and comparison

Pass requires a completed and verified turn, no unexpected changes, all source assertions, declared agent verification,
and a successful independent grade. Attempted excludes runtime-skipped tasks. Tool errors and refusals are recorded even
when a later recovery still lets the task pass. Recorded input/output tokens are deterministic character-based estimates
over stable messages, tool definitions and recorded responses; dynamic tool-result text is excluded because its timing
fields vary. These figures are not provider prompt estimates or billing data.

A baseline regression is a missing or additional scenario in a full comparison, a passed task becoming unpassed, more
errors/refusals/unintended changes, more iterations, or greater recorded usage. Timing and timestamps never participate.

## Testing

- Scenario loading and rejection of invalid policy.
- Recorded model stable calls and usage.
- Full real-loop success on a small text fixture.
- Incorrect edit and unexpected file detection.
- Failed command and absent-runtime handling.
- Baseline no-op and regression comparisons.
- CLI output/report contract and invalid arguments.
- Complete recorded corpus twice; compare semantic results.
- Release solution build and full test suite.

## Complexity Tracking

The harness necessarily has a scenario format, runner and report format. It adds no production abstraction and no new
package beyond the already used concrete DI container. Filesystem setup/report writing belong to the host; every command
available to the agent or grader remains an `ITool`. Live-provider abstraction is postponed rather than speculatively
building a second evaluation architecture.

## Constitution check

Domain is unchanged. `Process.Start` remains only in the shell implementation and MCP client. Network, index, F# and MCP
are disabled. No credential is read for the recorded resolver. Temporary workspaces are destroyed after each scenario.
All scenario order and model responses are deterministic; elapsed time is measurement only and excluded from comparisons.
