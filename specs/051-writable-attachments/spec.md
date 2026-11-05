# Feature Specification: Writable attached roots

**Feature**: `051-writable-attachments`
**Status**: implemented 2026-11-04

## Problem

`007` T030 deferred writable attachments. A developer who names an extra root and chooses `--approval allow` has
already granted write authority for that task; forcing every edit through a second flag or a parent `--workspace` made
the agent less useful than the authority the developer gave it.

## What ships

- `--add-root <path>` keeps its meaning: the root is readable in every mode.
- Under `--approval allow`, an absolute path inside an attached root is writable by `write_file`,
  `replace_in_file`, `delete_file`, `run_shell` (working directory), `dotnet_verify` (project) and the runtime
  verifiers (target).
- Under `--approval ask` and `readonly`, the existing read-only refusal is unchanged.
- A relative path still means the primary root; `list_roots` supplies the absolute prefix.

## Functional requirements

- **FR-001**: Only `ApprovalMode.AllowAll` opens the write path; `Ask` and `ReadOnly` do not.
- **FR-002**: A relative path resolves against the primary root, never an attachment.
- **FR-003**: An absolute path inside an attachment is resolved lexically and physically against every root, so a
  link that leaves all roots is refused.
- **FR-004**: `PathSafety.TryResolveWrite` is the single write resolver; `TryResolve` keeps its primary-only
  behaviour and pinned messages.
- **FR-005**: Effects and messages show a path relative to the owning tree.
- **FR-006**: The same refusal sentence (`read-only attachment`, `writes stay in the primary root`) is preserved for
  the modes that still refuse.

## Non-goals

- A separate `--writable` flag.
- Persisting attachments or their writability in settings.
- Indexing attachments (T031).
- Allowing a relative path to search attachments.

## Success criteria

- Under `AllowAll`, `write_file` creates, `replace_in_file` edits and `delete_file` removes a file in an attachment.
- Under `Ask`/`ReadOnly`, the same writes are refused and the file is untouched.
- A relative path still writes only in the primary root.
- Build, full suite and recorded evaluation green.
