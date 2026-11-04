# Tasks: Context-fraction automatic compaction

- [x] T001 Add `PromptTokenEstimator` with message, tool-call, image and tool-definition counting.
- [x] T002 Add `AgentOptions.CompactAtContextFraction` and validation.
- [x] T003 Wire `CompactAtContextFraction` through `SharpCoreAgentOptions` and `CliOptions`.
- [x] T004 Add `--compact-at <fraction>` to parsing and help.
- [x] T005 Extend `CodingAgent.CompactAsync` with the size trigger and progress guard.
- [x] T006 Emit one `ContextCompacted` event for the accumulated drops.
- [x] T007 Tests: default/fraction bounds, CLI mapping, size-trigger compaction and disabled trigger.
- [x] T008 Release build zero warnings; full suite **1,637/1,637**; recorded evaluation 3/3 at 61,573/249.

## Evidence

- `APromptOverTheContextFractionIsCompactedBeforeTheRequest`: a 200-token descriptor with a large transcript compacts,
  reports `Dropped > 0`, and finishes with an estimate at or below the 100-token fraction.
- `AContextFractionOfZeroLeavesTheTranscriptAlone`: no event and no dropped messages.
- `TheContextFractionMayBeDisabledOrSetAtItsEdges`: `0`, `0.1`, `1` accepted; `0.09` and `NaN` rejected with the
  parameter name.
- The existing `ATranscriptPastTheWindowIsCompactedAndTheTurnSaysSo` still sees `Kept = 10`.

## Out of scope

Provider-exact tokenization, semantic summaries, image eviction from the newest message, persistent compaction history,
and Git checkpoints.
