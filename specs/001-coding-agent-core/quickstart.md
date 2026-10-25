# Quickstart: Coding Agent Core

Runnable validation for `001-coding-agent-core`. Every step below works offline except the ones
marked **LIVE**, which spend money and are therefore not part of the automated suite.

## Prerequisites

- .NET SDK 11.0.100 with the .NET 11 runtime
- Windows (PowerShell 7) or Linux (bash)
- For **LIVE** steps only: `DEEPSEEK_API_KEY` and/or `XAI_API_KEY` in the environment

## 1. Build and test (offline, expected: 0 warnings)

```powershell
cd d:\repos\MPCoreDeveloper\SharpCoreAgent
dotnet build .\SharpCoreAgent.slnx -c Release
dotnet test .\SharpCoreAgent.slnx -c Release --project tests\SharpCoreAgent.Tests
```

Expected: `Build succeeded`, `0 Warning(s)`, `0 Error(s)`, all tests pass, wall clock under 60 s,
no outbound connection (the test project has no credential and no live URL).

## 2. Prove the loop without credentials

```powershell
dotnet run --project src\SharpCoreAgent.Cli -- run "list the files in this repository" --provider demo --approval readonly
```

Expected: `TurnStarted demo:demo-scripted`, one `→ list_directory({})`, a `← list_directory: ok`
line, a streamed summary naming files that really exist, and an exit code of 0 with a transcript
written under `.sharpcoreagent/sessions/`.

```powershell
$LASTEXITCODE   # 0
Get-ChildItem .sharpcoreagent\sessions | Select-Object -Last 1 | Get-Content | Select-Object -First 12
```

## 3. Prove a turn that needs a tool, then a refusal (offline)

```powershell
# ask the demo provider to write a file in read-only mode: the tool must be refused, the turn must survive
dotnet run --project src\SharpCoreAgent.Cli -- run "write hello to notes.txt" --provider demo --approval readonly
$LASTEXITCODE   # 0 — refusal is not failure
Test-Path .\notes.txt   # False
```

## 4. Prove provider selection and switching

```powershell
dotnet run --project src\SharpCoreAgent.Cli -- models
dotnet run --project src\SharpCoreAgent.Cli -- models --provider grok
```

Expected: `deepseek:deepseek-chat`, `deepseek:deepseek-reasoner  tools=no`, `grok:grok-4`,
`demo:demo-scripted`, and — when the key is unset — a note reading
`note: XAI_API_KEY is not set; grok runs will fail until it is`.

Interactive switch (**LIVE** for the second half):

```powershell
dotnet run --project src\SharpCoreAgent.Cli -- chat --provider demo
> hello                                  # answered by demo:demo-scripted
> /model grok:grok-4                      # switched, transcript preserved
> /usage                                  # demo and grok figures listed separately
> /exit
```

## 5. LIVE: a real DeepSeek turn

```powershell
$env:DEEPSEEK_API_KEY = "<your key>"
dotnet run --project src\SharpCoreAgent.Cli -- run "create src/demo/Hello.cs printing 'hi' and run the build" --provider deepseek --model deepseek-chat --approval ask
```

Expected: streamed reasoning-free text, a `write_file` approval prompt (answer `y`), a
`run_shell` approval prompt, `Build succeeded` echoed from the tool, a final summary listing
`src/demo/Hello.cs` and the build command, and exit code 0.

## 6. LIVE: the same prompt on Grok

```powershell
$env:XAI_API_KEY = "<your key>"
dotnet run --project src\SharpCoreAgent.Cli -- run "list the top-level directories" --provider grok --model grok-4 --approval readonly
```

Expected: identical tool behaviour, `grok:grok-4` in the report — proof the port is not
DeepSeek-shaped.

## 7. Prove the budgets and the guard rails

```powershell
# iteration budget
dotnet run --project src\SharpCoreAgent.Cli -- run "keep listing files" --provider demo --max-iterations 2
$LASTEXITCODE   # 4, report names "iterations"

# path escape is refused, not executed
dotnet run --project src\SharpCoreAgent.Cli -- run "read ../../Windows/win.ini" --provider demo --approval allow

# missing credential names the variable, and never prints a value
$env:DEEPSEEK_API_KEY = ""
dotnet run --project src\SharpCoreAgent.Cli -- run "hi" --provider deepseek
$LASTEXITCODE   # 3, message names DEEPSEEK_API_KEY
```

## 8. Resume a session

```powershell
dotnet run --project src\SharpCoreAgent.Cli -- chat --provider demo --session .sharpcoreagent\sessions\demo.json
> first message
> /exit
dotnet run --project src\SharpCoreAgent.Cli -- chat --provider demo --session .sharpcoreagent\sessions\demo.json
> /usage     # totals include the earlier turn
```

## 9. Secrets check

```powershell
Select-String -Path .sharpcoreagent\sessions\*.json -Pattern $env:DEEPSEEK_API_KEY   # no output
Select-String -Path .sharpcoreagent\sessions\*.json -Pattern "Authorization"        # no output
```

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| exit 3, `DEEPSEEK_API_KEY is not set` | credential missing | export the variable, or use `--provider demo` |
| exit 2, `unknown provider` | typo | `sharpcoreagent models` lists valid names |
| `model grok:grok-4 cannot honour this request` | `SupportsTools=false` on the selected model | pick a tool-capable model |
| tool refuses a path inside the workspace | symlink resolves outside | move the file into a real directory inside the root |
| `run_shell` reports a timeout | command exceeded 120 s | raise `timeout_seconds` (max 600) or run the build yourself |
