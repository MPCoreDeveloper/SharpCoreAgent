# Contract: Verification profiles

**Feature**: `037-verification-profiles`

## Application seam

```csharp
public sealed record VerificationProfile(
    string Name,
    string DisplayName,
    IReadOnlyList<string> Extensions)
{
    public bool Matches(string path);
}

public interface IVerificationTool
{
    IReadOnlyList<VerificationProfile> Profiles { get; }
}

public sealed record VerificationGap(VerificationProfile Profile, string ToolName);
```

- `Name` is stable and unique across registered verifier tools.
- `DisplayName` is what the nudge and report show.
- `Extensions` are suffixes with a leading dot; matching is case-insensitive.
- A tool's `ITool.Definition.Name` is the tool named in `VerificationGap.ToolName`.

## Built-in .NET profile

`DotnetVerifyTool` implements `IVerificationTool`:

```text
Name          dotnet
DisplayName   .NET
ToolName      dotnet_verify
Extensions    .cs .vb .fs .csproj .vbproj .fsproj .props .targets .sln .slnx
```

Its build/test schema, process execution, report parser and prompt text are unchanged.

## Turn rule

For every registered binding:

```text
lastEdit[profile]          = index of the last Created/Modified/Deleted effect whose target matches profile.Extensions
lastVerification[profile]  = index of the last non-error, non-refused result named binding.ToolName
```

A profile is a gap when it has an edit and either no verification or a verification index lower than the edit index.

- Act mode with at least one gap → the loop adds `PromptLibrary.VerifyNudgeFor(gaps)` and continues.
- After `CodingAgent.MaxVerificationNudges` unanswered nudges → completes with `AgentReport.IsUnverified = true` and
  `AgentReport.Detail = VerifyGate.Detail(gaps)`.
- Plan mode → no gate.
- No `IVerificationTool` in the registry → no bindings, no gaps, no gate.

## Wording

The single `dotnet`/`dotnet_verify` gap returns exactly:

```text
the turn edited .NET source but dotnet_verify did not pass after that edit, so the change is unverified.
```

and `PromptLibrary.VerifyNudge` exactly. Any other gap names its display language and tool, for example:

```text
the turn edited Python source but python_verify did not pass after that edit, so the change is unverified.
```

## Invariants

- A verification before the edit does not close the gap.
- A failed or refused verifier result does not close the gap.
- One profile's verifier does not close another profile's gap.
- The default .NET registry produces the same prompt, nudge and evaluation tokens as before.
- Adding a profile requires registering a tool; it cannot be done from the model's arguments.
