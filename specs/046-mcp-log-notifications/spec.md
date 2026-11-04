# Feature Specification: MCP logging notifications

**Feature**: `046-mcp-log-notifications`
**Status**: implemented 2026-11-04

## Problem

An MCP server can report `notifications/message` with a level, logger and structured data. The client previously logged
only the method name, so a verbose developer saw `notification: notifications/message` and not the server's message.

## What ships

- `notifications/message` is parsed into one bounded line:
  `server log [level] logger: message`.
- Missing `level` defaults to `info`; missing `logger` omits the segment; missing `data` is an empty message.
- String data is printed as text; object/array data is printed as compact JSON.
- The line is capped at 500 characters and only reaches the existing verbose log, never the model transcript or stdout.

## Functional requirements

- **FR-001**: Recognise the notification by its exact method name.
- **FR-002**: Read `params.level`, `params.logger` and `params.data` defensively.
- **FR-003**: Bound the rendered message.
- **FR-004**: Keep the generic `notification: <method>` fallback for every other notification.
- **FR-005**: Add no new process, network or tool surface.

## Non-goals

- MCP resources, prompts and sampling (still `009` T034).
- Forwarding server logs to the model or storing them in a transcript.
- Changing stderr handling; stderr remains a separate ring.

## Success criteria

- A logging notification produces the structured line under verbose logging.
- Other notifications keep their existing line.
- Full suite, build and recorded evaluation remain green.
