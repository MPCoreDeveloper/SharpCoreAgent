# Tasks: MCP logging notifications

- [x] T001 Parse `notifications/message` level/logger/data defensively.
- [x] T002 Bound the rendered message and keep it on the verbose log only.
- [x] T003 Test the structured line alongside an outstanding request.
- [x] T004 Build, full suite and recorded evaluation.

## Evidence

`ALoggingNotificationIsRenderedAsAServerLogLine` sends
`{"level":"warning","logger":"test","data":"careful"}` and asserts `server log [warning] test: careful`.

## Out of scope

Resources, prompts, sampling, transcript logging and remote transports.
