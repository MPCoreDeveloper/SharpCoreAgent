# Spec — 016 model roles and the pasted key

**Status**: landed on `main` as `6ac0c90` (2026-10-31), no merge commit, with the tree that commit carries
1228/1228 in `Release` at 0 warnings. Spec number `016`: `009`–`011` are reserved by
`specs/000-parity-roadmap.md` and `016` was the next free one. Round 8 of the parity roadmap.

**Depends on**: `001` (the loop and the CLI), `004` (the fallback, which taught the preflight to check a second
model), `005` (`--credential-file` and `SHARPCOREAGENT_CREDENTIAL_FILE`), `006` (the settings document, `config`,
and the open task T016 this round closes).

## Why this round exists

A session used one model for everything. The three things a session does are not one job: planning is reasoning
over a large transcript, acting is many small tool calls that must be fast and cheap, and reviewing is a second
opinion on work already done. A developer with a Grok key and a DeepSeek key picks the cheaper one for edits and
the stronger one for the plan — today that is `--model` per process, which loses the reason a plan was reached
because the transcript does not survive it, and no combination is possible at all.

The second gap is the first five minutes. A key that lives in a variable is a failed first run, then a search for
the variable's name, then an editor, then a shell restart. `006` deferred the interactive prompt (T016) with the
honest note that it is three features — echo suppression, per-platform permissions, rotation — and shipped the
hint instead. This round ships the first two and leaves rotation to the file the developer can edit.

## What it adds

| Surface | Shape |
|---|---|
| `--planner`, `--executor`, `--reviewer <model>` | a model per job, `provider:id`, an alias, or a bare id that keeps `--provider` |
| settings `planner`, `executor`, `reviewer` | the same three, durable, through `config set`/`setup`'s file |
| `SHARPCOREAGENT_PLANNER_MODEL`, `_EXECUTOR_MODEL`, `_REVIEWER_MODEL` | the same three, per shell |
| `/roles` | what each job runs on, and which of them is just the session model |
| `/review [focus]` | one read-only turn on the reviewer's model, in this transcript, with the approval mode and the model restored afterwards |
| first-run prompt | a missing key is offered as a paste instead of a refusal, written to `credentials.env` beside the settings file |

## Requirements

- **FR-001** A plan turn runs on the planner's model when one is named, and on the session model when none is.
  An act turn does the same with the executor.
- **FR-002** Naming no role changes nothing: a session with no roles behaves exactly as it did before this round,
  including `/model` followed by `/mode`.
- **FR-003** A role value is resolved by the same catalogue `/model` uses: `provider:id`, an alias (`ds`, `r1`,
  `grok`, …), or a bare identifier that keeps the effective provider. A bare id resolves against the provider in
  force at the end of the command line, so flag order cannot change its meaning.
- **FR-004** `/review` runs its turn with approval read-only and the reviewer's model, then restores both. It never
  changes the mode, and it never writes.
- **FR-005** Every model the process may use is checked for a credential before the first turn — the session's, each
  named role's, and the fallback's — so a missing key exits `3` at startup rather than mid-conversation.
- **FR-006** With a terminal on both ends and nothing configured, a missing key is offered as a paste. The key is
  read with the echo off and is never printed, logged, put in a transcript or repeated in a message.
- **FR-007** A pasted key is written to `NAME=value` in the file `--credential-file` /
  `SHARPCOREAGENT_CREDENTIAL_FILE` names, or to `credentials.env` beside the settings file when neither names one;
  the file keeps every line it had, replaces the assignment for that name, and is readable by its owner only where
  the platform has file modes.
- **FR-008** The pasted key works for the rest of the process without a restart, and — when the developer named no
  file — the settings document gains `credential-file`, so the next run reads it too.
- **FR-009** A piped stdin, a declined `[Y/n]` or a blank paste writes nothing and exits `3` with the three
  documented routes. Nothing is ever asked on a stream that cannot answer.
- **FR-010** `--planner` and friends are refused on `mcp serve` like every other model flag, and `config unset` keeps
  working on a file that holds a role value this build cannot resolve.

## Out of scope

- Rotation, revocation and expiry of keys. The file is a plain `NAME=value` document the developer edits; a secret
  manager is a different product.
- Per-role budgets, temperatures or approval modes. A role names a model, nothing else.
- A fourth role, or a role per tool.
- `config edit` (006 T017) and per-provider sections (006 T018).

## Acceptance

`dotnet build .\SharpCoreAgent.slnx -c Release` with zero warnings and the whole suite green. The behaviour is
covered by `AgentRoleOptionTests` (parsing and resolution), `ChatRoleTests` (`/roles`, the mode switch, `/review`),
`CredentialPromptTests` (the paste, the refusal, the default file) and `CredentialFileUpsertTests` (the write).
