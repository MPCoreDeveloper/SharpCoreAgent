# Feature Specification: Turn checkpoints and `/undo`

**Feature**: `036-turn-checkpoints`
**Status**: implemented 2026-11-04

## Problem

The transcript records what the agent did, but there is no way to undo it. A developer who dislikes a turn has to
reconstruct every file from memory, `git diff` or an editor's history — and a workspace without Git has none of those.
The existing `003` backlog listed "git checkpoints and `/undo`" as not started; this slice supplies the capability
without requiring the workspace to be a repository.

## What ships

- One in-memory checkpoint per turn, opened by `CodingAgent`.
- Exact pre-change capture in `write_file`, `replace_in_file` and `delete_file`.
- `/undo` in `chat`, repeatable for earlier turns.
- Sub-agent file changes merge into the parent turn.
- A restore refuses to overwrite a file that changed after the turn ended.
- A developer note appended to the transcript after a successful or partial restore.

## User stories

1. **Reject a bad edit.** A developer reads the turn's answer, types `/undo`, and the files are exactly as they were
   before the request.
2. **Undo a deletion.** `delete_file` removed a file; `/undo` recreates it with the bytes it had.
3. **Remove a creation.** A turn made three new files; `/undo` removes those files and leaves every other file alone.
4. **Undo a sub-agent's work.** The parent turn delegated edits through `task`; one `/undo` restores the files the child
   changed as part of the same turn.
5. **Protect work made afterwards.** The developer edits a file after the turn, then runs `/undo`; the new edit is left
   untouched and the command says why.
6. **Walk back further.** Repeated `/undo` calls restore earlier turns in reverse order.

## Functional requirements

- **FR-001**: `CodingAgent` opens a checkpoint for every turn, including read-only, failed, cancelled and budget-exhausted
  turns. A checkpoint with no net file change is discarded.
- **FR-002**: `write_file`, `replace_in_file` and `delete_file` capture the target after validation and before mutation.
  A capture failure refuses the mutation with an explanation and writes nothing.
- **FR-003**: The first capture of a path in a turn wins; later edits of the same path do not replace the pre-turn state.
- **FR-004**: A checkpoint records whether the file was absent, a regular file's exact bytes (including BOM and line
  endings), or a symbolic-link target.
- **FR-005**: At the end of a top-level turn, a fingerprint of each captured path is recorded. A path whose pre-state and
  end-state are identical is removed from the checkpoint, so a no-op turn is not an undo step.
- **FR-006**: A nested turn (a `task` sub-agent) merges its captures into the parent turn rather than pushing a separate
  undo step. File paths only a child touched become part of the parent's restore.
- **FR-007**: `/undo` restores the most recent completed checkpoint in LIFO order. Modified files get their old bytes,
  deleted files are recreated, absent files are removed, and symbolic links are recreated.
- **FR-008**: Before restoring a path, `/undo` compares its current fingerprint with the end-of-turn fingerprint. A
  mismatch (developer edit, replacement, directory or unreadable file) is reported and the file is left untouched.
- **FR-009**: A failed restore keeps the checkpoint in the journal. Files already restored in that attempt are remembered
  and skipped on a retry; when every file succeeds the checkpoint is consumed.
- **FR-010**: `/undo` reports restored and removed counts and paths, plus each failure. A successful or partial restore
  appends a developer-role note to the transcript saying the prior file edits no longer exist.
- **FR-011**: One captured file is capped at 64 MiB. A larger file is refused before mutation. History is process-local
  and discarded when the process exits.
- **FR-012**: The mechanism adds no tool, no process launch, no network call and no persistent storage.

## Non-goals

- Undoing changes made by `run_shell`, MCP tools, `dotnet_verify` or arbitrary commands, including commands a sub-agent
  runs. Those tools do not report per-file mutations the journal can capture.
- Cross-process or resumed-session history, disk persistence, background snapshots and file-system watching.
- Restoring file permissions, timestamps, ownership, extended attributes or directories created as a side effect.
- A `--checkpoint` flag, per-tool undo, selective paths, redo, or an undo command outside `chat`.
- Replacing Git or session transcripts as the durable record of work.

## Success criteria

- A turn that modifies, creates and deletes files is fully restored by one `/undo`.
- Repeated `/undo` calls restore earlier turns and then report nothing to undo.
- A read-only turn does not hide the previous edit.
- A post-turn developer edit is not overwritten and the checkpoint survives for retry.
- A sub-agent's file edit is included in the parent `task` turn and restored by one `/undo`.
- A no-op turn produces no undo step.
- Release build, complete suite and recorded evaluation remain green.
