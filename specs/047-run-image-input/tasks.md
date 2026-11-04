# Tasks: Image input on `run`

- [x] T001 Add `CliOptions.Images` and repeated `--image`; refuse it on `chat`.
- [x] T002 Add `ImageFile.TryLoad` over the existing workspace and image rules.
- [x] T003 Add the `RunCommand` load, text-only and total-budget checks.
- [x] T004 Add the `CodingAgent` image overload and `TurnState.SeedImages`.
- [x] T005 Help, README and `017` T030 note.
- [x] T006 Tests: loader, parser, first message and text-only refusal.
- [x] T007 Release build, full suite and recorded evaluation.

## Evidence

- `APngInsideTheWorkspaceBecomesAnAttachableImage`.
- `AnUnsupportedExtensionIsExplained`, `APathOutsideTheWorkspaceIsRefused`,
  `AnImageOverThePerFileCeilingIsRefused`.
- `TheImageFlagIsAcceptedOnlyForRun`.
- `APromptImageArrivesOnTheFirstUserMessage` and `AnImageWithATextOnlyModelFailsBeforeARequest`.

## Out of scope

Pasted/dragged/piped images, OCR, resizing, images on `chat`, image persistence in sessions.
