# Tasks — 007 external roots

Legend: `[X]` implemented and covered by tests in this change; `[ ]` not started, kept here so the
next pass has a real list rather than a memory.

## Phase 1 — the workspace carries the roots

- [X] T001 `Workspace` gains `Attachments` (`IReadOnlyList<Workspace>`), `HasAttachments`, and `WithAttachments(IEnumerable<string>)`, which normalises each root through `Create`, drops duplicates, drops a root equal to or nested under the primary root, and returns a new value. A root that does not exist keeps throwing `DirectoryNotFoundException` (FR-001, FR-010).
- [X] T002 `Workspace.TryResolveRead(path, out fullPath, out owner, out error)`: relative resolves against the primary root only; absolute resolves against the first root that contains it; everything else is refused with a message naming the primary root, the attachment count and `list_roots` (FR-003, FR-004).
- [X] T003 `Workspace.IsInsideAny(path)` for the physical check, and `IsInside`/`TryResolve` left byte-for-byte as they are (FR-002, FR-003).
- [X] T004 `Workspace.DescribeOutside(path)` returning the read-only-attachment sentence or `null`, so the write tools can explain a refusal without re-wording `TryResolve`.
- [X] T005 `PathSafety.TryResolveRead` built on the existing `ResolvePhysical` walk and `IsInsideAny`, so link handling has exactly one implementation (FR-005).

## Phase 2 — the tools

- [X] T006 `read_file` resolves through `TryResolveRead` and prints the path relative to whichever root answered it, not an absolute path.
- [X] T007 `list_directory` resolves through `TryResolveRead`; its listing is unchanged in shape, so the model needs no new parser.
- [X] T008 `search_text` resolves through `TryResolveRead` and keeps its existing result and character ceilings. It also gained the optional `path` argument this needs — without one it could never be pointed at an attachment — and a file path searches that one file.
- [X] T009 `write_file`, `replace_in_file` and `delete_file` append `DescribeOutside`'s sentence to a refusal when the path would have been readable through an attachment (FR-002).
- [X] T010 The five index tools answer an attachment path with "not indexed; one index describes the primary root" instead of the generic escape message (FR-006). Two of the five take a path (`search_code`, `code_context`); both resolve through `CodeIndexAccess.ResolvePath`, where the sentence lives, so the other three cannot drift from it.
- [X] T011 `ListRootsTool` (`list_roots`, `ToolSafety.ReadOnly`, empty object schema) rendering `RootListing.Describe(workspace)` (FR-007).
- [X] T012 Register `list_roots` in `AddTools` and correct the "fourteen workspace tools" remark in `ServiceCollectionExtensions`, plus the registry-size assertion in the tool registry tests. No such assertion existed — `ToolRegistryTests` pins order, duplicates and lookup, never a count — so the remark was the only place the number was written down, and it now says fifteen.
- [X] T013 `RootListing.Describe(Workspace)` in Application: `primary` / `read-only` lines with resolved paths, shared by the tool and the command. `DescribeForConsole` renders the same lines with native separators for `/roots`.

## Phase 3 — the command line

- [X] T014 `CliOptions.AdditionalRoots`, `--add-root <path>` (repeatable, `run` and `chat` only, usage error elsewhere), and `SHARPCOREAGENT_ADD_ROOTS` split on `Path.PathSeparator`, command line first (FR-008).
- [X] T015 `SharpCoreAgentOptions.AdditionalRoots` and `CreateWorkspace()` applying them, so the CLI's workspace and the index registration's workspace are the same value (FR-001).
- [X] T016 `CliHost.TryOpenWorkspace` applies the attachments and keeps mapping a bad root to exit 2 with the message already printed (FR-010). A resumed `chat` re-applies the new invocation's roots to the recorded workspace root, so nothing is inherited from the file (FR-009).
- [X] T017 `chat` answers `/roots`; the banner names the attachment count; `--quiet` still silences the banner (FR-011).

## Phase 4 — the evidence

- [X] T018 `WorkspaceTests`: an attachment is readable, a relative path still lands in the primary root, an absolute path inside an attachment resolves with that attachment as owner, an absolute path inside nothing is refused, a duplicate root is dropped, a root nested under another is dropped.
- [X] T019 `PathSafetyTests`: a link inside an attachment that points outside every root is refused; a link inside an attachment that points into the primary root is accepted; an attachment containing a link to itself terminates inside the 64-deep cap; and the write path still refuses a link out of the primary root even when the target is an attachment.
- [X] T020 `FileToolTests`/`SearchTextToolTests`/`ListDirectoryToolTests`: each reads from an attachment, and `write_file`, `replace_in_file`, `delete_file` refuse the same path with the read-only sentence naming the root (FR-002). There is no `ListDirectoryToolTests` file in this suite — the listing cases live in `ToolTests` beside the other file tools — so the attachment case went there.
- [X] T021 `ListRootsToolTests`: primary only, primary plus two attachments, and the ordering of the result.
- [X] T022 `CliOptionsTests`: repeated `--add-root`, the environment variable, both together in order, a missing directory as a usage error, and `--add-root` rejected on `models`/`index`/`config`.
- [X] T023 `ChatLoopTests` for `/roots`, `InteractiveStartTests` for the banner line, and a `ProgramTests` case asserting exit 2 for a nonexistent root before any request.
- [X] T024 Confirm the existing suite passes untouched: no assertion about write confinement is edited, and `git diff --stat tests/` shows additions only.

## Phase 5 — the surface

- [X] T025 `HelpText`: `--add-root`, `SHARPCOREAGENT_ADD_ROOTS`, `/roots`, and the read-only sentence in the usage block.
- [X] T026 `README.md`: the flag, the read-only rule, `list_roots`, and the suite count the run printed.
- [X] T027 `specs/001-coding-agent-core/contracts/cli.md` and `contracts/tools.md` updated for the new flag and the fifteenth tool; this round's `contracts/cli-roots.md` becomes the reference.
- [X] T028 `PublicAPI.Unshipped.txt` for `Workspace.Attachments`, `HasAttachments`, `WithAttachments`, `TryResolveRead`, `IsInsideAny` and `DescribeOutside`. Only `SharpCoreAgent.Domain` ships an approved-API snapshot (see its `.csproj`); `PathSafety.TryResolveRead`, `ListRootsTool` and `RootListing` live in assemblies that deliberately have none, so there is nothing to list for them.
- [X] T029 `dotnet build .\SharpCoreAgent.slnx -c Release` with zero warnings, and `dotnet test` green: 799 tests, 0 warnings, 0 errors. The merge and this spec's status are the last step of the round.

## Deliberately not in this round

- [ ] T030 A writable attachment (`--add-root --writable`). Needs per-root approval and a per-root safety class; a specification of its own.
- [ ] T031 Indexing an attachment. Needs a second index or a composite one, a staleness story per root and a new meaning for `index verify`.
- [ ] T032 Persisting attachments in the settings file. Refused on principle (`spec.md`, "What this feature rejects"), so this line is a reminder rather than work.

