# 007 — Look into another project: read-only attached roots

**Status**: implemented on `feature/007-external-roots` (merge to `main` is the last step of the round)
**Branch**: `feature/007-external-roots`
**Depends on**: `001-coding-agent-core` (the tool model and `Workspace`), `003-terminal-console-agent`
(the console surface this extends), `006-interactive-start-and-config` (the precedence rules this
deliberately does not join)

## Why this feature exists

A workspace is one directory tree, and every tool resolves against it. That is the property that makes
"no tool may escape the root" a fact about the model rather than a rule each tool has to remember, and
it is worth keeping. It is also the first thing that stops real work.

The developer who hits it is not asking for a bigger workspace. They are asking to read one file in
the other repository — the shared library this service depends on, the proto definitions, the sibling
app whose API contract is the question — while the change being made belongs to this repository. The
workarounds today are both worse than the problem: copy the file in, which is a lie in the transcript
and a file to delete later, or widen `--workspace` to a parent directory, which hands the agent write
access to every sibling project at once.

So this round adds a second kind of root: attached, explicitly named, readable, and never writable.
The primary root keeps every guarantee it has. An attachment is a reading list, not a second workspace.

## User stories

### US1 — Read a file in another repository (P0, Phase 1)

As a developer changing this service, I want to read a file from the repository it depends on, so that
the change is informed by the real interface instead of my memory of it.

Acceptance:

- `--add-root ..\Shared.Contracts` makes `read_file` accept
  `D:\repos\Shared.Contracts\src\Order.cs` and return the numbered lines.
- `list_directory` and `search_text` accept the same absolute path and answer from that tree.
- `list_roots` names the primary root and every attachment, so the model can see what it may read
  instead of guessing a path.
- The banner says the session has one read-only attachment.

### US2 — The write is refused, and says why (P0, Phase 1)

As a developer who attached a repository to read, I want a write into it refused with a sentence I can
act on, so that an attachment can never become an edit I did not intend.

Acceptance:

- `write_file`, `replace_in_file` and `delete_file` refuse an absolute path inside an attachment, and
  the refusal names the root and the words `read-only`.
- `run_shell` is unchanged: it runs in the primary root, and what a command does with the rest of the
  file system is the trust decision `--approval` already makes. This round does not pretend the shell
  is confined when it never was.
- A relative path still means the primary root, even when an attachment holds a file of the same
  relative name.

### US3 — An attachment cannot widen the escape hatch (P1, Phase 1)

As a maintainer, I want a link inside an attached tree judged against the whole set, so that attaching
a directory does not create a path to `/etc` or `C:\Windows`.

Acceptance:

- A symbolic link or junction inside an attachment whose target is outside every root is refused by
  the physical check, with the same shape of message the primary root already produces.
- A link inside an attachment that points into the primary root is accepted, because it stays inside
  what the developer granted.

## Functional requirements

- **FR-001** `Workspace` MUST carry an ordered set of attached roots alongside its primary root. The
  set is part of the workspace value, so `ToolInvocationContext.Workspace` reaches it without a new
  context type and no tool can be invoked with attachments silently missing.
- **FR-002** An attached root MUST be readable and MUST NOT be writable. `write_file`,
  `replace_in_file`, `delete_file` and every shell command MUST resolve against the primary root alone.
- **FR-003** `Workspace.TryResolve` MUST keep its current behaviour, messages included, because it is
  the write path and the suite pins it. A separate read path MUST accept a relative path (meaning the
  primary root) and an absolute path inside the primary root or any attachment, and MUST reject
  everything else.
- **FR-004** A relative path MUST always mean the primary root. Attachments MUST NOT be searched in
  order, because two projects that both contain `src/Order.cs` must not make one path mean whichever
  root was attached first.
- **FR-005** `PathSafety` MUST judge the physical target against the whole set: a path whose link
  chain leaves every root MUST be refused, and one that ends inside any root MUST be accepted.
- **FR-006** `read_file`, `list_directory` and `search_text` MUST use the read path. The five index
  tools MUST keep answering from the primary root only, with a refusal that says an attachment is not
  indexed; one index describes one tree.
- **FR-007** A new read-only `list_roots` tool MUST report the primary root and every attachment, and
  MUST be how a model learns an absolute path it may use.
- **FR-008** `--add-root <path>` MUST be repeatable on `run` and `chat`. `SHARPCOREAGENT_ADD_ROOTS`
  MUST supply the same list, `Path.PathSeparator`-separated. Command-line roots come first, in the
  order given, then the environment's.
- **FR-009** Attachments MUST NOT be written to the settings file and MUST NOT be added to the
  session snapshot; a resumed session starts with the roots its new invocation names.
- **FR-010** A root that does not exist, is a file rather than a directory, or cannot be normalised
  MUST fail before the first request as a usage error naming the path and the reason, exit 2. A root
  that equals, contains or is contained by another root MUST be de-duplicated rather than rejected.
- **FR-011** The banner MUST gain a `roots` row when any attachment exists, naming how many there are
  and where the first one is; `--quiet` still silences it. `chat` MUST answer `/roots` with each root,
  its kind, and its resolved path.
- **FR-012** The behaviour MUST be documented in `HelpText`, `README.md`,
  `specs/001-coding-agent-core/contracts/cli.md` and `specs/007-external-roots/contracts/cli-roots.md`
  in the same change.
- **FR-013** The round MUST add no dependency and MUST list every new public type in
  `PublicAPI.Unshipped.txt`.

## Success criteria

- **SC-001** One turn reads a file from an attached repository and is refused when it writes one, both
  proven by tests that fail on revert.
- **SC-002** The existing suite passes without deleting or weakening a confinement assertion: the
  change adds assertions to `tests/` and removes none that pin a refusal.
- **SC-003** `--add-root` naming a missing directory exits 2 before any request, with the path in the
  message.
- **SC-004** A link inside an attachment that escapes every root is refused; one that lands in the
  primary root is accepted.
- **SC-005** A relative path resolves to the same file with and without attachments present.
- **SC-006** Zero warnings on `dotnet build -c Release`, suite green, `README.md` tool list and suite
  count updated.

## What this feature rejects, and why

- **Making the workspace a list and letting every tool pick from it.** Then "which root did that write
  land in" becomes a per-call question, every confinement assertion in the suite has to be rewritten,
  and the primary root stops being a guarantee. An attachment is the narrower thing: secondary, and
  read-only.
- **A root-qualified path syntax (`@shared/src/Order.cs`).** A second path grammar is a second parser,
  a second error vocabulary and one more thing the model gets wrong. Every tool already understands an
  absolute path; `list_roots` is how one is learned.
- **Persisting attachments in the settings file.** This is 006's rule read the other way round: a
  *preference* is a file the developer chose, and *authority* is a flag they typed. A persisted
  attachment is read access granted by a file nobody re-reads.
- **Indexing an attachment.** One index describes one tree. A shared index doubles the build, needs a
  staleness story per root, and makes `verify` mean two things at once.
- **Confining the shell tool to the primary root.** It never was confined — a command can `cd ..` —
  and saying otherwise in an error message would be a lie in the transcript. The shell's boundary is
  `--approval`, unchanged.
- **A writable attachment (`--add-root --writable`).** Deferred rather than refused on principle: it
  needs a per-root approval prompt and a per-root safety class, which is its own specification.

## Exit codes

| Code | Meaning |
|---|---|
| 2 | a root names a path that is not an existing directory |

No new number. A bad root is a bad command line, which is exactly what 2 already means.

## Risks

- **The model starts using absolute paths for the primary root too.** Mitigation: the tool
  descriptions keep saying a relative path is resolved against the workspace, and the refusal message
  for an outside path points at `list_roots` instead of inviting a guess.
- **A large attachment makes `search_text` slow.** It costs time, not context: the existing result
  ceiling still bounds what comes back, and the attachment is not in the index.
- **`/roots` and `list_roots` drift apart.** Both render from one accessor and one formatter, so a
  change to either is a change to the single place that prints a root.
- **Two attachments with the same leaf name confuse a reader.** The banner prints a count; `/roots`
  and `list_roots` print resolved absolute paths, which cannot collide.

## Decisions

1. **Absolute-path addressing, no new syntax.** `list_roots` exists so the path is discovered rather
   than invented.
2. **Per invocation, never persisted.** `--add-root` and `SHARPCOREAGENT_ADD_ROOTS` only; the settings
   file and the session snapshot are untouched, and a resume inherits nothing.
3. **Read-only, always.** Writes, deletes and shell commands resolve against the primary root alone,
   and the refusal says `read-only` in as many words.
4. **The index stays primary-only.** Index tools answer from the root the index was opened for and say
   so when asked about an attachment.
5. **One containment authority.** Attachments live in `Workspace`, not in a parallel type each tool
   consults, because principle II says every path question is answered in one place.


