# Feature Specification: Stdin image input

**Feature**: `050-stdin-image-input`
**Status**: implemented 2026-11-04

## Problem

`047` attached images by workspace path. A screenshot pasted into a terminal, or produced by another command, has no
path yet. `--image -` reads it from stdin without a temporary file.

## What ships

- `run "<prompt>" --image -` reads one binary image from standard input.
- PNG, JPEG, GIF and WebP are recognised by their signatures, because stdin has no extension.
- The read is bounded at 4 MiB; a larger stream is refused without growing an unbounded buffer.
- `--image -` is accepted once, and only when stdin is redirected.
- The image follows the same model-support, per-turn budget and first-message rules as a path image.

## Functional requirements

- **FR-001**: `-` means stdin and only `run` accepts it.
- **FR-002**: The type is sniffed from bytes: PNG, JPEG, GIF, WebP.
- **FR-003**: The stream is capped at `TextLimits.MaxImageBytes` (4 MiB).
- **FR-004**: A terminal stdin is refused with an instruction to pipe or paste.
- **FR-005**: Two `-` entries are refused; stdin can be read once.
- **FR-006**: A non-image, empty or oversized stream produces one sentence and exit 2.
- **FR-007**: A piped image is not written to the workspace or session file.
- **FR-008**: A path image and a stdin image can appear in the same command.

## Non-goals

- Clipboard APIs, drag-and-drop, or a GUI paste event.
- OCR, PDF, audio or video.
- Reading more than one image from one stdin.

## Success criteria

- A PNG signature on a `MemoryStream` loads as `image/png`.
- Text and oversized streams are refused.
- A full suite, build and recorded evaluation remain green.
