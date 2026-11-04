# Feature Specification: Image input on `run`

**Feature**: `047-run-image-input`
**Status**: implemented 2026-11-04

## Problem

`view_image` lets the model ask for a picture after a turn starts, but the developer cannot put a picture beside the
first instruction. `017` T030 names an `--image` flag on `run` for exactly that case.

## What ships

- `run "<prompt>" --image <path>`, repeatable.
- Paths resolve through the same workspace and read-only attachment rules as the tools.
- Extensions map to the same four provider media types: PNG, JPEG, GIF, WebP.
- Each file is capped at 4 MiB and the prompt's total is checked against `MaxTurnImageBytes`.
- A text-only model is refused before any request, with a message that says why.
- The pictures travel on the first user message; the turn's tool budget treats them as already spent and already shown.

## Functional requirements

- **FR-001**: `--image` is accepted on `run` only; `chat` refuses it with the run form in the hint.
- **FR-002**: The path must resolve inside the workspace or a read-only attachment.
- **FR-003**: The extension decides the media type; an unsupported extension is refused with the list.
- **FR-004**: A file over 4 MiB is refused before it is read into the request.
- **FR-005**: A model without `SupportsImages` is refused before the transcript is changed.
- **FR-006**: The images are sent on the first user message.
- **FR-007**: `SeedImages` adds their bytes to the turn's picture budget and adds them to the already-shown set.
- **FR-008**: The prompt remains required; an image does not replace the instruction.
- **FR-009**: The files are not persisted in the session file, matching the existing no-images-in-transcripts rule.

## Non-goals

- Pasted, dragged or piped images.
- OCR, resizing or re-encoding.
- Images on `chat`, resumed sessions or `mcp serve`.

## Success criteria

- A valid PNG appears on `model.Requests[0]` on the user message.
- A text-only model fails with no request.
- Unsupported extension, outside path and oversized file are all refused.
- Full suite, build and recorded evaluation remain green.
