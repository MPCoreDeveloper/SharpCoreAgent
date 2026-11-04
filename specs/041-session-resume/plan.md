# Implementation Plan: `--resume` transcript browser

**Feature**: `041-session-resume`
**Status**: implemented 2026-11-04

## Parsing

`CliOptions` gains `Resume` and `ResumePath`. The parser consumes a following non-flag token as the path; otherwise the
request is a listing. `--session` still produces `SessionPath`, and the chat case rejects both names together. `run`
rejects `--resume`; the no-transcript commands include it in their existing `RejectModelFlags` check.

## Listing before startup

`ChatCommand.ExecuteAsync` builds the container (which does not resolve a model or a key), obtains `ISessionStore` and
checks `Resume && ResumePath is null` before `TryEnsureCredentials` or `TryStartMcpAsync`. The listing therefore works
offline and with no provider key.

The session directory is `${workspace}/.sharpcoreagent/sessions`. Files are read through `ISessionStore.LoadAsync`, which
applies the same version, role and tool-message validation as opening. A `SessionFormatException`, `IOException` or
`UnauthorizedAccessException` marks that row `unreadable`; the rest still list. The output is plain text so it works when
the answer stream is redirected.

## Opening

A named `--resume` path is resolved against the workspace when relative. It is checked for existence before the
credential prompt. `OpenAsync` treats a missing path as "new conversation" only when neither `--session` nor
`--resume` named a path, preserving the old `--session` behavior. A path named by `--resume` must exist; otherwise the
command exits 5.

## Bounds and safety

- Listing reads only `*.json` directly under the workspace's session directory. `.tmp` files are not matched.
- A corrupt file cannot stop the listing.
- No transcript is written by the listing branch.
- No model, process or network call is reached before the listing returns.

## Why Git checkpoints are not here

The deferred `003` line also names Git-based checkpoints. `036` chose exact in-memory file snapshots because it works in
a workspace without Git and does not mutate repository state; `035` intentionally made all Git access read-only.
Replacing or supplementing that journal with Git would add mutating Git commands, index/object writes and new recovery
semantics. That is not a listing feature and cannot ride in this slice without violating both contracts. It stays an
explicit non-goal with its rationale recorded rather than being quietly attempted.
