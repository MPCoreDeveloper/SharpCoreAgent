# Implementation Plan: Language-neutral verification profiles

**Feature**: `037-verification-profiles`
**Status**: implemented 2026-11-04

## Architecture

Application owns the profile vocabulary because it owns the turn loop:

- `VerificationProfile(Name, DisplayName, Extensions)` matches by path suffix.
- `IVerificationTool` has one member: `Profiles`.
- `VerificationGap(Profile, ToolName)` is what the loop reports and phrases.

`VerifyGate.Bind` walks the registry, keeps tools that implement `IVerificationTool`, and produces
`VerificationBinding(ToolName, Profile)` records. The binding is fixed when `CodingAgent` is constructed, because the
registry is already complete by then and a turn must not see a profile move mid-answer.

`DotnetVerifyTool` implements the interface with one `dotnet`/`.NET` profile whose extensions are the old
`VerifyGate` list. Its build/test behavior, parser, process host and prompt wording are untouched.

## Per-turn state

`CodingAgent.TurnState` holds:

```text
lastEdit[profile.Name]          index of the last effect that matched the profile
lastVerification[profile.Name]  index of the last successful result from the owning tool
```

`Add` records both in result order. A verifier result after the edit raises the verification index; an edit after the
verify raises the edit index. `UnverifiedGaps` returns one `VerificationGap` per profile whose edit is newer than its
verification or which has no verification. Duplicate profile names are ignored after the first binding, and the profile
contract requires names to be unique.

`VerifyGate.IsRequired` is then simply act mode plus a non-empty gap list. A registry with no `IVerificationTool` has no
bindings and therefore no gap, which is the old "host without dotnet_verify is not gated" rule generalized.

## Wording

`VerifyGate.Detail` returns the old `UnverifiedDetail` for the exact `dotnet`/`dotnet_verify` binding and a sentence
naming the display names and tool names for every other set. `PromptLibrary.VerifyNudgeFor` does the same: the single
.NET gap returns the existing `VerifyNudge` constant; another profile gets a nudge that names its language and tool.

`ConsoleObserver` prints the report's detail with the warning marker when `IsUnverified` is set, and falls back to the
legacy sentence for a synthetic report that has no detail. The normal detail line is suppressed for an unverified report
so the message is not printed twice.

## Why profiles live on tools

The tool is the only component that knows how to run a verifier. Putting the extensions there keeps `VerifyGate` free of
language names and lets a language adapter add a profile without changing the turn loop. It also makes the opt-in
explicit: a verifier that is not registered cannot gate anything, just as `dotnet_verify` being absent never gated a
host before this round.

## Complexity and limits

- A profile is a suffix list, not a project graph; a `.cs` file outside a .NET project still triggers the .NET nudge,
  which is the existing conservative behavior.
- A tool with several profiles has one result name for all of them; the current design assumes one verifier tool owns
  all profiles it declares.
- The built-in registry still ships only the .NET profile. Python/JS tools and their commands are out of scope.
- No prompt or token change occurs for the default registry, so the recorded evaluation baseline does not move.
