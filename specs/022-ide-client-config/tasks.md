# Tasks — 022 ide client config

Every row is closed by evidence: a test that fails without the change, or a `dotnet build` and `dotnet test` run
recorded in the commit that carries it.

## Phase 1 — the fourth client

- [X] T001 `McpClientKind.VisualStudio` in `CliOptions`, with the remarks that say why it shares `.mcp.json` with Claude
      Code and `servers` with VS Code, and the enum's own summary corrected from "three files with three shapes" to
      "four clients, three files, two shapes".
- [X] T002 `ParseClient` accepts `visualstudio` and `vs`, case-insensitively, and the two hints that list the clients
      name all four (`mcp config needs --client`, `unknown client`).
- [X] T003 `McpClientSnippets.TryShape` — the one place that says which key a client reads and whether it wants a
      `type` — and `McpClientSnippets.Entry`, the one place the entry object is built. `Render` and the writer both go
      through them, so a block and a merge cannot disagree about the key.
- [X] T004 `Destination(VisualStudio)` returns `.mcp.json` in the workspace, with the remarks that record the documented
      discovery order and why the `.vscode` copy is the wrong advice.
- [X] T005 `Note(VisualStudio)` — "solution scope: Visual Studio reads .mcp.json beside the .sln" — and the prefix every
      stderr line shares lifted into `NotePrefix`, so the printing and writing paths cannot drift apart.

## Phase 2 — the merge

- [X] T006 `McpConfigWriter.Write` (`Cli`): reads the destination, keeps every top-level key, every other server and
      every entry field the command does not own, writes once without a byte-order mark, and answers `(outcome, note)`.
      An entry that already matches is not written to at all.
- [X] T007 `McpConfigWriteException` (public, like `CliUsageException`) for the three cases that cannot be merged, raised
      before anything is written.
- [X] T008 `CliOptions.McpConfigWrite`: the `--write` case, `RejectWriteFlag` beside `RejectClientFlag`, calls on run,
      chat, index, models, config, setup, `mcp serve` and `mcp bench`, and the `--client codex --write` refusal in
      `FinishMcp`'s `config` verb.
- [X] T009 `McpCommand.WriteSnippetAsync`: `--write` routes to the writer, writes nothing on stdout, puts the note on
      stderr, and maps a refusal to exit `7` with the message prefixed like every other line this command writes.

## Phase 3 — the tests

- [X] T010 `McpConfigWriteTests` (nine cases): created with nothing on stdout; other keys and servers kept; both keys in
      one `.mcp.json` after two runs; a second run byte-identical and saying "nothing written"; an `env` block kept while
      a stale `command` is replaced; `.vscode` created; not-JSON and `servers`-as-array each refused at exit `7` with the
      file unchanged.
- [X] T011 `McpConfigCommandTests` (+1, plus the note case extended): `--client visualstudio` — `servers`, `type:
      "stdio"`, the binary that exists, `mcp serve` last, `.mcp.json` and "solution scope" on stderr.
- [X] T012 `McpConfigParsingTests` (+15): the three new spellings parse to one kind; `--write` parses beside `--client`
      and defaults off; four clients in both hints; `--client codex --write` refused; `--write` refused on eight other
      command shapes; `--help` and `--version` answer with it in the history.
- [X] T013 `HelpTextTests` amended rather than extended: the golden `Serve` section, the `--client` description column,
      and the usage-block assertions now cover the fourth name and the `--write` paragraph.

## Phase 4 — the record

- [X] T014 `dotnet build -c Release` at 0 warnings, 0 errors; `dotnet test -c Release --no-build` at 1,356 passed,
      0 failed (25 of them this round's, on a tree carrying 22 from an unrelated uncommitted slice).
- [X] T015 `HelpText`: the synopsis line, the `--client` row, a `--write` row at the three documented columns, the `Serve`
      section's command list and its wiring paragraph, one new paragraph for the merge, and an example
      (`mcp config --client visualstudio --write`).
- [X] T016 `README.md`: the synopsis line, the flag table's `--client` row and a `--write` row, and the "wiring a
      client" section rewritten around the four clients, the merge and the wrong-key/wrong-file failure the fourth name
      exists to prevent.
- [X] T017 `specs/001-coding-agent-core/contracts/cli.md`: the synopsis line, both option rows and the exit-code `7` row.
- [X] T018 `specs/000-parity-roadmap.md`: round 13 in the table, a round-13 section, and `022` added to the numbering
      note.
- [X] T019 The uncommitted index-timing slice already in the working tree left untouched: no file it owns is staged,
      edited or reverted by this round.
