# Spec — 023 ide surface

**Status**: specification only, written 2026-11-02 beside its `plan.md` and `tasks.md`; no line of `src/` belongs to
this round yet. The first slice it lands as is `ui --serve`, and the two host apps are recorded under "What this round
does not build" with their costs, so the next round inherits an argument instead of a guess.

**Depends on**: `002` (the index, and the JSON object `index --status --json` prints) and `013` (`mcp serve`, the
read-only reader over that index). No new domain type, no new tool, no new setting, no new dependency.

## Why this round exists

The index is the product, and every number in it leaves the process as text on a terminal or as one object a script
parses. `index --status` prints seven lines a person reads once, `index --status --json` prints the same facts as one
object nobody looks at, and neither is what a developer consults while deciding whether the index has gone stale under
a branch switch. The screenshot this round started from is a card of those same numbers, and the numbers in it already
exist — files, passages, symbols, edges, communities, size, state, tier, built, embedder — in `IndexStatus`.

The naive reading of the screenshot is "write an extension". Both IDEs can display a loopback URL in a panel they
already have — VS Code's Simple Browser, Visual Studio's browser window — so a page served by this binary is visible
inside both IDEs with no TypeScript project, no `npm`, no `.vsix`, no signing key and no second language in the
repository. That is the honest ordering too: a page that is *correct* is worth more than a host app that renders a
wrong number beautifully, and the host app can then be built against a page whose contract is already pinned.

The second reason is the constitution's own sentence, from Principle IV: *"A budget enforced only in a UI is not
enforced at all."* The panel of this round is a reader with no number of its own. It does not cache, it does not
estimate, it does not remember what the index said last time, and it writes nothing. Any refresh that walks the tree is
named on the command line and bounded by the flag that already bounds a walk.

## User stories

### User Story 1 — the panel, served by the binary (Priority: P1)

A developer types `sharpcoreagent ui --serve` in a repository that has an index. The command binds `127.0.0.1` on an
ephemeral port and prints two lines on stderr: the URL, and the workspace the page will describe. The developer opens
the URL in a browser, or in VS Code's Simple Browser, and sees one page: state and freshness, the tier and embedder in
force, the built timestamp, files/stale/skipped/orphan rows, passages/symbols/edges/communities, the index size, and a
coverage bar whose two segments are indexed files against files on disk. Every one of those numbers is the one
`index --status --json` prints for the same workspace, and the page says so in a footer naming the command.

**Why this priority**: it is the whole value of the round — the index made legible — and it needs nothing but the
binary, the BCL and the JSON object that already exists.

**Independent Test**: build the document for a status the test constructs and assert the rendered page carries every
number; then pass one open index through `index --status --json` and through the server's document builder and assert
the two objects are byte-identical. Neither assertion opens a socket.

**Acceptance Scenarios**:

1. **Given** a workspace with a fresh index, **When** the developer runs `ui --serve`, **Then** a URL on `127.0.0.1` is
   printed on stderr, the page carries the same numbers as `index --status --json`, and stdout stays empty unless
   `--json` was given.
2. **Given** an indexed workspace and a page already open, **When** the developer reloads it, **Then** the numbers are
   read from the index again — the page holds no cache and emits `Cache-Control: no-store`.
3. **Given** a workspace with no index at all, **When** the page is opened, **Then** it says the index is `missing`,
   prints the one hint `run sharpcoreagent index to build it`, and the command still exits `0` when interrupted: the
   server answered, the workspace simply has nothing to show.
4. **Given** an index touched since it was built, **When** the page is opened without `--refresh`, **Then** `state` reads
   `stale` and the stale count is shown; **with** `--refresh`, **Then** the walk happens first, inside `--index-budget`,
   and the page shows the numbers the walk produced.

---

### User Story 2 — the same page inside an editor (Priority: P2)

The same URL, opened in the editor the developer already has open. VS Code's Simple Browser (`Ctrl+Shift+P` →
`Simple Browser: Show` → the URL) and a Visual Studio browser window both render it, and the page adopts the editor's
colours because its stylesheet answers `prefers-color-scheme` and carries both pairs. The command's note prints both
routes by name, so a developer who has never used Simple Browser does not have to go looking for it.

**Why this priority**: it is what makes the round *an IDE surface* rather than a web page, and it costs a stylesheet and
two sentences. It is second because it is worthless if the numbers are wrong, and it delivers nothing the browser did
not already deliver.

**Independent Test**: assert the rendered page contains both `prefers-color-scheme: dark` and its light counterpart, and
that the note on stderr names `Simple Browser` and the browser window; assert that no stylesheet or script reference
leaves `127.0.0.1` (no CDN, no font URL, no analytics).

**Acceptance Scenarios**:

1. **Given** the server is running, **When** the URL is pasted into VS Code's Simple Browser, **Then** the page renders
   with no external request, and in dark mode its background and text are the dark pair.
2. **Given** the server is running, **When** the note is read, **Then** it names both routes, and neither is opened on
   the developer's behalf.

## What it adds

| Surface | Layer | Shape |
|---|---|---|
| `ui --serve` | `Cli` | one command: binds `127.0.0.1`, answers `GET /` and `GET /api/status.json`, prints the URL and the workspace on stderr |
| `UiServer` | `Cli` | the listener: `HttpListener` on a loopback prefix, two routes, a `Host` check, `no-store`, one request at a time |
| `IndexStatusDocument` | `Cli` | the one builder of the status object — the dictionary and its serialisation — shared by `IndexCommand.StatusAsync` and the server, so the two cannot disagree |
| `UiPage` | `Cli` | the page as a pure function of the document: `Render(document, workspace)` returns the HTML string; embedded stylesheet, one fetch, no other origin |
| exit `2` on an unbindable port | `Cli` | `--port` outside `0–65535`, or an explicit port already held; nothing is bound and nothing is served |

## Requirements

- **FR-001** `sharpcoreagent ui --serve` MUST bind `http://127.0.0.1:<port>/` on an ephemeral port when `--port` is
  absent (`--port 0` says the same thing explicitly), print the resolved URL to stderr as its first line, and serve
  until the process is interrupted. Ctrl+C MUST stop the listener and exit `0`, the way `mcp serve` ends.
- **FR-002** `GET /` MUST answer with the page, `Content-Type: text/html; charset=utf-8` and `Cache-Control: no-store`.
  `GET /api/status.json` MUST answer with the same object `index --status --json` prints,
  `Content-Type: application/json; charset=utf-8` and `Cache-Control: no-store`.
- **FR-003** Both routes MUST be derived from `IndexStatusDocument`, which MUST also be what `IndexCommand.StatusAsync`
  uses. A field added to one and not the other is the bug this requirement exists to make impossible, and a test
  compares the two renderings rather than either one alone.
- **FR-004** The server MUST answer only `GET`. Any other method, any other path, and any request whose `Host` header is
  not the bound host and port MUST be answered `404` or `403` with an empty body that carries no number — a loopback
  listener that answers any `Host` is reachable from a hostile page by DNS rebinding, and this is the line that closes
  that door.
- **FR-005** The command MUST NOT `Process.Start` anything. It prints the URL; opening it is the developer's action.
  Principle III names two exceptions and this is not a third.
- **FR-006** The status MUST be read through the same open index the other commands use (`SharpCoreAgentIndex`), in
  process. Running `sharpcoreagent index --status --json` as a child process to obtain the object is a `Process.Start`,
  refused by FR-005 as well as being slower.
- **FR-007** The default MUST be `--status` semantics: reading the index never writes, and a page load never walks the
  tree. `--refresh` opts into one walk before each answer, bounded by `--index-budget` (default `3000` ms, the value
  `run` and `chat` already use). Absent the flag, the safe behaviour is the one that happens.
- **FR-008** `--json` MUST print one object on stdout before the first request is answered —
  `{"command":"ui","action":"serve","url":"http://127.0.0.1:<port>/","workspace":"<slash-normalised path>","refresh":<bool>}`
  — so a script can start the panel and learn where it is without parsing a sentence. Everything else stays on stderr.
- **FR-009** The page MUST carry every field of the status document a developer reads — `state`, `tier`, `embedder`,
  `builtUtc`, `files`, `stale`, `skipped`, `orphans`, `passages`, `symbols`, `edges`, `communities`, `sizeBytes` — and
  MUST NOT display a number the document does not carry. A `missing` index renders the state and the one hint rather
  than a card of zeroes, because zeroes are a lie about a workspace that was never indexed.
- **FR-010** The page MUST be self-contained: one stylesheet inside the document or served from the same origin, one
  fetch to `/api/status.json` on load and one on a button, and no request to any other origin — no CDN, no web font, no
  favicon from elsewhere.
- **FR-011** The page MUST render in the editor's colour scheme: `prefers-color-scheme: dark` and its light counterpart
  both defined, contrast at or above 4.5:1 for body text, and no colour that assumes a white background.
- **FR-012** A port the command cannot bind MUST end the command at exit `2`, with the port in the message and `--port`
  named as the fix, before any page is served. A `--port` outside `0–65535` and a non-numeric `--port` are the same
  failure.
- **FR-013** The command MUST refuse at exit `2` the flags that describe a turn or a client: `--model`, `--provider`,
  `--planner`, `--executor`, `--reviewer`, `--session`, `--approval`, `--client`, `--write`, `--mcp`, `--web`. It opens no
  model, no transcript and no connection beyond the listener it was asked for.

## Edge cases

- **No index.** `state` is `missing`; the page prints the state and `run sharpcoreagent index to build it`, and no card.
- **Index locked by another process.** The read fails; the page answers with the failure the other commands print for
  that condition, and the server keeps serving, because a failed page is not a failed process.
- **Two panels on one workspace.** A second `--serve` on an ephemeral port is fine; an explicit `--port` already held is
  FR-012's refusal.
- **An index mid-rebuild.** `incomplete` is `true`; the page says `partial` and keeps showing the last complete build's
  numbers, which is what `--status` already reports.
- **A workspace path with spaces or a non-ASCII directory.** It appears in the page's footer HTML-escaped, and in
  `--json` slash-normalised, as `index --status --json` already does it.
- **A very large index.** `sizeBytes` is formatted the way `--status` formats it (`14.2 MiB`), not as a raw count, and
  the numbers are grouped with the invariant-culture separators the CLI already uses.

## What this round does not build

- **A VS Code extension.** `createWebviewPanel`/`WebviewViewProvider` need a TypeScript project, a `package.json`,
  `npm`, a bundler and a `.vsix` — a second toolchain in a repository with one language and no JavaScript anywhere. The
  page this round serves is exactly what such a webview would load, so the extension is the next round's work against a
  pinned contract.
- **A Visual Studio 2026 tool window.** `ToolWindowPane` with in-process WebView2, or an out-of-process
  `IRemoteUserControl`, needs C#, XAML, a `.vsixmanifest` and a signing key. The remote-user-control path is the
  interesting one — it is a UI server, the same shape as this round's listener — and it is a separate spec with its own
  authentication story, because a remote-UI channel calls back into the IDE rather than answering a page.
- **Auto-opening a browser.** Principle III names two `Process.Start` exceptions and this is not a third. The URL is
  printed; the developer opens it. If that is ever judged too much friction it is a constitution amendment with a
  written reason, not a convenience flag.
- **A polling page.** A timer that re-reads the index every second would open the database on a schedule the developer
  never asked for, and would quietly change "the index as it is" into "the index as of a second ago". The button and the
  reload key are the refresh.
- **Any write, any tool, any setting.** No `ui` section in the settings file, no environment variable, no `ITool`
  registered, nothing publishable over `mcp serve`. A panel that could act would be a second door to the machine.
- **A LAN-reachable panel.** `--host` does not exist. Binding `0.0.0.0` would put a developer's file names, symbol
  counts and workspace path on whatever network the laptop is on, and nothing this round read says anybody asked for it.

## What makes this round done

- `IndexStatusDocumentTests`: the object carries every key `index --status --json` carries, in the same order, and a
  status with `BuiltUtc` null prints `null` rather than a fabricated timestamp.
- `UiPageTests`: the page carries every field in FR-009; a `missing` index renders the hint and no card; both
  `prefers-color-scheme` blocks exist; no `http://` or `https://` reference other than the panel's own origin.
- `UiCommandParsingTests`: `--serve` required; `--port` range and spelling; `--refresh` off by default; the `--json`
  shape; each of FR-013's eleven flags refused; `--help` lists the command.
- `UiServerTests`: one loopback round trip on an ephemeral port — `GET /`, `GET /api/status.json`, `POST /` refused, a
  foreign `Host` refused, `Cache-Control: no-store` present — skipped rather than failed where the platform refuses the
  prefix, and leaving the rest of the suite unaffected.
- `dotnet build -c Release` at 0 warnings, 0 errors; `dotnet test -c Release --no-build` green, with the round's own
  delta recorded in the commit that carries it.

