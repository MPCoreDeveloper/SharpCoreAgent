# Feature Specification: Language-neutral verification profiles

**Feature**: `037-verification-profiles`
**Status**: implemented 2026-11-04

## Problem

The turn gate names one language. It recognizes `dotnet_verify` and a fixed list of .NET extensions, so a turn that
edits Python or JavaScript can end unverified even when the workspace has a verifier for it. A language adapter cannot
join the rule without editing `VerifyGate`, and the report always says `.NET` even when another verifier answered.

## What ships

- `VerificationProfile` — a stable name, developer-facing display name and extensions.
- `IVerificationTool` — a tool declares the profiles it verifies. The tool's own name is the name the gate and the
  nudge use.
- `VerifyGate` binds registered verifier tools to their profiles and tracks the last edit and last passing result
  independently for each profile.
- `dotnet_verify` declares the built-in `.NET` profile, including source and build-file extensions.
- `PromptLibrary.VerifyNudgeFor(...)` names the profile's language and verifier; the .NET result remains exactly the
  existing `VerifyNudge`.
- `AgentReport`/CLI wording can describe any profile, while synthetic reports with no detail keep the legacy .NET
  sentence.

The seam makes Python, JavaScript or a host's own verifier a tool registration plus a profile declaration. Shipping
those language tools is deliberately a later slice; this round supplies the gate and the proof that another profile
works.

## User stories

1. **A Python verifier gates Python edits.** A host registers a `python_verify` tool with `.py`; editing `app.py` and
   ending without calling it is marked unverified, and the nudge names `python_verify`.
2. **A passing Python verifier closes its own gap.** It does not close a .NET gap, and a .NET verifier does not close a
   Python gap.
3. **The .NET experience is unchanged.** `dotnet_verify` still gates C#/VB/F# project files and still produces the old
   `.NET` report sentence and nudge.
4. **A host without verifiers is not gated.** No registered `IVerificationTool` means no profile and no nudge.
5. **Mixed-language turns report both gaps.** A turn that edits `.cs` and `.py` and verifies only one says so and names
   the missing tool.

## Functional requirements

- **FR-001**: `VerificationProfile` contains a unique `Name`, a `DisplayName` and a non-empty set of extensions; matching
  is case-insensitive and uses the path suffix.
- **FR-002**: `IVerificationTool.Profiles` declares one or more profiles; the binding is the tool's
  `ToolDefinition.Name` plus the profile.
- **FR-003**: `VerifyGate.Bind` reads only the registered tool registry. A tool that does not implement
  `IVerificationTool` contributes no profile.
- **FR-004**: For each profile, a successful, non-refused result from its owning tool after the last matching edit closes
  that profile's gap. A failure, refusal, or result before the edit does not.
- **FR-005**: `TurnState` tracks edit and verification indices per profile name; `UnverifiedGaps` returns profiles with
  no later verification.
- **FR-006**: Act mode with at least one gap is gated; plan mode and a registry with no verifier profile are not.
- **FR-007**: `dotnet_verify` implements `IVerificationTool` and declares profile `dotnet`, display `.NET`, with the
  existing source/build extensions.
- **FR-008**: The .NET nudge and report sentence remain byte-for-byte unchanged. Another profile's nudge names its
  display language and tool, and its report detail does the same.
- **FR-009**: A turn with gaps receives at most `CodingAgent.MaxVerificationNudges` nudges, then completes with
  `IsUnverified` and the profile-aware detail.
- **FR-010**: No process, network, tool count or prompt text changes for the default .NET registry.

## Non-goals

- Shipping built-in `python_verify` or `node_verify` tools and auto-detecting their commands. The profile seam is the
  contract they will implement; command choice and project layout belong to those tools.
- Changing `dotnet_verify`'s build/test arguments, report parser or command host.
- A settings-file or CLI flag for profiles. A profile is declared by a registered tool, not typed by the model.
- Per-profile approval, custom file globs beyond extension suffixes, version-specific profiles, or language server
  probing.

## Success criteria

- The built-in .NET profile matches `.cs`, `.csproj` and the other current extensions, and not `.py`.
- A fake Python verifier profile gates a `.py` edit before its tool runs, closes the gate when it runs after the edit,
  and produces a Python-specific detail and nudge.
- The .NET gate tests and sentences remain green without changing their expected text.
- A registry with no verifier tool is not gated.
- Release build, full suite and recorded evaluation remain green.
