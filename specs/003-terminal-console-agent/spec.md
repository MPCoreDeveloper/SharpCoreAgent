# 003 — Terminal console agent: colour, fragment edits, plan mode

**Status**: implemented (P0–P2, P3-lite, P4-aliases)
**Branch**: `feature/sharpcoredb-2-1-2`
**Depends on**: `001-coding-agent-core`, `002-code-evidence-index`

## Why this feature exists

SharpCoreAgent already runs a real turn: it streams, calls tools, asks before writing, budgets the
session and writes a transcript. What it did not do was behave like a terminal product a developer
would choose over an editor panel. Three gaps were measured against Cline, not guessed:

1. **No colour anywhere in the product.** Every glyph (`· → ← ⨯ —`) was emitted plain. The only ANSI
   escapes in the repository were in `eng/sonar-scan.ps1`. A developer scanning a long turn had to read
   every line to find the tool that failed.
2. **Edits were whole-file rewrites.** `write_file` was the only way to change a file, so a model that
   had read three lines reproduced the other four hundred. Approval showed `allow write_file? {json}`
   with no indication of what would change.
3. **No plan/act split.** `--approval readonly` prevented writes, but the model was still asked to act,
   and nothing in the transcript said "this is a proposal, not a change".

Two smaller gaps were closed in the same pass because they sit on the same code paths: the session had
no way to shrink a long transcript, and switching models required the fully qualified
`provider:model` spelling.

## User stories

### US1 — A coloured terminal that stays plain when it must (P0)

As a developer running a turn, I want the tool, its result, the timing and the diff to be visually
distinct, so that I can follow a twenty-call turn without reading every character.

Acceptance:

- `sharpcoreagent run "…"` on a terminal paints glyphs, tool names, timings and summaries.
- Piping the same command produces byte-identical output with no `\u001b` sequence.
- `NO_COLOR=1`, `TERM=dumb` and `--color never` each suppress colour; `--color always` forces it.
- `--json` output contains no escapes, whatever `--color` says.

### US2 — An edit I can review before it happens (P1)

As a developer in `ask` mode, I want the approval prompt for a file edit to show the change, so that
`y` is an informed decision rather than a leap of faith.

Acceptance:

- `replace_in_file` replaces one exact fragment and leaves every other byte identical.
- An ambiguous match (the fragment occurs twice, `all` not set) is refused, and the file is untouched.
- The prompt renders `--- a/path`, `+++ b/path`, a hunk header and `-`/`+` lines before `[y/N]`.
- A missing match reports guidance ("read the file first") rather than an empty success.

### US3 — Plan first, act second (P2)

As a developer facing a risky change, I want the agent to investigate and propose, then carry out the
same plan once I agree, without losing the reasoning.

Acceptance:

- `--mode plan` and `/mode plan` advertise only read-only tools in the request.
- A destructive call named anyway is refused with a message that says plan mode is on.
- The plan instruction is prepended per request, so switching to `act` restores the full tool set.
- The transcript is shared: `/mode act` continues the same conversation.

### US4 — A long session that stays usable (P3-lite)

As a developer in a long `chat`, I want to drop old turns without losing the system prompt, so the
request stops growing and the provider stops rejecting it.

Acceptance:

- `/compact [n]` keeps every leading system message and the last `n` messages (default 8).
- The window never begins with a tool result whose call was dropped.
- The removed count is reported; a transcript that already fits is left alone.

### US5 — Model switching without the ceremony (P4-lite)

As a DeepSeek/Grok user, I want the three models I actually switch between to have short names.

Acceptance:

- `/model ds`, `/model r1`, `/model reasoner`, `/model grok`, `/model g3` resolve to a provider and
  model from the catalogue.
- An unknown alias falls through to the existing bare-identifier path, whose error names the provider.


## Functional requirements

- **FR-001** The CLI MUST render through one palette (`AnsiTheme`); no renderer may write a raw escape.
- **FR-002** Colour MUST default to `auto`, which is off when stdout is redirected, `NO_COLOR` is set
  (presence, including empty) or `TERM=dumb`.
- **FR-003** `--json` MUST NOT contain escape sequences; the observer forces the plain palette.
- **FR-004** `--color auto|always|never` MUST be accepted by every command and MUST fail with exit `2`
  and a hint on an unknown word.
- **FR-005** A tool that can describe its change MAY implement `IToolPreview`; the loop MUST attach the
  preview to `ToolApprovalRequest` and MUST treat a preview failure as "no preview", never as a denial.
- **FR-006** `replace_in_file` MUST be `Destructive`, MUST match ordinally, MUST refuse zero or
  ambiguous matches, and MUST write atomically (temp file + rename) as UTF-8 without a BOM.
- **FR-007** `--mode plan` MUST filter the advertised definitions to `ToolSafety.ReadOnly` and MUST deny
  a destructive call even when approval is `allow`.
- **FR-008** Plan mode MUST add its instruction per request, not mutate the stored system prompt.
- **FR-009** `/compact` MUST keep leading system messages verbatim, MUST NOT create an orphan tool
  result, and MUST report how many messages it removed.
- **FR-010** `/model <alias>` MUST resolve aliases before treating the argument as a model identifier.
- **FR-011** `--help`, `README.md` and `HelpText.ChatCommands` MUST document every new flag, command and
  the `NO_COLOR` opt-out.

## Success criteria

- `dotnet test` is green: 497 tests (460 before this feature, 37 added).
- With output redirected, `run`, `chat`, `models` and `--help` contain no `\u001b` byte.
- An `ask`-mode edit on a real file prints a hunk whose `-` lines match the file before approval.
- A plan turn on any provider performs no write: the tool list has no destructive entry, and a forced
  call is refused without touching the disk.
- `/compact` on a 40-message session removes the old turns, keeps the system prompts, and the next
  request is accepted by the same provider.

## Out of scope

- MCP servers (deliberately deferred in `002/research.md` D14).
- Multi-hunk `apply_diff` against a patch file; `replace_in_file` is one exact fragment per call.
- Git checkpoints and `/undo`; the transcript is the record, not a rollback mechanism.
- `--resume` as a first-class flag: `--session <path>` already resumes, and it is documented.
- Per-token cost in currency; `/usage` reports the token counts the provider actually sent.
- Provider fallback on 401/5xx; a failed turn still reports the provider's message.

## Risks

- **Escape leakage.** A raw escape written by a new renderer would corrupt `--json`. Mitigated by
  routing every renderer through `AnsiTheme` and by forcing `Plain` in the observer's JSON mode.
- **Ambiguous edits.** A model may retry a refused match with `all=true` where it meant one occurrence.
  The refusal text says "include more surrounding lines" first, and `all` second.
- **Plan-mode drift.** A model can describe a plan and still call a read-only tool. `run_shell` and
  every file mutation are destructive, so the read-only set is exactly the tools that cannot change the
  workspace.
