# Quickstart: Verification profiles

The default build ships one profile, declared by `dotnet_verify`:

```text
dotnet_verify  →  .NET  →  .cs .vb .fs .csproj .vbproj .fsproj .props .targets .sln .slnx
```

A turn that edits one of those paths is gated exactly as before: the loop asks for `dotnet_verify`, and after two
unanswered nudges the report says `IsUnverified`. The sentence still reads:

```text
the turn edited .NET source but dotnet_verify did not pass after that edit, so the change is unverified.
```

## Adding a profile

A language adapter or host tool implements `IVerificationTool`:

```csharp
public sealed class PythonVerifyTool : ITool, IVerificationTool
{
    public ToolDefinition Definition { get; } = new(
        "python_verify",
        "Run the Python verification profile.",
        """{"type":"object"}""");

    public ToolSafety Safety => ToolSafety.Destructive;

    public IReadOnlyList<VerificationProfile> Profiles { get; } =
        [new("python", "Python", [".py", ".pyi"])];

    // ExecuteAsync runs the project's checker and returns a failed result when it fails.
}
```

Register the tool beside `dotnet_verify`. No change to `VerifyGate`, `CodingAgent` or the prompt is needed.

- Editing `app.py` now creates a Python gap.
- `python_verify` must pass after that edit to close it.
- The nudge names `python_verify`, not `dotnet_verify`.
- A .NET verification does not close the Python gap.
- A host with no `IVerificationTool` is not gated.

## What this slice does not do

It does not choose commands for Python or Node. A verifier tool has to decide how its project is checked — the profile
only says which files it owns and what name the gate should wait for. Built-in `python_verify`/`node_verify` tools are a
later language slice.
