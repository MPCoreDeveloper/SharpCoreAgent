# Feature Specification: Context-fraction automatic compaction

**Feature**: `042-context-fraction-compaction`
**Status**: implemented 2026-11-04

## Problem

The loop already compacts when the transcript exceeds `MaxContextMessages`, but a message count cannot see the size of
one request. A single large tool result, a long system prompt or a large tool-definition set can push a request past the
model's context window while the message count still looks ordinary. `003` T034 asked for a size-aware trigger: compact
when the prompt is a configurable fraction of the model's advertised context window.

## What ships

- `AgentOptions.CompactAtContextFraction`, default `0.8`; `0` disables the size trigger.
- `--compact-at <fraction>` on `run` and `chat`, with the same setting on `SharpCoreAgentOptions`.
- A BCL-only `PromptTokenEstimator` that counts messages, tool calls, images and tool definitions.
- A pre-request loop that compacts through `AgentSession.Compact(1)` until the estimate fits or no further cut is safe.
- The existing message-count trigger and `/compact` remain, and recorded evaluation is unchanged.

## User stories

1. **A huge tool result arrives.** The next request is measured, and old history is dropped before the provider rejects
   the turn.
2. **A model has a small window.** A custom model can set a low context window; the loop compacts at the configured
   fraction instead of waiting for a provider 400.
3. **The developer wants manual control.** `--compact-at 0` leaves only the message-count and `/compact` paths.
4. **A tool round is in flight.** Compaction never starts a window on a tool result; when no safe cut exists it stops and
   sends the request as it is rather than producing an invalid one.
5. **Visibility.** A compaction emits the existing `ContextCompacted` event, so terminal and JSON observers report it.

## Functional requirements

- **FR-001**: The default fraction is `0.8`; the setting is `0` or a value in `[0.1, 1.0]`.
- **FR-002**: The estimate includes message content, tool-call names and arguments, image base64 length, and every
  advertised tool's name, description and schema. It is an estimate, never a billing claim.
- **FR-003**: The trigger is evaluated before every request, including the first request of a turn.
- **FR-004**: A model with no `ContextWindow` is not affected by this trigger. The message-count trigger still applies.
- **FR-005**: When over the threshold, the loop calls `AgentSession.Compact(1)` so the newest non-system message and the
  system prompts survive; this is intentionally more aggressive than the count trigger's half-window rule.
- **FR-006**: The loop stops if `Compact` drops nothing or the transcript did not shrink, so a tool result that cannot be
  separated from its call cannot cause an infinite loop.
- **FR-007**: The total dropped count is emitted once as `ContextCompacted`; `Kept` is the last successful `keep`.
- **FR-008**: Tools, model resolution, approvals and the transcript format are unchanged.
- **FR-009**: `--compact-at` is rejected on commands that do not run a turn by the existing turn-flag parser.

## Non-goals

- Exact tokenizer parity with any provider; four characters per token is the documented estimate.
- Summarizing dropped messages semantically; the existing elision notice is retained.
- Compacting images out of the newest request when the newest message is the one carrying the image.
- Persisting compaction decisions or changing the transcript schema.
- Git checkpoints; this slice is about context, not file history.

## Success criteria

- A 200-token model with a large transcript compacts before its request and reports the event.
- `CompactAtContextFraction = 0` leaves the transcript untouched at the same size.
- The existing count-based test still reports `Kept = 10` and a transcript within its window.
- Release build zero warnings; complete suite and recorded evaluation remain green.
