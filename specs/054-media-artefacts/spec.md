# Feature Specification: PDF, audio and video artefacts

**Feature**: `054-media-artefacts`
**Status**: implemented 2026-11-04

## Problem

`010` T032 limited saved binaries to five image types. A browser server also returns PDFs, audio and video; refusing
them by name left the transcript with a description and no file.

## What ships

- The artefact sink saves `application/pdf`, `video/mp4`, `video/webm`, `audio/mpeg` and `audio/wav` using the same
  size, approval, naming and retention rules as images.
- The extension comes from the MIME table, never from a server-supplied file name.
- The model still gets a path and size, not bytes; only image parts are collected as attachable `ImageContent`.
- Unknown types remain described and unsaved.

## Functional requirements

- **FR-001**: PDF/audio/video resource blobs are offered to the sink.
- **FR-002**: Only the listed MIME types are saved; an unlisted type is described.
- **FR-003**: Per-file, per-call and directory ceilings are unchanged.
- **FR-004**: Approval and retention are unchanged.
- **FR-005**: No media is re-encoded, probed or played.

## Non-goals

- OCR/PDF text extraction.
- Audio transcription or video frames.
- A media viewer.
- Guessing an extension from a file name.

## Success criteria

- A `application/pdf` resource writes a `.pdf`.
- `video/mp4` and `audio/mpeg` write `.mp4` and `.mp3`.
- Unknown MIME types still produce no file.
- Build, suite and recorded evaluation green.
