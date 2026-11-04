# Tasks: Turn checkpoints and `/undo`

## Setup

- [x] T001 Add Domain `ITurnCheckpoint` and `ToolInvocationContext.Checkpoint`; update the public API record.
- [x] T002 Add Application `ITurnCheckpointJournal`, `CheckpointUndo` and `CheckpointFailure`.
- [x] T003 Register one `TurnCheckpointStore` as the journal singleton.

## Capture

- [x] T004 Capture exact bytes, absence and link targets before `write_file`, `replace_in_file` and `delete_file`.
- [x] T005 Keep the first capture per path and refuse a mutation when capture fails.
- [x] T006 Open and close one checkpoint around every `CodingAgent.RunTurnAsync`; pass it through the tool context.
- [x] T007 Share the journal with `TaskTool` so a child turn merges into the parent.

## Restore

- [x] T008 Fingerprint each captured path at turn end, drop net no-ops and push a LIFO history entry.
- [x] T009 Restore modified, deleted and created files atomically; recreate links and parent directories.
- [x] T010 Refuse post-turn drift, keep failed checkpoints for retry and remember already-restored paths.
- [x] T011 Implement `/undo` output and the transcript developer note.

## Verification

- [x] T012 Journal tests: bytes/BOM, create/delete, first-capture-wins, LIFO, empty/read-only turns, nested merge,
      drift refusal and retry.
- [x] T013 Tool tests: all three mutating tools capture; a refused capture writes nothing.
- [x] T014 Agent and CLI tests: a real turn is undone; `chat` `/undo` restores a pre-existing file.
- [x] T015 Update README, help, contract, spec, plan and quickstart.
- [x] T016 Run Release build, complete suite and recorded evaluation; record evidence.

## Evidence

- Focused: 8 journal cases, 5 tool cases, 1 `CodingAgent` round trip, 1 parent/child `task` round trip and 1 CLI
  `/undo` conversation. Complete Release suite: **1,601/1,601**, zero warnings.
- Recorded evaluation: 3/3 tasks, 57,493 input / 260 output tokens, no semantic regressions.
- No new process, network, tool or settings surface. The journal is process-local memory.

## Out of scope

Undoing shell/MCP/verifier commands, cross-process persistence, file-system watching, directories, metadata, redo,
selective paths, a checkpoint flag and any undo command outside `chat`.
