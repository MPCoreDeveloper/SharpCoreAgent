# Implementation Plan: MCP logging notifications

`JsonRpcPipe.Dispatch` already separated a server request from a notification. The notification branch now checks for
`notifications/message` and calls `ServerLog`, which reads the three optional fields, serializes non-string data, bounds
the result to 500 characters and sends it to the existing `_log` callback.

No new port or state is involved; `McpServerConnection` already passes its verbose log callback into the pipe. The test
uses the in-memory pipe and asserts the rendered line while a normal request is outstanding, so the notification cannot
disturb correlation.

## Limits

This closes only the logging part of `009` T034. Resources, prompts and sampling remain deferred because each needs a
new model- or user-facing surface rather than a log line.
