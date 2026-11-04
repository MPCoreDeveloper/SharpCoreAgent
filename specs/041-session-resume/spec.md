# Feature Specification: `--resume` transcript browser

**Feature**: `041-session-resume`
**Status**: implemented 2026-11-04

## Problem

`--session <path>` opens a transcript, but a developer has to know the timestamped filename first. `003` T032 asked for
`--resume [path]`: with no path it should list the workspace's transcripts, and with a path it should open one. The
listing must not require a model, a credential or an MCP server, because its whole purpose is to help someone choose a
conversation before configuring anything.

## What ships

- `chat --resume` lists saved transcripts in the workspace, newest first.
- `chat --resume <path>` opens that transcript; a relative path is resolved inside the workspace.
- Each row shows update time, message count, provider/model and workspace-relative path.
- Malformed transcripts are listed as `unreadable` rather than aborting the listing.
- A missing resumed path is an error and does not create a new transcript.
- `--resume` is rejected on `run` and on commands that keep no transcript; `--resume` and `--session` cannot both name a
  path.

## User stories

1. **Find yesterday's conversation.** `sharpcoreagent chat --resume` prints the newest sessions with enough context to
   choose one, without asking for a key.
2. **Open the chosen one.** `sharpcoreagent chat --resume .sharpcoreagent/sessions/20261104T120000Z.json` loads it.
3. **Do not silently start over.** A typo in the path prints an error and exits 5 instead of creating a fresh session at
   that path.
4. **Keep working with `--session`.** Existing explicit new-or-resume behavior is unchanged.
5. **Survive a bad file.** One corrupt JSON file is shown as unreadable; the rest of the list still appears.

## Functional requirements

- **FR-001**: `--resume` takes an optional value. A following argument that starts with `-` is parsed as a flag, not a
  path.
- **FR-002**: No value means list `${workspace}/.sharpcoreagent/sessions/*.json`. The branch runs before credential
  prompting and before MCP startup.
- **FR-003**: Rows are ordered by `UpdatedAtUtc` descending, then path; each includes the UTC timestamp with seconds,
  message count, provider/model and relative path.
- **FR-004**: An unreadable or schema-invalid file becomes an `unreadable` row and does not hide the others.
- **FR-005**: A named relative path is resolved against the workspace root; an absolute path is accepted as given.
- **FR-006**: A named path that does not exist returns `ExitCodes.SessionIo` (5) with a message pointing at the listing
  command.
- **FR-007**: `--resume` and `--session` together are a usage error. `--resume` is chat-only.
- **FR-008**: The listing reads through `ISessionStore`, so its validity rules match what opening the transcript will
  enforce.
- **FR-009**: No new tool, process, network call, credential read or transcript write is added.
- **FR-010**: The existing `--session` behavior is preserved: a missing explicitly named `--session` still starts a new
  conversation at that path.

## Non-goals

- Git-based checkpoints. `036-turn-checkpoints` deliberately implements process-local file snapshots, `035-git-change-evidence`
  keeps Git read-only, and adding a mutating Git path is a separate capability with its own safety and recovery contract.
- Cross-process undo, transcript editing, deletion, renaming, search, paging or a picker UI.
- Automatic selection of the newest transcript.
- Listing transcripts from attached read-only roots, or outside the workspace.

## Success criteria

- A fixture transcript is listed with its timestamp, message count, model and relative path.
- Listing works before any credentials are requested.
- A missing resumed path exits 5 and does not create a file.
- `--resume` and `--session` conflict; `run --resume` is rejected.
- Full suite, Release build and recorded evaluation remain green.
