# Contract: Checkpoints and `/undo`

**Feature**: `036-turn-checkpoints`

## Tool seam

```csharp
public interface ITurnCheckpoint : IDisposable
{
    ValueTask<string?> CaptureAsync(
        string relativePath,
        string fullPath,
        CancellationToken cancellationToken = default);
}
```

- `CaptureAsync` is called **after** path / content validation and **before** any mutation.
- `null` means the path is captured or already captured by this turn.
- A non-null message is a failed capture. The tool returns a failed `ToolResult` and writes nothing.
- `Dispose` closes the turn. It is idempotent; a nested turn merges into its parent, a top-level turn is recorded only
  when its net file effect is non-empty.
- `ToolInvocationContext.Checkpoint` is null when the host did not enable a journal; tools retain their old behaviour.

## `/undo`

```text
/undo
```

No arguments. Available only in `chat`; it does not run a model turn.

### Semantics

1. Take the most recent completed checkpoint with a net file change.
2. For each captured path, compare the current fingerprint with the end-of-turn fingerprint. If they differ, leave the
   path untouched and add a failure.
3. Otherwise:
   - pre-state **missing** → delete the path;
   - pre-state **regular file** → write the captured bytes atomically;
   - pre-state **symbolic link** → recreate the link.
4. Record successful paths. If every path succeeds, pop the checkpoint. If any path fails, keep it for a retry.
5. Append a user-role developer note to the transcript when at least one path was restored or removed.

### Output

Clean restore:

```text
undo: restored 1 file(s), removed 2 created file(s)
  restored src/Cart.cs
  removed  src/NewCart.cs
  removed  src/OldCart.cs
```

No history:

```text
undo: nothing to undo; no completed turn in this process changed a file
```

Conflict:

```text
undo: restored 0 file(s), removed 0 created file(s) · 1 file(s) left alone; the checkpoint is kept
  ! src/Cart.cs: changed after the turn ended; it was left untouched
```

Lists stop after twenty entries with `… more files restored`, `… more files removed`, or a failure-count line.

### Invariants

- A read-only, plan, review, failed or empty turn creates no undo step.
- The first capture of a path in a turn is the one restored.
- Repeated `/undo` calls are LIFO across completed turns.
- A file changed after its turn is never overwritten.
- A restore failure is data: the checkpoint remains, the successful files are remembered, and a retry continues.
- `/undo` never runs a shell, network request, MCP tool or new process.
- Only `write_file`, `replace_in_file` and `delete_file` capture. Shell/MCP/verifier changes are outside the contract.
- Snapshots are process-local and files are capped at 64 MiB.
