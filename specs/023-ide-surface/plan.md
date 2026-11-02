# Plan — 023 ide surface

The spec is the argument. This is the shape it took, the facts it rests on, the choices that were weighed, and what the
build has to produce for the round to be done.

## The shape, in one paragraph

`CliCommand.Ui` joins the enum and `CliOptions.FinishUi` accepts exactly one verb, `serve`, with `--port`, `--refresh`,
`--index-budget` and `--json`; every flag that describes a turn or a client is refused there, the way `mcp serve` refuses
a model. `UiCommand.ExecuteAsync` opens the workspace and the index the other commands open, builds the status object
through the new `IndexStatusDocument`, renders the page through `UiPage.Render`, and hands both to `UiServer`, which owns
an `HttpListener` on `http://127.0.0.1:<port>/` and answers two `GET` routes and a `404` for everything else. Nothing in
`IndexCommand` changes except the four lines that used to build the dictionary, which become a call into
`IndexStatusDocument` — so `index --status --json` and `/api/status.json` are one object by construction rather than by
agreement.

## The facts this round is built on

Read from this machine and this repository on 2026-11-02, and quoted because the round turns on them:

- The status object already exists, field for field, in `src/SharpCoreAgent.Cli/Commands/IndexCommand.cs:282-304`:
  `command, action, workspace, state, tier, embedder, dimensions, files, stale, skipped, orphans, passages, symbols,
  edges, communities, sizeBytes, vectorSearch, builtUtc, incomplete, notes`. Its contract is
  `specs/002-code-evidence-index/contracts/cli-index.md:163`, which pins the same list as one line of JSON. The page
  therefore invents nothing: `IndexStatus` carries every number the screenshot shows.
- `System.Net.HttpListener.dll` is present in the `net11.0` reference pack
  (`C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\11.0.0-rc.3.26522.148\ref\net11.0\`) **and** in the shared
  runtime the build targets (`C:\Program Files\dotnet\shared\Microsoft.NETCore.App\11.0.0-rc.3.26522.148\`). So
  `HttpListener` is framework, not a NuGet package, and the constitution's dependency clause is never engaged. The same
  check passes for `System.Net.Sockets.dll`, which is the fallback if the delegation below fails.
- `CliOptions` already has the shape this command needs: a verb list (`FinishMcp`, `CliOptions.cs:965-999`), a
  refuse-the-wrong-flags helper (`RejectModelFlags`, `CliOptions.cs:491-508`), and a `--json` writer every command
  shares. `ExitCodes` has no spare number for "the port is busy" and does not need one (D9 below).

What this round did **not** verify on paper, and then did verify by probe:

- Whether a non-elevated Windows 11 user can bind `http://127.0.0.1:<port>/` without a URL ACL reservation. `http.sys`
  has historically reserved `localhost` for the interactive user and answered `HttpListenerException` 5 (access denied)
  for an explicit `127.0.0.1` prefix in some configurations. **T003/T004 measured it on 2026-11-02, non-elevated
  (`elevated = False`, Windows NT 10.0.26300.0), and the answer is yes.** The probe and its results:
  - `http://127.0.0.1:51987/` → **binds**. So the prefix stays `127.0.0.1` and D5's `localhost` fallback is not used.
  - `http://localhost:51988/` → binds as well, which is why the unused fallback was plausible; it is not needed.
  - `http://127.0.0.1:0/` → **fails with `HttpListenerException` code 87** (ERROR_INVALID_PARAMETER, "The parameter is
    incorrect"). `0` is not a legal prefix: `http.sys` wants a real port. So `--port 0` does not mean "let the listener
    choose" — it means "choose first, then bind", and the port is picked with a `TcpListener` on `127.0.0.1:0` whose
    `LocalEndpoint.Port` is read and released before the prefix is built. Measured working twice (58438, 63008).
  - `GET /` arrives with `Host: 127.0.0.1:<port>` and answers `200` with `Content-Type: text/html; charset=utf-8` and
    `Cache-Control: no-store`; a request with `Host: evil.example` arrives with that header and is distinguishable, so
    FR-004's check is one string comparison; a `POST` arrives with `HttpMethod = "POST"` visible before any body is read.
  - A second listener on the same prefix → **code 183** ("conflicts with an existing registration"); a prefix over a port
    a plain socket holds → **code 32**. Both are the "already held" case FR-012 words, and both are caught, not fatal.
  - `http://127.0.0.1:80/` → **code 5, access denied** even though 80 needs no URL ACL for `http.sys`: a privileged port
    is refused to a normal user, so the range check accepts `0–65535` and the bind decides. `65535` binds.

- Whether Visual Studio 2026 docks a browser window on a loopback URL, and whether VS Code's Simple Browser renders
  `prefers-color-scheme` from the editor rather than from the OS. Neither changes a line of this round: the page is a
  plain web page and the note only says which menu opens it. Both are written down so the extension round does not read
  this one as evidence.

## Decisions

**D1 — a command, not an MCP surface.** `ui` is a sibling of `index` and `models`, not a verb under `mcp`. It publishes
no tool, appears in no `tools/list`, and is not reachable from an agent: it answers a human at a console. Putting it
under `mcp` would have suggested it is part of the served surface, and Principle II's allow-list is a careful thing that
should not accumulate a web server.

**D2 — loopback only, and no flag to change it.** The prefix is `http://127.0.0.1:<port>/`. There is no `--host`, and
`0.0.0.0` is not reachable by any spelling. The page carries a workspace path, file counts and symbol counts; those are
not secrets in the Principle VIII sense, but they are a map of a private repository and nothing this round read says
anybody asked to publish it.

**D3 — the safe default is `--status`, and `--refresh` is the opt-in.** `mcp serve` refreshes before each call, because
its caller is an agent that asked a question in this process. A page reload is not that: the developer pressed F5 on
something whose whole contract is "the index as it is". So `ui` reads like `index --status` — never writes, never walks —
until `--refresh` says otherwise, and then the walk is bounded by `--index-budget`, the number `run` and `chat` already
honour. The order matters: the constitution wants the safe behaviour to be the one that happens when the flag is absent.

**D4 — one document, two consumers.** `IndexStatusDocument.Build(workspace, status)` returns the dictionary and
`Serialize(document)` returns the bytes; `IndexCommand.StatusAsync` calls them instead of building its own anonymous
dictionary, and `UiServer` calls them for `/api/status.json`. The alternative — the page re-declaring the fields it
cares about — is how a panel and a CLI drift apart one field at a time, with each half's tests passing. The test that
matters compares both renderings of one index, not either one alone.

The document owns the *formatters* as well as the fields, or the drift simply moves: `State`, `TierName`, `Slash`,
`Number` and `Size` are privates of `IndexCommand` today (`IndexCommand.cs:660-751`, with `State` already `internal`
and called from `McpBench.cs:196` and `McpCommand.cs:154`), so they move into `IndexStatusDocument` and those two call
sites follow. A page that rendered `index.Tier` as `"Rich"` where the CLI prints `rich` would be a one-character
disagreement no field-set test would catch. `Serialize` is `JsonSerializer.Serialize(document)` with no options —
exactly what `WriteJson` does at `IndexCommand.cs:758-761` — which is what makes the byte-equality assertion honest
rather than approximate.

**D5 — `HttpListener`, with a probe and two named fallbacks.** Options weighed: (a) `TcpListener` plus a hand-written
HTTP/1.1 responder — no protocol risk if it is only two `GET` routes, but it re-implements request parsing, header
folding, `Content-Length` and connection teardown, and the one thing worse than a dependency here is a home-made parser
answering bytes; (b) `HttpListener` — framework, verified present in the pack and the shared runtime, correct HTTP, and
on Windows it is `http.sys`, which is a component the operating system already runs; (c) Kestrel — an ASP.NET Core
dependency for two routes, rejected on size. (b) wins, with two fallbacks recorded rather than improvised: if the
`127.0.0.1` prefix raises `HttpListenerException` 5, retry once as `http://localhost:<port>/` and print which prefix
answered; if both are refused, the round stops and the fallback is (a), which the pack also carries. **T003 settled it:
the first prefix binds non-elevated, so neither fallback is used, and both stay recorded in case an environment that
reserves the prefix turns up.** The probe also turned up one thing this decision had wrong: `--port 0` cannot be passed
to `http.sys` at all (code 87), so an ephemeral port is *chosen* — `TcpListener` on `127.0.0.1:0`, read
`LocalEndpoint.Port`, release it, then build the prefix — and the race that opens between the two steps is why
`--port 0` retries a handful of times before it gives up. An explicit `--port` never retries: a port someone named and
that is busy is a mistake to report, not a number to second-guess.

**D6 — the page is a string.** `UiPage.Render(document)` returns `string` — one argument, the document, so the page
cannot print a number the JSON does not carry; the stylesheet lives in it, the one script lives in it, and encoding goes
through `System.Net.WebUtility.HtmlEncode`. Rejected: Razor (an ASP.NET Core
dependency and a build step), a templating package (a dependency for one page), and embedded resources read through a
manifest stream (more moving parts to test than the page has fields). A pure function of the document is also the only
shape that makes FR-009 assertable without a socket.

**D7 — no auto-open.** Principle III lists two `Process.Start` exceptions and a browser launch satisfies neither: it is
not a process the developer named on the command line, and it does not speak a documented protocol over a pipe. The URL
is printed; the note names Simple Browser and the Visual Studio browser window, and opening either is the developer's
action.

**D8 — a `Host` check and `no-store`, because a loopback listener is not private.** Any process on the machine can reach
`127.0.0.1`, and a page in any browser can reach it by DNS rebinding if the server answers whatever `Host` it is sent.
The check is one string comparison against the bound prefix; the answer is `403` with an empty body. `no-store` is the
other half: a cached page would show numbers that are no longer the index's, which is the failure this round exists to
end.

**D9 — exit `2` for a port that cannot be bound, and no new code.** `ExitCodes` has seven members and each names a
condition a script can act on; "the port you named is busy" is not one of them, and adding an eighth for a mistake fixed
by typing a different number would be a code nobody branches on. The command line named something that cannot be
honoured, which is what `2` means, and the message carries the port and the flag.

**D10 — `--json` prints the endpoint, and then the process keeps running.** `index --json` prints one object and exits;
`ui --serve --json` prints one object and serves, so a script can start the panel, read the port out of a pipe and stop it
with a signal instead of scraping a sentence for a number. Nothing else goes to stdout after that — an answer is a socket
response, not a line — which is also why the object is printed before the first request rather than after the first one.

## Constitution Check

*GATE: passed before Phase 0 research, re-checked after Phase 1 design. Version 1.2.0.*

| Principle | How this round complies | Verified by |
|---|---|---|
| **I. The Model Is a Port** | Nothing here touches a model, an endpoint or `IChatModel`. `SharpCoreAgent.Domain` is not edited at all; the listener, the document and the page live in `Cli`, which may reference HTTP types. | `git diff --stat -- src/SharpCoreAgent.Domain` is empty; the layering review sees one project touched. |
| **II. Tools Are the Only Door** | The panel registers no `ITool`, publishes nothing over `mcp serve`, and the served allow-list is untouched. It is a reader of the index, the same read `index --status` performs. | `mcp serve`'s published set is unchanged and `UiCommandParsingTests` asserts `ui` is not a verb under `mcp`. |
| **III. No `Process.Start`** | The command starts no process. It prints a URL. FR-005 and D7 say so, and FR-006 refuses the tempting shortcut of shelling out to `index --status --json`. | `UiCommandParsingTests` plus the reviewer's grep for `Process.Start`, whose only two call sites stay the shell tool and the MCP client. |
| **IV. Every Loop Has a Budget** | The round adds no loop. Its one unbounded-looking surface — a server that serves until interrupted — is bounded by the developer's Ctrl+C, and its one walk is bounded by `--index-budget` when `--refresh` is given. The quoted line is the round's premise: a UI never owns the number. | `--refresh` off by default (FR-007); the page holds no cache (FR-002, D8). |
| **V. Streaming Is the Default** | Not applicable: no turn runs. Recorded rather than left blank. | — |
| **VI. Evidence over Assertion** | Every requirement has a test that fails without it, named in "What makes this round done". The load-bearing one compares `index --status --json` with `/api/status.json` from one index, so removing the shared document breaks a test rather than a screenshot. | `IndexStatusDocumentTests`, `UiPageTests`, `UiCommandParsingTests`, `UiServerTests`. |
| **VII. Zero Warnings, Snapshot Surface** | The new types are `internal`, like `CliOptions` and the other commands, and `SharpCoreAgent.Cli` carries no `PublicAPI.*.txt`, so the shipped surface of every assembly is unchanged. The build gate is unchanged and must stay at zero. | `dotnet build -c Release` at 0 warnings; no `PublicAPI` file in the diff. |
| **VIII. Secrets Stay in the Process** | No key is read, carried or logged. The status document contains no credential by construction — its fields are counts, names and a timestamp. The listener makes no outbound call; the page makes none at all (FR-010). `mcp serve`'s carve-out is untouched, and this round adds no member to the opt-in list, because a listener the developer typed is not a connection the agent opened. | FR-010's no-other-origin test; the reviewer's read of `IndexStatus`'s fields. |
| **Technical Constraints** | `net11.0`, no new dependency: `HttpListener` is in the platform (verified in the reference pack and the shared runtime), not a package. No native binary is shipped; `http.sys` is the operating system's. No platform-specific code: the prefix is the same string everywhere, and the one place the platform can refuse it is a probe with two recorded fallbacks. | The pack/runtime check in "The facts this round is built on"; `dotnet list package` shows no addition. |

No amendment is needed, and this is the round's strongest claim: unlike an agent-facing server (a listener that calls
back into a model endpoint) or a remote-user-control IDE channel (a listener that calls back into an IDE), a loopback
reader the developer started by hand fits the constitution as written.

## Project Structure

### Documentation (this feature)

```text
specs/023-ide-surface/
├── spec.md              # the argument (written)
├── plan.md              # this file
└── tasks.md             # the slice list (/speckit-tasks output)
```

No `research.md`, `data-model.md` or `contracts/` directory: the round introduces no entity, no wire format and no
persisted state, and the two routes it adds are pinned in `specs/001-coding-agent-core/contracts/cli.md` beside every
other command rather than in a per-feature copy of the same table. The one open question that a `research.md` would
carry — which listener — is D5, and it is answered by a probe task rather than by a document.

### Source Code (repository root)

```text
src/SharpCoreAgent.Cli/
├── CliOptions.cs                    # + CliCommand.Ui, FinishUi, --serve/--port/--refresh, the refusals
├── HelpText.cs                      # + synopsis line, the options, the Panel section
├── Program.cs                       # + the CliCommand.Ui arm of the dispatch switch
├── Ui/
│   ├── IndexStatusDocument.cs       # the one status object: Build + Serialize + the five formatters
│   ├── UiPage.cs                    # the page as a function of the document: Render
│   ├── UiServer.cs                  # HttpListener, two routes, Host check, no-store
│   └── UiEndpoint.cs                # the loopback prefix, its authority, --port parsing, the --json object
└── Commands/
    ├── IndexCommand.cs              # StatusAsync delegates its dictionary to IndexStatusDocument
    └── UiCommand.cs                 # --serve: open workspace + index, print the URL, listen

tests/SharpCoreAgent.Tests/Cli/
├── IndexStatusDocumentTests.cs
├── UiPageTests.cs
├── UiCommandParsingTests.cs
└── UiServerTests.cs
```

**Structure Decision**: the single-project layout the repository already uses, with a `Ui/` folder beside `Commands/`
because the listener, the document and the page are one capability with three parts and none of them belongs in
`Commands/`, which holds one file per command. `UiCommand.cs` is the only file in `Commands/` this round adds. Nothing
under `src/SharpCoreAgent.Domain`, `Application`, `Infrastructure` or `DependencyInjection` is touched.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| *(none)* | — | — |

No violation to record. The round adds one command, four internal types in one folder, one folder in the test project and
no dependency; `Process.Start` is not involved, no tool was added, `SharpCoreAgent.Domain` is untouched, and no public
API surface changes. The one piece of new machinery — an HTTP listener — is BCL framework, verified present in the
reference pack and the shared runtime this build targets.

## What the build must produce

- `dotnet build .\SharpCoreAgent.slnx -c Release` at 0 warnings, 0 errors, with the new `System.Net.HttpListener` usage
  raising no analyzer finding. Two findings are expected and must be fixed rather than silenced: `CA2000`-style disposal
  of the listener (it is stopped and closed in a `finally`) and `S6966`/async preferences on the request loop if the
  analyser asks for them.
- `dotnet test -c Release --no-build` green, with the round's delta recorded in the commit that carries it, the way round
  13 recorded its twenty-five against 1,331.
- The evidence table in `spec.md` closed case by case: `IndexStatusDocumentTests`, `UiPageTests`, `UiCommandParsingTests`,
  `UiServerTests`, each failing on revert.
- `README.md`, `HelpText.cs` and `specs/001-coding-agent-core/contracts/cli.md` updated in the same commit as the code, and
  `specs/000-parity-roadmap.md` given round 14 in a follow-up commit, because a commit cannot contain its own hash.
