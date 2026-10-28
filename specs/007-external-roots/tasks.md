# Tasks — 007 external roots

Legend: `[X]` implemented and covered by tests in this change; `[ ]` not started, kept here so the
next pass has a real list rather than a memory. Nothing below is `[X]` yet: this file is the plan of
record for the round, written before the first edit.

## Phase 1 — the workspace carries the roots

- [ ] T001 `Workspace` gains `Attachments` (`IReadOnlyList<Workspace>`), `HasAttachments`, and `WithAttachments(IEnumerable<string>)`, which normalises each root through `Create`, drops duplicates, drops a root equal to or nested under the primary root, and returns a new value. A root that does not exist keeps throwing `DirectoryNotFoundException` (FR-001, FR-010).
- [ ] T002 `Workspace.TryResolveRead(path, out fullPath, out owner, out error)`: relative resolves against the primary root only; absolute resolves against the first root that contains it; everything else is refused with a message naming the primary root, the attachment count and `list_roots` (FR-003, FR-004).
- [ ] T003 `Workspace.IsInsideAny(path)` for the physical check, and `IsInside`/`TryResolve` left byte-for-byte as they are (FR-002, FR-003).
- [ ] T004 `Workspace.DescribeOutside(path)` returning the read-only-attachment sentence or `null`, so the write tools can explain a refusal without re-wording `TryResolve`.
- [ ] T005 `PathSafety.TryResolveRead` built on the existing `ResolvePhysical` walk and `IsInsideAny`, so link handling has exactly one implementation (FR-005).

## Phase 2 — the tools

- [ ] T006 `read_file` resolves through `TryResolveRead` and prints the path relative to whichever root answered it, not an absolute path.
- [ ] T007 `list_directory` resolves through `TryResolveRead`; its listing is unchanged in shape, so the model needs no new parser.
- [ ] T008 `search_text` resolves through `TryResolveRead` and keeps its existing result and character ceilings.
- [ ] T009 `write_file`, `replace_in_file` and `delete_file` append `DescribeOutside`'s sentence to a refusal when the path would have been readable through an attachment (FR-002).
- [ ] T010 The five index tools answer an attachment path with "not indexed; one index describes the primary root" instead of the generic escape message (FR-006).
- [ ] T011 `ListRootsTool` (`list_roots`, `ToolSafety.ReadOnly`, empty object schema) rendering `RootListing.Describe(workspace)` (FR-007).
- [ ] T012 Register `list_roots` in `AddTools` and correct the "fourteen workspace tools" remark in `ServiceCollectionExtensions`, plus the registry-size assertion in the tool registry tests.
- [ ] T013 `RootListing.Describe(Workspace)` in Application: `primary` / `read-only` lines with resolved paths, shared by the tool and the command.

## Phase 3 — the command line

- [ ] T014 `CliOptions.AdditionalRoots`, `--add-root <path>` (repeatable, `run` and `chat` only, usage error elsewhere), and `SHARPCOREAGENT_ADD_ROOTS` split on `Path.PathSeparator`, command line first (FR-008).
- [ ] T015 `SharpCoreAgentOptions.AdditionalRoots` and `CreateWorkspace()` applying them, so the CLI's workspace and the index registration's workspace are the same value (FR-001).
- [ ] T016 `CliHost.TryOpenWorkspace` applies the attachments and keeps mapping a bad root to exit 2 with the message already printed (FR-010).
- [ ] T017 `chat` answers `/roots`; the banner names the attachment count; `--quiet` still silences the banner (FR-011).

## Phase 4 — the evidence

- [ ] T018 `WorkspaceTests`: an attachment is readable, a relative path still lands in the primary root, an absolute path inside an attachment resolves with that attachment as owner, an absolute path inside nothing is refused, a duplicate root is dropped, a root nested under another is dropped.
- [ ] T019 `PathSafetyTests`: a link inside an attachment that points outside every root is refused; a link inside an attachment that points into the primary root is accepted; an attachment containing a link to itself terminates inside the 64-deep cap.
- [ ] T020 `FileToolTests`/`SearchTextToolTests`/`ListDirectoryToolTests`: each reads from an attachment, and `write_file`, `replace_in_file`, `delete_file` refuse the same path with the read-only sentence naming the root (FR-002).
- [ ] T021 `ListRootsToolTests`: primary only, primary plus two attachments, and the ordering of the result.
- [ ] T022 `CliOptionsTests`: repeated `--add-root`, the environment variable, both together in order, a missing directory as a usage error, and `--add-root` rejected on `models`/`index`/`config`.
- [ ] T023 `ChatLoopTests` for `/roots`, `InteractiveStartTests` for the banner line, and a `ProgramTests` case asserting exit 2 for a nonexistent root before any request.
- [ ] T024 Confirm the existing suite passes untouched: no assertion about write confinement is edited, and `git diff --stat tests/` shows additions only.

## Phase 5 — the surface

- [ ] T025 `HelpText`: `--add-root`, `SHARPCOREAGENT_ADD_ROOTS`, `/roots`, and the read-only sentence in the usage block.
- [ ] T026 `README.md`: the flag, the read-only rule, `list_roots`, and the suite count the run printed.
- [ ] T027 `specs/001-coding-agent-core/contracts/cli.md` and `contracts/tools.md` updated for the new flag and the fifteenth tool; this round's `contracts/cli-roots.md` becomes the reference.
- [ ] T028 `PublicAPI.Unshipped.txt` for `Workspace.Attachments`, `HasAttachments`, `WithAttachments`, `TryResolveRead`, `IsInsideAny`, `DescribeOutside`, `PathSafety.TryResolveRead`, `ListRootsTool` and `RootListing`.
- [ ] T029 `dotnet build .\SharpCoreAgent.slnx -c Release` with zero warnings and `dotnet test` green; then the merge, and this spec's status moved to `implemented`.

## Deliberately not in this round

- [ ] T030 A writable attachment (`--add-root --writable`). Needs per-root approval and a per-root safety class; a specification of its own.
- [ ] T031 Indexing an attachment. Needs a second index or a composite one, a staleness story per root and a new meaning for `index verify`.
- [ ] T032 Persisting attachments in the settings file. Refused on principle (`spec.md`, "What this feature rejects"), so this line is a reminder rather than work.

