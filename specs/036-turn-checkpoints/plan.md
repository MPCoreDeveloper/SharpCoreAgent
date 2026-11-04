# Implementation Plan: Turn checkpoints and `/undo`

**Feature**: `036-turn-checkpoints`
**Status**: implemented 2026-11-04

## Architecture

Three layers, each replaceable on its own:

1. **Domain seam.** `ITurnCheckpoint` is an `IDisposable` capability on `ToolInvocationContext`. A mutating tool calls
   `CaptureAsync(relativePath, fullPath)` after validation and before its first write; `null` means captured, a message
   means the capture failed and the write must not happen. `ToolInvocationContext.Checkpoint` is nullable, so a
   hand-built context and every pre-checkpoint test keep their old behaviour.
2. **Application contract.** `ITurnCheckpointJournal.BeginTurn()` returns the checkpoint; `UndoLast()` restores the
   latest completed turn. `CodingAgent` opens one checkpoint per `RunTurnAsync` with `using var`, passes it into
   `TurnState`, and from there into every `ToolInvocationContext`. It depends on the interface, not the store.
3. **Infrastructure store.** `TurnCheckpointStore` owns the history, snapshots, fingerprints and restore operations.
   It is registered as one singleton with the journal interface; `TaskTool` receives the same journal and gives it to
   the child `CodingAgent`.

## Lifetime and grouping

`TurnCheckpointStore` keeps an `AsyncLocal<TurnCheckpoint?>` for the current async flow. `BeginTurn` links the new
checkpoint to the current one; disposing it:

- for a nested turn, merges its snapshots into the parent;
- for a top-level turn, fingerprints each captured path, drops entries whose pre-state equals the end state, and pushes
  the remainder onto a LIFO stack.

An empty checkpoint is never pushed. A review or plan turn therefore leaves the previous edit in place. The journal is
process-local; a resumed session cannot undo work from the process that produced the transcript.

## Snapshot model

A capture records one of:

| Kind | Stored | Restore |
|---|---|---|
| Missing | no bytes | delete the path if it exists |
| File | exact bytes + SHA-256 + length | write a temporary file in the same directory and atomically replace |
| Link | link target | delete the current entry and recreate the symbolic link |

Bytes are read once and hashed; the content is preserved including BOM, CRLF/LF and the absence of a trailing newline.
`FileInfo.LinkTarget` is checked before `Exists` so a link is not followed and written through. Directories are refused.
One file is capped at 64 MiB; a larger file returns an explanatory capture error and the tool refuses the mutation.

## Why end fingerprints

Restoring the pre-state without checking what is on disk now would overwrite a developer who edited the file after the
turn. At top-level completion the store therefore fingerprints each captured path again and keeps the end fingerprint.
`/undo` refuses a path whose current fingerprint differs from that end state and reports `changed after the turn ended;
it was left untouched`. The checkpoint remains, so putting the file back into the state the agent left and retrying is
possible. This is the rule that makes "agent-owned changes only" true across time, not merely at capture time.

## Restore

The restore walks snapshots in insertion order. A path already restored by an earlier failed attempt is skipped. Each
success is recorded in the checkpoint; failures are collected rather than thrown. The history entry is popped only when
all paths succeed, which makes `/undo` retryable after a permission problem or a later developer edit is resolved.

Restore writes through a temporary file in the destination directory and `File.Move(..., overwrite: true)`, so a failed
write cannot truncate the current file. Missing parent directories are recreated. Empty directories created by a turn
are not removed: the journal tracks files, not directory history.

## CLI

`/undo` is handled beside `/review` because both touch state outside the command parser. It calls
`ITurnCheckpointJournal.UndoLast()`, prints counts and up to twenty paths/failures, and appends a user-role developer note
only when at least one file was restored or removed. The note is a user message rather than a system message because a
system role in the middle of a compatible provider's transcript is less portable.

## Complexity and limits

- Snapshots are memory; not persisted. Large histories can use memory proportional to the captured bytes.
- Shell, MCP and verifier changes are not tracked; no file-system watcher or full-tree snapshot is added.
- Symbolic links are restored where the platform permits creating them; a permission failure is reported and the
  checkpoint kept.
- The no-op decision is byte equality; timestamp-only changes are not an undo step.
- No new process, network, tool or settings surface is added.
