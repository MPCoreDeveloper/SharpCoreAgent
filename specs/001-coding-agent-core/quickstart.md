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
dotnet test .\SharpCoreAgent.slnx -c Release
```

Expected: `Build succeeded`, `0 Warning(s)`, `0 Error(s)`, all tests pass, wall clock well under a
minute, no outbound connection (the test project has no credential and no live URL). `dotnet test`
takes the solution or `--project tests\SharpCoreAgent.Tests`, not both: the two together are rejected
with "Specify either the project, solution, directory, or test modules option."

## 2. Prove the loop without credentials

```powershell
dotnet run --project src\SharpCoreAgent.Cli -- run "list the files in this repository" --provider demo --approval readonly
```

Expected: `TurnStarted demo:demo-scripted`, one `→ list_directory({})`, a `← list_directory: ok`
line, a streamed summary naming files that really exist, and an exit code of 0 with a transcript
written under `.sharpcoreagent/sessions/`. When the index is enabled (the default) the run starts with
one `index · fresh (N files, M passages, …)` line before the first tool call.

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

Expected: the four `deepseek:` models (with `tools=no` on `deepseek-reasoner`), the four `grok:`
models, `openai-compatible:gpt-4o-mini`, and `demo:demo-scripted`; and — for every provider whose key
is unset — a note reading `note: set XAI_API_KEY or GROK_API_KEY to use grok` (the names are the
provider's own, joined with `or`).

Interactive switch (**LIVE** only for the turn; switching and `/exit` spend nothing):

```powershell
dotnet run --project src\SharpCoreAgent.Cli -- chat --provider demo
> hello                                  # answered by demo:demo-scripted
> /model grok:grok-4                      # switched, transcript preserved
> /usage                                  # demo and grok figures listed separately
> /cost                                   # priced from the dated table; `no price known` where it must be
> /exit
```

## 5. LIVE: a real DeepSeek turn

```powershell
$env:DEEPSEEK_API_KEY = "<your key>"
dotnet run --project src\SharpCoreAgent.Cli -- run "create src/demo/Hello.cs printing 'hi' and run the build" --provider deepseek --model deepseek-flash --approval ask
```

Expected: streamed reasoning-free text, a `write_file` approval prompt (answer `y`), a
`run_shell` approval prompt, `Build succeeded` echoed from the tool, a final summary listing
`src/demo/Hello.cs` and the build command, and exit code 0.

## 6. LIVE: the same prompt on Grok

```powershell
$env:XAI_API_KEY = "<your key>"
dotnet run --project src\SharpCoreAgent.Cli -- run "list the top-level directories" --provider grok --model grok-4.7 --approval readonly
```

Expected: identical tool behaviour, `grok:grok-4.7` in the report — proof the port is not
DeepSeek-shaped.

## 7. Prove the budgets and the guard rails

```powershell
# iteration budget: the demo lists once and then answers, so one call is one too few
dotnet run --project src\SharpCoreAgent.Cli -- run "keep listing files" --provider demo --max-iterations 1
$LASTEXITCODE   # 4, report reads "the iteration budget of 1 model calls ran out…"

# path escape is refused, not executed
dotnet run --project src\SharpCoreAgent.Cli -- run "read ../../Windows/win.ini" --provider demo --approval allow

# missing credential names the variable, and never prints a value
$env:DEEPSEEK_API_KEY = ""
dotnet run --project src\SharpCoreAgent.Cli -- run "hi" --provider deepseek
$LASTEXITCODE   # 3, message reads "deepseek needs a credential and none is set for deepseek:deepseek-chat. Set DEEPSEEK_API_KEY…"
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
| `'openai:gpt-4o-mini' is not a fallback this build can use` | `--fallback` named a provider key this build does not serve, or omitted the `provider:` prefix | `sharpcoreagent models` lists the provider keys; the form is `--fallback grok:grok-4.7` |
| exit 3, `grok needs a credential and none is set for grok:grok-4.7` | `--fallback` names a provider whose key is unset | set the key, or name a fallback you can actually reach — a fallback that cannot start is worse than none |
| tool refuses a path inside the workspace | symlink resolves outside | move the file into a real directory inside the root |
| `run_shell` reports a timeout | command exceeded 120 s | raise `timeout_seconds` (max 600) or run the build yourself |
