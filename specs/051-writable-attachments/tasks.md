# Tasks: Writable attached roots

- [x] T001 Add `PathSafety.TryResolveWrite` gated on `AllowAll`.
- [x] T002 Wire `write_file`, `replace_in_file`, `delete_file`, shell, dotnet verifier and runtime verifiers.
- [x] T003 Report paths relative to the owning tree.
- [x] T004 Tests for write/edit/delete and the `Ask` refusal.
- [x] T005 Update `007` T030, spec, help and README.
- [x] T006 Build, full suite and recorded evaluation.

## Evidence

`WriteFile_WritesAnAttachmentOnlyWhenTheSessionAllowsAll` and
`TheWritePathAllowsAnAttachmentOnlyUnderAllowAll`.

## Out of scope

A separate `--writable` flag, persisted attachments, attachment indexing, relative attachment paths.
