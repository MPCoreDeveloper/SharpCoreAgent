# Feature Specification: Agent web UI

**Feature**: `060-agent-web-ui`
**Status**: implemented 2026-11-07

## What ships

- `sharpcoreagent serve [options]` — chat in a browser: the same workspace, model, tools and transcript
  `chat` uses, served on a loopback page. It keeps `chat`'s flags and adds `--port` (shared with `ui`).
  It refuses `--serve` and `--refresh` (the read-only panel's own flags), `--image` (pictures arrive per
  turn over the wire) and `--resume` (it starts a fresh conversation).
- A loopback `HttpListener` with four JSON endpoints and a server-sent event stream:
  - `GET /` — one self-contained page (inline stylesheet, one script, no other origin).
  - `GET /api/events` — `text/event-stream`, one frame per `AgentEvent`, carrying the same JSON lines
    `ConsoleObserver --json` prints.
  - `GET /api/state` — `{model, approval, mode, tokensSpent, ceiling}`.
  - `POST /api/turn` — `{prompt}`; 202 accepted, 409 while a turn is running, 400 on an empty prompt.
  - `POST /api/approval` — `{id, decision}`; completes the `IToolApprover` question the page shows.
  - `POST /api/settings` — `{approval}` or `{mode}`; switches the conversation's approval mode or plan/act.
- `WebObserver : IAgentObserver` fans events to the page through `ServeHub` (a bounded, drop-oldest
  channel per connection); `WebApprover : IToolApprover` bridges the destructive question to the page and
  denies on cancel or disconnect.

## Requirements

- **FR-001**: `serve` parses as its own command, keeps the flags `run`/`chat` keep, and refuses the panel's
  two flags and the picture flag with a message naming the command the flag belongs to.
- **FR-002**: The listener binds `127.0.0.1` and only `127.0.0.1`; a request whose `Host` is not
  `127.0.0.1:<port>` is refused `403` with an empty body; any other path or method is `404`; every answer
  is `no-store`.
- **FR-003**: The event stream is server-sent events, not a poll, and a page that stops reading drops the
  oldest frames rather than stalling the turn.
- **FR-004**: One turn runs at a time; a second `POST /api/turn` while one is in flight is `409`.
- **FR-005**: A destructive tool is approved from the page (once, this tool, every tool, or no); a cancel
  or a disconnect denies. The scope words match `ConsoleApprover`.
- **FR-006**: No `Process.Start`, no browser auto-launch, no `--host`, no new dependency — `HttpListener`,
  `System.Threading.Channels` and `System.Text.Json` only.
- **FR-007**: The page renders every event shape as text (never as markup), so a tool that returns HTML is
  shown rather than obeyed.

## Non-goals

- A VS Code extension or a Visual Studio tool window (a TypeScript project, `npm` and a `.vsix`).
- Per-turn picture upload; resuming an archived transcript (`--resume` stays with `chat`).
- WebSockets, authentication, or publishing the listener beyond loopback.

## Evidence

- `ServeCommandParsingTests` (8), `ServeEventsTests` (5), `WebApproverTests` (12), `ServeServerTests` (2):
  twenty-seven new cases in four classes.
- `ServeServerTests` opens a real loopback socket and proves `GET /`, `GET /api/state`, `POST /api/turn`,
  `POST /api/settings`, the `403`/`404` refusals and a live `GET /api/events` round trip; it skips rather
  than fails where the platform refuses the prefix, as `UiServerTests` does.
- Release build of the whole solution: 0 warnings, 0 errors. Full suite 1,899 total, 1,897 succeeded, 1
  skipped (pre-existing latency harness); the one failure this run
  (`RunShellCommandToolTests.AFinishedBackgroundCommandReportsItsExitCodeAndStopIsIdempotent`) is a
  pre-existing background-command race that passes on rerun and is unrelated to this slice.
