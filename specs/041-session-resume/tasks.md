# Tasks: `--resume` transcript browser

## Parsing

- [x] T001 Add `Resume` and `ResumePath` to `CliOptions`; parse an optional non-flag path.
- [x] T002 Reject `--resume` with `--session`, on `run`, and on commands that keep no transcript.
- [x] T003 Document `--resume [path]` in `HelpText`.

## Listing

- [x] T004 List `*.json` transcripts before credential prompting or MCP startup.
- [x] T005 Sort by update time then path; print timestamp, message count, provider/model and relative path.
- [x] T006 Treat malformed/unreadable files as `unreadable` rows.
- [x] T007 Print a clear empty-directory message.

## Opening

- [x] T008 Resolve a relative resume path against the workspace root.
- [x] T009 Fail with exit code 5 when the named transcript is missing; do not create it.
- [x] T010 Preserve `--session`'s existing new-or-resume behavior.

## Verification

- [x] T011 Parser tests: list request, named path, conflict and chat-only refusal.
- [x] T012 CLI tests: fixture transcript listing and missing-path failure without credentials.
- [x] T013 Release build zero warnings; complete suite **1,632/1,632**.
- [x] T014 Recorded evaluation 3/3 at 61,573 input / 249 output; no semantic regression.

## Evidence

- `ResumeWithoutAPathListsTheWorkspaceTranscripts` writes a real `SessionSnapshot` through `JsonFileSessionStore`, runs
  the real entry point and asserts the listing without a prompt.
- `ResumingAMissingTranscriptFailsBeforeACredentialIsNeeded` uses `--provider deepseek` with no key and still reaches the
  missing-session error, proving the ordering.
- Full test count rose from 1,626 to 1,632 (4 parser cases and 2 CLI cases).

## Out of scope

Git-based or persistent checkpoints, cross-process undo, transcript editing/deletion/search/paging, attached-root
transcripts, automatic newest-session selection and a picker UI.
