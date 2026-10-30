# Plan — 012 compiler oracle

## Shape

One tool, one parser, one gate in the loop, and one new flag's worth of nothing.

```text
PromptLibrary.SystemPrompt (rule 5)  --> "verify with dotnet_verify"
                     |
model calls dotnet_verify(kind=build) --> DotnetVerifyTool
                     |                        |
                     |        ShellProcessRegistry.Start("dotnet", build <project>)
                     |                        |
                     |              DrainAsync -> DotnetVerifyReportParser.Parse
                     |                        |
                     +---------- ToolResult (report header + bounded log) ----+
                                                                               |
CodingAgent.AdvanceAsync: a text answer arrives                                 |
    VerifyGate.IsRequired(session, tools, lastEdit, lastVerify)? <-- ToolResult.ToolName/.IsError/.Effects
        yes, and nudges < 2  -> append PromptLibrary.VerifyNudge, loop again
        yes, and nudges = 2  -> Completed + AgentReport.IsUnverified + Detail
        no                   -> Completed, exactly as today
```

Nothing learns a new flag: no `CliOptions`, no `HelpText`, no settings key, no environment variable. The
tool is registered by `AddTools` beside `run_shell`, which means it ships in every build that could call it
and the prompt cannot name a tool the registry does not have.

## The tool

`DotnetVerifyTool` in `Infrastructure/Tools`, beside the other filesystem and shell tools:

```csharp
public DotnetVerifyTool();                                        // "dotnet", a private registry
public DotnetVerifyTool(ShellProcessRegistry processes);          // the container's shared host
public DotnetVerifyTool(string dotnetFileName, ShellProcessRegistry processes);   // a test's stand-in
```

`ExecuteAsync` is one path:

1. read `kind`, `project` and `timeout_seconds` through `ToolArgumentReader`, so a garbage call is an
   argument error and not a `dotnet` that runs with the wrong verb;
2. resolve `project` through `PathSafety.TryResolve` and require it to exist, so the compiler is never
   pointed at a path outside the root;
3. start `dotnet` with `ArgumentList` — no shell, no quoting — through `ShellProcessRegistry`, with the
   credential filter `run_shell` already uses and `DOTNET_CLI_UI_LANGUAGE=en`;
4. wait, kill the tree on timeout or cancellation, drain, parse, render.

The answer is a labelled header followed by the log, at the same width as `list_roots` and `fetch_url`:

```text
kind         build
project      src/App/App.csproj
exit code    1
result       failed
warnings     2
duration     3.4s
summary      Build FAILED.
errors       1
  1. src/App/Program.cs(7,9): error CS0103: The name 'Foo' does not exist

<the log, at most 64 KiB>
```

`result       failed` and `IsError = true` are the same fact said twice, which is the point: the model reads
the header, the loop reads the flag, and the gate cannot be satisfied by a build that did not pass.

## The parser

`DotnetVerifyReportParser.Parse(int exitCode, string output)` is a pure function over the log, and
`DotnetVerifyReport` is a plain record of what it found — no policy, no second opinion:

- `Succeeded` = exit code 0, no `Build FAILED.`/`Failed!` marker and no `error XXnnnn:` line. The exit code
  is the authority; the markers catch a `dotnet test` that reported a failure and still exited 0;
- `Errors` = the diagnostic lines, deduplicated (MSBuild prints each one inline and again in the summary)
  and capped at 25, because a header is a summary and 4 000 errors are not;
- `WarningCount` = the `N Warning(s)` total when the log printed one, otherwise the count of
  `warning CSnnnn:` lines;
- `TestsPassed`/`TestsFailed` = the last `Failed: n, Passed: m` on a `Passed!`/`Failed!` line;
- `Summary` = the first `Build succeeded.`, `Build FAILED.`, `Passed!` or `Failed!` line, else
  `exit code n`.

Localisation is the reason the tool sets `DOTNET_CLI_UI_LANGUAGE=en`: a Dutch SDK prints `Fout(en)` and
`0 Waarschuwing(en)`, and a parser that matched only English would report every build on that machine as an
unnamed failure.

## The gate

`VerifyGate` (internal, `Application`) answers three questions and owns no state:

```csharp
static bool Verifies(ToolResult result)        // ToolName == ToolNames.DotnetVerify && !IsError && !IsRefused
static bool EditsDotnetSource(ToolResult r)    // any Created/Modified/Deleted effect on a .NET extension
static bool IsRequired(AgentSession, IToolRegistry, int lastEdit, int lastVerification)
```

`CodingAgent.TurnState` records two indices while it collects results — the last .NET edit and the last
passing verify — and the gate is open when `lastVerification < lastEdit`. Order rather than presence is what
stops a verify that ran before the edit from closing the gate, and `-1` for both means a turn that touched
nothing is never gated.

Three passthroughs, each deliberate:

| Case | Why it is not gated |
|---|---|
| Plan mode | It cannot write, so `lastEdit` stays `-1`; the check is explicit anyway, so a future plan-mode tool cannot quietly introduce a gate |
| No `dotnet_verify` in the registry | A host that never had the tool has not lost anything; refusing its turns would be a behaviour change in a build that did not ask for one |
| No .NET edit | A turn that read three files and answered a question owes the compiler nothing |

The nudge is a `ChatMessage.User` carrying `PromptLibrary.VerifyNudge`, appended to the transcript rather
than injected into the system prompt, so the model's next request sees it *after* the tool result that

## The console and the report

`AgentReport.IsUnverified` is the one domain addition: a bool on a record that already carries `Outcome`,
`Detail` and `Fallbacks`, set only on an `Completed` report the gate could not close. The console prints one
extra line under the outcome when it is true —

```text
✻ completed · 6 iteration(s) · 1.2s · 12,345 in / 678 out
  ⚠ unverified: no passing dotnet_verify ran after the last .NET edit.
```

— and the JSON report gains `"unverified": true`. No exit code moves: `Completed` is `0` either way, because
the developer's remedy is the line they are reading, and a script that treated this as a failure would
re-run turns that already did their work.

## Complexity Tracking

| Decision | Cost | Why it is worth it |
|---|---|---|
| A second tool that starts a process | one more call site of the existing `ShellProcessRegistry`, not a third `Process.Start` | the compiler is the oracle; a rule without a tool that runs it is a suggestion |
| A parser instead of "the exit code is the answer" | one class, one record, eight tests | an exit code cannot tell a model *which* error, and a header cannot be re-derived from a 64 KiB log |
| `ToolNames` in the domain | a static class with one constant, two approved-API lines | the alternative is `Application` referencing `Infrastructure` for a string, or two literals that can drift |
| Two indices in `TurnState` | four lines in `Add` and two properties | comparing order is the only way "verified" cannot be satisfied by a stale result |
| A nudge message rather than a system-prompt edit | one constant, one `session.Add` | the instruction has to arrive after the tool result that earned it; a system prompt is read once |
| A `ChatMessage.User` in the middle of a tool round | one more message per nudge | providers already accept a user turn after tool results, and the transcript shows who asked |
| `DOTNET_CLI_UI_LANGUAGE=en` for the child | one environment override, for one tool | a parser that only reads English and a SDK that speaks Dutch produce a report with no summary in it |
| No `--no-verify` flag | a turn in `readonly` approval can never be verified | the honest answer is the `⚠ unverified` line, not a switch that says "trust me" |

## What this deliberately does not reuse

`HeuristicSyntaxFactsExtractor` is not touched and not grown. Deciding whether an edit *broke* the build is
the compiler's job in this round; a heuristic in the indexing layer that guessed at it would be a second,
worse oracle, and the constitution says not to grow that type for C# or VB.

## As built

Landed as planned, with the notes a reader would otherwise have to rediscover:

1. **The gate is satisfied by a *passing* result, not by any run.** `DotnetVerifyTool` returns
   `IsError = true` for a build that failed, so a failed verify leaves the gate open and the model is asked
   to fix it. A failing build is still data: the diagnostics, the log and the summary are all in the result.
2. **`PromptLibrary` gained no new parameter.** The base `SystemPrompt` rule 5 now names `dotnet_verify`, and
   `dotnet_verify` is registered unconditionally by `AddTools`, so there is no build in which the prompt
   names a tool the registry does not have — the same guarantee `WebRule` earns with a parameter, bought here
   with an unconditional registration.
3. **`VerifyNudge` is what the loop appends, not what the prompt ships.** A model that never edits .NET
   source never sees it.
4. **The credential filter moved out of `RunShellCommandTool` into `ChildEnvironment`.** The verifier starts a
   process too, and one implementation of "no key in the child's environment" beats two that can drift. The
   shell tool's behaviour is unchanged, and its existing test still pins the `*_API_KEY` case.
5. **The child is told `DOTNET_CLI_UI_LANGUAGE=en`.** Without it, a machine with a non-English SDK produces a
   log whose summary line and warning total the parser cannot read, which would report every failing build as
   `exit code 1` — honest, but useless to the model the gate is asking. The developer's own locale is
   untouched; only the verification child is told.
6. **`ChatMessage.User` rather than a second system message for the nudge.** Providers already accept a user
   turn after tool results, and a developer who resumes the session reads the same sentence the model did.

Suite: 1,112 tests, 0 warnings, 0 errors on `dotnet build .\SharpCoreAgent.slnx -c Release`. One of them — `ARealBuildPassesAndThenFailsWithItsDiagnostics` — starts the real SDK, because the point of the tool is what the compiler does with a project.


caused it — and so a developer reading `/resume` sees what the harness asked for.
