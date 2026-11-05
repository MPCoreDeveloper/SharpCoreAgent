# Feature Specification: Artefact directory retention

**Feature**: `049-artefact-retention`
**Status**: implemented 2026-11-04

## Problem

`010` T033 left the artefacts directory unbounded: a long browser session can fill a disk with screenshots. The
decision is a simple cap, not a catalogue.

## What ships

- `McpClientOptions.MaxArtefactDirectoryBytes`, default **256 MiB**, valid 1 MiB–4 GiB.
- Before a new artefact is written, the sink measures the files in `.sharpcoreagent/artifacts/` and deletes the oldest
  until the incoming file fits.
- No deduplication, no archive, no second copy and no deletion outside that dedicated folder.
- If the oldest files cannot be removed, the new artefact is refused with one sentence naming the measured total, the
  incoming size and the ceiling; the call still succeeds and the server's text is preserved.

## Functional requirements

- **FR-001**: The default cap is 256 MiB.
- **FR-002**: The cap applies to files in the dedicated artefacts folder only.
- **FR-003**: Files are ordered by last write time, then name, oldest first.
- **FR-004**: Deletion continues until the incoming file fits or no more files can be removed.
- **FR-005**: A locked or unreadable file is skipped; the refusal is used only when room still cannot be made.
- **FR-006**: The refusal is a sentence on the artefact entry, never an exception or a tool error.
- **FR-007**: A missing folder needs no pruning.
- **FR-008**: No deduplication or content hashing is added.

## Non-goals

- A user-facing `artifacts` command or settings key.
- Deduplication.
- Deleting files outside `.sharpcoreagent/artifacts/`.
- A transcript rewrite after an old screenshot is removed. The transcript keeps the path it recorded; the file may age
  out, which is the meaning of retention.

## Success criteria

- With a 1 MiB test cap, two 600 KiB files and a new image, the oldest is deleted, the newer stays and the new file is
  written.
- The ordinary suite remains green.
