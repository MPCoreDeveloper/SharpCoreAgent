# Tasks: Artefact retention

- [x] T001 Add `MaxArtefactDirectoryBytes` default 256 MiB, validated 1 MiB–4 GiB.
- [x] T002 Add `McpArtefactSink.EnsureRoom` with oldest-first deletion and no dedup.
- [x] T003 Refuse with a sentence when room cannot be made.
- [x] T004 Test oldest deletion under a 1 MiB cap.
- [x] T005 Update `010` T033, the spec and the roadmap.
- [x] T006 Build, full suite and recorded evaluation.

## Evidence

`TheOldestArtefactsAreDeletedWhenTheFolderWouldExceedItsCeiling`: 600 KiB + 600 KiB under a 1 MiB cap; the oldest file is
removed, the newer file stays, and the new screenshot is written.

## Out of scope

Dedup, archive, a settings key, an `artifacts` command, deletion outside the artefacts folder.
