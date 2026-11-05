# Tasks: Stdin image input

- [x] T001 Add `ImageFile.TryLoadStdin` with signature sniffing and a bounded read.
- [x] T002 Handle `-` in `RunCommand`; refuse terminal stdin and a second `-`.
- [x] T003 Test PNG, unknown bytes and oversized stdin.
- [x] T004 Update help, README, `047` and `017` T030.
- [x] T005 Build, full suite and recorded evaluation.

## Evidence

`StdinWithAPngSignatureBecomesAnImage`, `StdinWithNoKnownSignatureIsRefused` and
`StdinOverThePerFileCeilingIsRefused`.

## Out of scope

Clipboard APIs, drag-and-drop, multiple stdin images, OCR/PDF/audio/video.
