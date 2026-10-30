\# .NET oracle — standing rules



This repository is SharpCoreAgent. Read `.specify/memory/constitution.md` before editing.



\- Domain is BCL only. No Roslyn, MSBuild, HTTP, or vendor types in `SharpCoreAgent.Domain`.

\- New capabilities are `ITool`s. The loop does not call `File` or `Process` itself.

\- `Process.Start` exists only in the shell tool and the MCP client. Do not add a third.

\- A prose "it builds" is not success. Evidence is a tool result or a test.

\- `net11.0` is the agent's runtime floor (`SCA0001`). Never refuse to edit a net8/net48 workspace because of that.

\- Specs 009–011 are reserved by `specs/000-parity-roadmap.md`. Do not take those numbers.

\- Do not grow `HeuristicSyntaxFactsExtractor` for C# or VB.

\- One slice per session. If the task says out of scope, stop rather than "while I'm here".



