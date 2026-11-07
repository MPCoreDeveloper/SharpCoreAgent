# Feature Specification: framework cues (routes, tests, DI, database calls)

**Spec**: `058-framework-cues`
**Status**: landed
**Depends on**: `002-code-evidence-index` (the index and its retrieval tools). Reuses the same
`ICodeIndex` read port; adds no table.

## Problem

The index answers "where is X", "what does X call" and "what do the docs say about X", but it cannot
answer "what routes does this service expose", "which declarations are tests", "where are the
dependency-injection registrations" or "where does the code touch the database". Airk answers those
from framework cues (`airk-framework-cues`); SharpCoreAgent does not.

## What ships

A seventh read-only index tool, `find_cues`, and a read-port method `ICodeIndex.FindCuesAsync`, which
scan the passage text the build already stored for four kinds of framework cue and return cited
candidates:

| kind | examples the rules recognise |
|---|---|
| `route` | `app.get('/users')`, `@app.route`, `router.post`, `@GetMapping`, `.HandleFunc(` |
| `test` | `describe(`, `it(`, `test(`, `def test_`, `func Test`, `@Test` |
| `dependency_injection` | `Depends(`, `@Injectable`, `inject(`, `provide(` |
| `database_call` | `db.query(`, `session.execute(`, `cursor.fetchall(`, `conn.execute(` |

Each cue carries its `kind`, `path`, `line`, the written target (`app.get`), the captured string
argument when the pattern names one (`/users`), the enclosing declaration when the passage opens one,
and the matched line as a snippet.

## Requirements

- **FR-001** `find_cues` is `ReadOnly`, registered on the default surface beside the other index tools,
  and answers from `ICodeIndex`.
- **FR-002** `kind` is a closed list `any|route|test|dependency_injection|database_call`, default `any`;
  an unknown value is a `ToolArgumentException` before any scan runs.
- **FR-003** `text` optionally requires a term to appear in the target, the path or the captured
  detail, case-insensitively.
- **FR-004** Results are cited as `path:line`, ordered by path then line, de-duplicated per
  `(path, line, kind, target)`, capped by `max_results` (1–100, default 40) and the shared output
  budget.
- **FR-005** The scan runs over passages the build already stored; it adds no table, no column, no
  `analyzer_contract` change and forces no rebuild.
- **FR-006** A match is a *candidate*: a regular expression over source is a guess, not a resolved fact,
  and the tool and its documentation say so.

## Does not ship

- Build-time `facts` storage (a future round; it needs an `analyzer_contract` bump and a writer change).
- C#/VB-specific heuristics — the Roslyn oracle owns those languages and `HeuristicSyntaxFactsExtractor`
  is not touched.
- Resolution of a written target (`app.get`) to a symbol id; the cue names the written expression.
