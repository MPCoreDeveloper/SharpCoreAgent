# Feature Specification: Artefact catalogue

**Feature**: `053-artifacts-tool`
**Status**: implemented 2026-11-04

## Problem

`010` T034 left the saved artefact directory without a discovery surface. The path is in the tool result that saved it,
but a later turn or a different message cannot list what is there. The folder is deliberately outside the code index and
outside `list_directory`.

## What ships

- A read-only `list_artifacts` tool, available in every session.
- Optional `limit` from 1 to 500, default 100.
- Newest first, with workspace-relative path, human-readable size and UTC timestamp.
- No directory creation, no writes, no index changes.
- Symbolic links are skipped, so a link cannot turn the catalogue into a read of something outside the folder.

## Functional requirements

- **FR-001**: The tool is read-only and takes only `limit`.
- **FR-002**: A missing folder returns `artifacts: 0` and a sentence, not a failure.
- **FR-003**: Files are ordered by `LastWriteTimeUtc` descending.
- **FR-004**: The answer says how many exist when the limit hides some.
- **FR-005**: A result carries a `Read` effect for `.sharpcoreagent/artifacts`.
- **FR-006**: A directory read failure is a tool failure with the reason.
- **FR-007**: The folder remains outside the index and ordinary file listing.

## Non-goals

- Deleting or opening artefacts from this tool.
- Deduplication.
- A `/files` filter or index visibility.
- Changing the 256 MiB retention policy.

## Success criteria

- A missing folder is an empty catalogue.
- Two files with different timestamps list newest first; `limit: 1` hides the older one and says so.
- Full suite, build and recorded evaluation remain green.
