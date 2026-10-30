# Contract — `dotnet_verify` and the gate that reads it

The reference for round `012`. Everything here is a promise to a model, a script or a developer reading a
transcript; the tests in `tests/SharpCoreAgent.Tests/Tools/DotnetVerifyToolTests.cs`,
`Tools/DotnetVerifyReportParserTests.cs`, `Application/CodingAgentTests.cs` and
`Application/PromptLibraryTests.cs` hold it.

## Tool

```json
{
  "name": "dotnet_verify",
  "safety": "destructive",
  "parameters": {
    "type": "object",
    "properties": {
      "kind": { "type": "string", "enum": ["build", "test"],
                "description": "build compiles the workspace; test also runs it." },
      "project": { "type": "string",
                   "description": "Project or solution to narrow the run to, relative to the workspace root." },
      "timeout_seconds": { "type": "integer", "minimum": 1, "maximum": 900 }
    },
    "required": ["kind"],
    "additionalProperties": false
  }
}
```

- `ToolSafety.Destructive`: it starts a compiler and writes `obj/` and `bin/`. `readonly` approval refuses it,
  and `--mode plan` never advertises it.
- One effect on every run that finished: `CommandRun` with the command line, e.g.
  `dotnet build src/App/App.csproj`.
- A `kind` that is neither `build` nor `test` is a `ToolArgumentException` naming the two values.
- A `project` that escapes the root is a refusal; one that does not exist is a failure naming the path.
  Neither starts a process.

## Result, passing

```text
kind         build
project      (workspace root)
exit code    0
result       passed
warnings     0
duration     2.4s
summary      Build succeeded.

Determining projects to restore...
Restored src/App/App.csproj (in 96 ms).
  App -> src/App/bin/Debug/net11.0/App.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:02.31
```

## Result, failing

```text
kind         build
project      src/App/App.csproj
exit code    1
result       failed
warnings     1
duration     3.1s
summary      Build FAILED.
errors       1
  1. src/App/Program.cs(7,9): error CS0103: The name 'Foo' does not exist

<the log>
```

- `result` and `ToolResult.IsError` are the same fact: `passed` and `false`, or `failed` and `true`.
- `warnings` is the `N Warning(s)` total the log printed, or the count of `warning CSnnnn:` lines.
- `errors` lists at most 25 distinct diagnostic lines; the log below the header still contains every one.
- `tests` appears only for a run whose log carried a `Failed: n, Passed: m` summary:
  `tests        12 passed, 0 failed`.
- `truncated    yes` appears when the log was cut by the 64 KiB ceiling (`TextLimits.MaxCommandOutputChars`).
- One blank line separates the header from the log.
- A timeout is a failure naming the seconds and pointing at `run_shell` + `shell_output`; a `dotnet` that
  cannot be started is a failure naming the file and `PATH`.

## The gate

`CodingAgent` will not end a turn `Completed` when, in that turn:

1. mode is `act`; **and**
2. a tool result reported a `Created`, `Modified` or `Deleted` effect on a path ending in `.cs`, `.vb`,
   `.fs`, `.csproj`, `.vbproj`, `.fsproj`, `.props`, `.targets`, `.sln` or `.slnx` (case-insensitively);
   **and**
3. no `dotnet_verify` result that is neither `IsError` nor `IsRefused` was recorded *after* that edit; **and**
4. the registry the turn is running against contains `dotnet_verify`.

Then:

| State | What happens |
|---|---|
| first answer, nudges 0 → 1 | `PromptLibrary.VerifyNudge` is appended as a user message; the turn continues |
| second answer, nudges 1 → 2 | the same nudge is appended again; the turn continues |
| third answer | the turn completes with `IsUnverified = true` and `Detail` = "the turn edited .NET source but dotnet_verify did not pass after that edit, so the change is unverified." |

Everything else is unchanged: a turn that edited no .NET file, a plan-mode turn and a host without the tool
complete exactly as they did before this round, and `Completed` still exits `0`.

## Prompt

`PromptLibrary.SystemPrompt` rule 5 is the instruction and the tool in one sentence: *after changing code,
call `dotnet_verify` with kind build or test and read the report; `run_shell` is for the commands
`dotnet_verify` does not cover.* The `Reporting` block adds that no build or test may be claimed as passing
without a `dotnet_verify` result that says so.

`PromptLibrary.VerifyNudge` is the loop's sentence, not the prompt's: it ships only when a turn has earned
it. `PromptLibraryTests` pins both, and pins that the older sentence — proving a .NET edit with `run_shell` —
is gone.
