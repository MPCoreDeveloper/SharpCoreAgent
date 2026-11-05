# Feature Specification: `fetch_url` paging

**Feature**: `055-fetch-paging`
**Status**: implemented 2026-11-04

## What ships

`fetch_url` gains optional `start_index` (0–1,000,000, default 0). The response is the `max_chars` window starting at
that offset, followed by `[window: kept characters A..B of N]` when the window is not the whole page. The original
first-window truncation sentence is unchanged.

## Functional requirements

- **FR-001**: `start_index` defaults to 0.
- **FR-002**: The window starts at the byte/character offset and is bounded by `max_chars`.
- **FR-003**: A non-first or shortened window says its 1-based range and the total.
- **FR-004**: An offset beyond the text returns an empty window with the range note.
- **FR-005**: The existing byte ceiling, HTML reduction and header block are unchanged.

## Non-goals

- Fetching a second HTTP range or resuming a response.
- Changing `max_chars`.

## Success criteria

- A 10-character page with `start_index: 4`, `max_chars: 3` returns `efg` and `5..7 of 10`.
- The old truncation test remains byte-identical.
- Full suite, build and evaluation green.
