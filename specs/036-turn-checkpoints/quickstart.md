# Quickstart: Turn checkpoints and `/undo`

Checkpoints are automatic in `chat`. No flag turns them on.

```powershell
sharpcoreagent chat --provider demo --workspace . --approval allow
```

## Try it

Create a file with something in it, then ask the agent to change it:

```text
> write notes.txt
✻ completed · ...
> /undo
undo: restored 1 file(s), removed 0 created file(s)
  restored notes.txt
> /exit
```

`notes.txt` is back to the bytes it had before the turn. Running `/undo` again restores the turn before that, and so on
until the process has no history:

```text
undo: nothing to undo; no completed turn in this process changed a file
```

## What is covered

- `write_file`: an existing file is restored; a newly created file is removed.
- `replace_in_file`: the original bytes return.
- `delete_file`: the deleted file is recreated.
- A `task` sub-agent's file edits belong to the parent turn and are undone with it.
- Read-only, plan and review turns do not become undo steps.

## Conflicts

If you edit a file after the turn, `/undo` leaves it alone:

```text
undo: restored 0 file(s), removed 0 created file(s) · 1 file(s) left alone; the checkpoint is kept
  ! notes.txt: changed after the turn ended; it was left untouched
```

The checkpoint is still there. Return the file to the state the agent left it in, or accept it, then run `/undo` again.
Restored files are remembered, so a retry only continues the remaining work.

## Limits

- Snapshots are in memory for this process; a resumed session has no earlier undo history.
- One captured file is capped at 64 MiB. A larger file is refused before the tool changes it.
- Only the three file tools capture. `run_shell`, MCP tools and `dotnet_verify` are not undone.
- Empty directories created by a turn and file metadata are not restored.
- `/undo` is a `chat` command; `run` is one turn and exits.

## Evidence

```powershell
dotnet test tests/SharpCoreAgent.Tests/SharpCoreAgent.Tests.csproj -c Release
```

`TurnCheckpointStoreTests`, `CheckpointToolTests`, the `CodingAgent` undo round trip, the parent/child `task` merge test
and the `chat` `/undo` integration test cover the case list above. See
[`contracts/checkpoints.md`](contracts/checkpoints.md) for the exact output and semantics.
