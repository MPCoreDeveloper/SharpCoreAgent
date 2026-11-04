# Feature Specification: Live-model evaluation suite

**Feature**: `039-live-model-evaluation`
**Status**: implemented 2026-11-04

## Problem

The recorded evaluation proves the loop, tool registry and independent grader, but it cannot answer whether a real
provider can solve the same tasks. A live run has different costs and different failure modes: it can spend money,
hit rate limits, take minutes, and produce a different but still-correct transcript. Treating that as the checked-in
baseline would make the default gate nondeterministic and would turn a provider outage into a repository failure.

## What ships

- `--live` opt-in mode on `eng/SharpCoreAgent.Evaluation`.
- Provider and model selection through the same `IChatModelResolver` and `SharpCoreAgentOptions` the product uses.
- A separate live report (`artifacts/evals/live.json` by default), never the recorded baseline.
- Credential preflight before the first request and clear exit code 2 when no key is configured.
- Explicit iteration, output-token and total-token caps for every scenario.
- No `--check` comparison for live results; task success is still graded by the same independent checks.
- Recorded mode remains the default, deterministic CI gate.

## User stories

1. **Measure a real provider.** A maintainer runs `--live --provider deepseek` and gets the same three scenarios,
   worked through the production model adapter, with per-scenario token usage and independent grading.
2. **Survive a missing key.** A CI worker without `DEEPSEEK_API_KEY` receives a one-line error and exit code 2 before
   any request is sent.
3. **Bound the bill.** A maintainer sets `--max-total-tokens 50000`, `--max-output-tokens 2000` and
   `--max-iterations 20`; the loop stops at those limits exactly as it does in a normal session.
4. **Keep the gate deterministic.** Ordinary `--check` continues to compare only recorded reports and remains the
   repository's pass/fail gate.
5. **Review a live run without pretending it is a baseline.** The live JSON is a dated observation; a human decides
   whether a result is acceptable. A provider that solves a scenario differently is not a recorded regression.

## Functional requirements

- **FR-001**: `--live` selects live execution. `--provider`, `--model`, `--base-url`, `--credential-file` and the live
  limits are rejected without it.
- **FR-002**: Live execution calls `AddSharpCoreAgent` with the selected provider, model, endpoint and credential file,
  then uses the registered `IChatModelResolver`; the harness does not implement a second transport.
- **FR-003**: `--check` is rejected with `--live`. `--baseline` remains valid only with `--check`.
- **FR-004**: The live default output is `artifacts/evals/live.json`; a live run may never write
  `eng/evals/baseline.json`, including through an explicit `--output`.
- **FR-005**: The harness checks the configured credential before creating a scenario workspace. A demo provider needs
  no key; a provider with no configured key fails with exit code 2 and names the accepted variables, never a value.
- **FR-006**: Default live limits are 40 iterations, 4,000 output tokens per turn and 250,000 total tokens per scenario.
  Values are bounded at parse time and passed to `SharpCoreAgentOptions`.
- **FR-007**: The report records `mode: "live"`, the provider and the selected model. Existing recorded reports keep
  `mode: "recorded"` and remain schema version 1.
- **FR-008**: Independent grading is identical in both modes. A live task qualifies only when the expected workspace
  changes, verification evidence and post-turn grading command all pass. File existence is checked in both modes;
  exact source-fragment assertions are recorded-only, because a live model may choose an equivalent implementation and
  the independent behavior check is the authority.
- **FR-009**: A live run is never a CI gate by default. The repository's normal commands and `--check` remain offline and
  deterministic.
- **FR-010**: Cancellation still returns exit code 4; provider/task failures return 1; malformed arguments return 2.

## Nondeterministic comparison policy

- Exact transcript, iteration count, file-change ordering and token count are observations, not equality checks.
- A live report is compared by a human against the scenario's user-visible outcome: the required files, the
  independent grading result, and the absence of unintended workspace changes.
- A repeated live run may take a different path. The suite does not fail because two live reports differ from each
  other, and it does not promote either report to `baseline.json` automatically.
- Any future automatic live comparison must be opt-in and must state its tolerance; this slice deliberately provides
  no tolerance-based gate.

## Non-goals

- Running live evaluation from the default CI workflow or requiring provider credentials to build/test the repository.
- Recording a live transcript as a deterministic fixture.
- Automatic retries, parallel scenarios, provider-side rate-limit backoff policy, or a cost estimator beyond the
  provider's reported usage.
- Changing the recorded corpus, its baseline or its comparison rules.
- A general benchmark leaderboard or claims that one provider is better than another.

## Success criteria

- Recorded `--check` remains 3/3 with the existing baseline.
- `--live --provider demo` produces a live report and never touches the recorded baseline.
- Missing credentials fail before a request with exit code 2.
- `--live --check`, `--provider` without `--live`, and `openai-compatible` without `--base-url` are rejected.
- Release build has zero warnings and the complete suite remains green.
