# Tasks: Language-neutral verification profiles

## Setup

- [x] T001 Add `VerificationProfile`, `IVerificationTool` and `VerificationGap`.
- [x] T002 Make `dotnet_verify` declare the built-in `.NET` profile and its extension set.

## Gate

- [x] T003 Bind profiles from the registered verifier tools.
- [x] T004 Track last edit and last verification per profile in `TurnState`.
- [x] T005 Gate act-mode turns on the profile gaps; leave plan and no-verifier hosts ungated.
- [x] T006 Keep the .NET detail and nudge byte-for-byte; add profile-aware detail and `VerifyNudgeFor`.
- [x] T007 Print the report detail for any profile in `ConsoleObserver`.

## Verification

- [x] T008 Add a fake Python verifier profile and prove: an edit is gated, a later pass closes it, and its wording
      names `python_verify`.
- [x] T009 Preserve the .NET tests, nudge text and profile assertions.
- [x] T010 Update README, contract, spec, plan and quickstart.
- [x] T011 Run Release build, full suite and recorded evaluation; record evidence.

## Evidence

- Built-in profile: `dotnet` / `.NET` / `dotnet_verify`, with the ten existing extensions.
- New profile cases: Python edit without verification is unverified and names `python_verify`; a later Python
  verification closes only that profile's gap.
- The default registry's prompt and token surface is unchanged, so the recorded evaluation baseline remains identical.
- Complete Release suite: **1,607/1,607**, zero warnings. Evaluation: 3/3, 57,493 input / 260 output, no semantic
  regressions.

## Out of scope

Built-in Python/JavaScript tools, command discovery, per-profile CLI settings, globs beyond suffix matching, and changes
to `dotnet_verify`'s command or parser.
