# Tasks — 023 ide surface

Every row is closed by evidence: a test that fails without the change, or a `dotnet build` and `dotnet test` run
recorded in the commit that carries it. `[P]` marks rows that touch different files and can be done in any order;
`[US1]`/`[US2]` map a row to a user story in `spec.md`.

## Phase 1 — Setup

**Purpose**: the command exists on the command line, and nothing is bound yet.

- [x] T001 `src/SharpCoreAgent.Cli/CliOptions.cs`: `CliCommand.Ui`, `UiServe`, `UiPort`, `UiRefresh`; `FinishUi`
      with `--serve` as its only action (required: `ui` without it is a usage error) and a positional after it refused
      the way `mcp serve` refuses one, `RejectUiFlags` beside `RejectClientFlag`/`RejectWriteFlag` for FR-013's eleven
      flags, and `RejectUiOptions` for `--port`/`--refresh` on every other command. `--port` and `--refresh` join
      `ApplyOption`; `--index-budget` is reused as it is.
- [x] T002 `src/SharpCoreAgent.Cli/Ui/UiEndpoint.cs`: one small value type that validates a requested port (`0–65535`,
      digits only, empty means ephemeral), formats the loopback prefix and its slash-normalised URL, and builds the
      `--json` object of FR-008 with the slash-normalised workspace `index --status --json` already prints.

## Phase 2 — Foundational

**Purpose**: the one open question answered, the shared document extracted, the listener and the command wired. No user
story is complete until this phase is.

- [x] T003 Probe the platform question in `plan.md` D5, non-elevated, before any of it is written into the command:
      bind `http://127.0.0.1:0/` with `HttpListener`, `Start()` it, record whether it succeeds, and record the
      `HttpListenerException` code if it does not. The probe is a throwaway console run, not a committed test.
- [x] T004 Record the answer in `plan.md` under "The facts this round is built on" and fix the prefix in D5: `127.0.0.1`
      if the probe passed, `localhost` if it failed with error 5, and a stop-for-review if both are refused — the
      recorded fallback is `TcpListener`, which the pack also carries.
- [x] T005 `src/SharpCoreAgent.Cli/Ui/IndexStatusDocument.cs`: `Build(workspace, status)` returning the twenty-key
      dictionary of `index --status --json` and `Serialize` returning `JsonSerializer.Serialize(document)` with no
      options, as `WriteJson` does today. The five formatters that decide how a status reads to a person — `State`,
      `TierName`, `Slash`, `Number`, `Size` — move here from `IndexCommand.cs:660-751`, and `IndexCommand.StatusAsync`'s
      dictionary is replaced by a call into `Build`. Two call sites follow the moved `State`: `McpBench.cs:196` and
      `McpCommand.cs:154`. This is the round's only refactor of existing code, and the reason `/api/status.json` cannot
      drift from the CLI by a field or by a capital letter.
- [x] T006 `src/SharpCoreAgent.Cli/Ui/UiServer.cs`: `HttpListener` on the prefix from T004, `GET /` and
      `GET /api/status.json` answered from the rendered page and the serialised document, `403` for a foreign `Host`,
      `404` for anything else, `Cache-Control: no-store` on both, `Connection: close`, one request at a time, and the
      listener stopped and closed in a `finally` so Ctrl+C leaves no half-open port.
- [x] T007 `src/SharpCoreAgent.Cli/Commands/UiCommand.cs` and the `CliCommand.Ui` arm of `Program.cs`: open the
      workspace and the index the other commands open, print the URL and the note on stderr (`--json` prints the object on
      stdout instead), serve until cancelled, exit `0` on Ctrl+C, exit `2` with the port named when the bind fails, and
      `--refresh` walking inside `--index-budget` before each answer.

**Checkpoint**: `sharpcoreagent ui --serve` starts, prints a URL, and answers a page whose numbers are the index's.

## Phase 3 — User Story 1: the panel, served by the binary (P1)

**Goal**: one page carrying every number `index --status --json` carries, from the index that is open in this process.

**Independent Test**: the document and the page are pure functions of a constructed `IndexStatus`, so this story is
tested without a socket; the one socket test is the server's and it skips rather than fails where the prefix is refused.

- [x] T008 [US1] `src/SharpCoreAgent.Cli/Ui/UiPage.cs`: `Render(document)` returning the HTML — the state/tier header,
      the five count cards (`files`, `passages`, `symbols`, `edges`, `communities`), the health rows (`stale`, `skipped`,
      `orphans`, `dimensions`, `vectorSearch`), `sizeBytes` formatted as `--status` formats it, the `missing` state
      rendering its one hint and no card, HTML escaping through `WebUtility.HtmlEncode`, and the footer naming
      `sharpcoreagent index --status --json` as the source of every number.
- [x] T009 [P] [US1] `tests/SharpCoreAgent.Tests/Cli/IndexStatusDocumentTests.cs`: the key set and order match
      `index --status --json`; `BuiltUtc` null serialises as `null`; the two renderings of one open index are
      byte-identical — the case that fails if `IndexCommand` keeps its own dictionary.
- [x] T010 [P] [US1] `tests/SharpCoreAgent.Tests/Cli/UiPageTests.cs`: every FR-009 field appears with its value; a
      `missing` status renders the hint and no card of zeroes; a workspace path with `&`/`<` and a non-ASCII directory is
      escaped; `sizeBytes` is the formatted string, not a raw count.
- [x] T011 [P] [US1] `tests/SharpCoreAgent.Tests/Cli/UiServerTests.cs`: one loopback round trip on an ephemeral port —
      `GET /` at `text/html; charset=utf-8` with `no-store`, `GET /api/status.json` equal to the document's bytes, `POST /`
      refused `404`, a foreign `Host` refused `403`, `GET /api/other` refused `404` — skipped with a message rather than
      failed where the platform refuses the prefix.
- [x] T012 [US1] `tests/SharpCoreAgent.Tests/Cli/UiCommandParsingTests.cs`: `ui` with no action refused; `--serve`
      accepted; a positional after `--serve` refused; `--port` parsed, out of range and non-numeric refused; `--port 0`
      equal to absent; `--refresh` off by default; `--port` and `--refresh` refused on `run`, `chat`, `index`, `models`,
      `config`, `setup`, `mcp serve` and `mcp bench`; each of FR-013's eleven flags refused on `ui --serve`; `--json`
      object shape; `ui` unknown to `mcp`'s verb list.

**Checkpoint**: `index --status --json` and `/api/status.json` are one object, proven by a test rather than by a
screenshot.

## Phase 4 — User Story 2: the same page inside an editor (P2)

**Goal**: the page is legible in the editor the developer already has open, and the route to it is named in the note.

**Independent Test**: the stylesheet's two colour-scheme blocks and the note's two route names are asserted from the
rendered page and the command's stderr, with no editor involved.

- [x] T013 [US2] `src/SharpCoreAgent.Cli/Ui/UiPage.cs`: the `prefers-color-scheme` pair — dark and light, both complete,
      body contrast at or above 4.5:1, no colour that assumes white — and the surface/raised/border/accent triple the
      cards need so the page reads as a panel rather than a table dump.
- [x] T014 [US2] `src/SharpCoreAgent.Cli/Commands/UiCommand.cs`: the note that follows the URL on stderr —
      `open it in a browser, or in VS Code's Simple Browser (Ctrl+Shift+P → Simple Browser: Show) or a Visual Studio
      browser window` — and the workspace line, both on the same `note:` prefix `mcp config` uses.
- [x] T015 [P] [US2] `tests/SharpCoreAgent.Tests/Cli/UiPageTests.cs`: both `prefers-color-scheme` blocks present; no
      `http://`/`https://` reference other than the panel's own origin; no `<link rel="stylesheet"` to another host; the
      one `<script>` mentions `/api/status.json` and nothing else.
- [x] T016 [US2] `tests/SharpCoreAgent.Tests/Cli/UiCommandParsingTests.cs`: the note printed after the URL names
      `Simple Browser` and `Visual Studio`, and the workspace line follows it on the same prefix.

**Checkpoint**: the page is dark in a dark editor, light in a light one, and the developer is told where to open it.

## Phase 5 — Polish and the record

- [x] T017 `dotnet build .\SharpCoreAgent.slnx -c Release` at 0 warnings, 0 errors. Expected findings to fix rather than
      silence: disposal of the `HttpListener` (`CA2000`-family), async preferences on the request loop, and the analysers'
      preference for `ConfigureAwait(false)` on every new `await`.
- [x] T018 `dotnet test -c Release --no-build` green; the round's own delta and the total recorded in the commit
      message, the way round 13 recorded twenty-five against 1,331.
- [x] T019 `src/SharpCoreAgent.Cli/HelpText.cs`: the synopsis line, the `ui` section with its command list, `--port`,
      `--refresh` and `--json` rows at the three documented columns, one paragraph on the two routes, and an example
      (`sharpcoreagent ui --serve`). `HelpTextTests`' golden block amended rather than duplicated.
- [x] T020 `specs/001-coding-agent-core/contracts/cli.md`: the `ui` command, its options, the two routes, and the exit
      codes `0` and `2` it can produce — including the sentence that the listener is loopback-only by construction and
      that no flag widens it.
- [x] T021 `README.md`: the synopsis line, the flag table, and a "seeing the index" section with the two routes into an
      editor, the safe default (`--refresh` is the opt-in), and the one sentence that says nothing leaves the machine.
- [x] T022 `specs/000-parity-roadmap.md`: round 14 in the table, a round-14 section, and `023` added to the numbering
      note — in a follow-up commit, because a commit cannot contain its own hash.

---

## Dependencies & Execution Order

- **Phase 1 (T001–T002)** — no dependency. `UiCommand` cannot be dispatched before T001 parses `ui`, and T002 is pure.
- **Phase 2 (T003–T007)** — T003 gates T004, T004 gates T006 (the prefix is in the listener), T005 is independent of
  T003/T004 and may be done first, and T007 needs T005, T006 and the `UiPage.Render` signature that T008 then fills in.
- **Phase 3 (T008–T012)** — T008 needs T005 (the document) and nothing else; the test rows need their subject.
  T009 is the row that pins FR-003 and it is worth doing before T010–T012.
- **Phase 4 (T013–T016)** — needs T008. Independent of Phase 3's tests and safe to interleave with them.
- **Phase 5 (T017–T022)** — last. T019 is atomic with its own `HelpTextTests` amendment, so the golden usage block and the
  text that satisfies it land in one commit; `README.md` (T021) and `contracts/cli.md` (T020) may be written alongside
  their code. T022 is the follow-up commit.

### Parallel Opportunities

```text
# After T004 and T005, these three touch different files:
T006  UiServer.cs          T008  UiPage.cs            T009–T011  the three test files [P]
```

`[P]` rows in this round are T009, T010, T011 and T015: four files that no other row writes.

## Implementation Strategy

### MVP first (User Story 1 only)

1. T001, T002 — the command parses and refuses what it must refuse.
2. T003, T004 — the platform question answered before a line of listener is written.
3. T005 — the shared document, with `IndexCommand` delegating to it.
4. T006, T007, T008 — the listener, the command and the page.
5. T009–T012 — the evidence.
6. **Stop and validate**: `ui --serve` against this repository's own index, with `index --status --json` beside it in
   another terminal, comparing field by field.

### Incremental delivery

Phase 1 + 2 → the command runs. Phase 3 → the numbers are proven. Phase 4 → it looks like part of the IDE. Each phase
leaves a build that passes and a story that stands on its own; US1 is shippable without US2, and US2 is a stylesheet and
two sentences on top of it.

## Notes

- One slice per session: this round ends at T022. The VS Code webview and the Visual Studio 2026 tool window are the next
  spec, recorded in `spec.md` under "What this round does not build" with their costs.
- The only existing file this round refactors is `IndexCommand.StatusAsync`, four lines becoming one call. Anything else
  in that file is out of scope by definition.
- `Process.Start` appears nowhere in this round; the shell tool and the MCP client keep their two call sites.
- If T003 fails for both prefixes, the round stops after T004 and the fallback (`TcpListener` with a two-route responder)
  becomes its own plan, because that is a different amount of code and a different set of tests.

