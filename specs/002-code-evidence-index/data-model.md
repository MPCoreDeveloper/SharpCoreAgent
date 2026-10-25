# Data Model: Code Evidence Index (SharpAriadne)

**Feature**: `002-code-evidence-index` | **Date**: 2026-10-24

Two models live here and they are deliberately separate:

1. **The Domain model** (`SharpCoreAgent.Domain.Index`) — what a host and a tool see. Pure BCL, no storage
   vocabulary, snapshot-approved.
2. **The storage model** (SharpCoreDB tables) — what `SharpCoreAgent.Indexing` writes. Nothing outside that
   project may name a table or a column.

The mapping between them is one-directional: the adapter projects rows onto records, and no record has a
public `Id`.

## 1. Domain vocabulary

```csharp
namespace SharpCoreAgent.Domain.Index;

/// <summary>How much detail the index keeps. Lean is the default and the supported tier.</summary>
public enum IndexTier { Lean, Rich }

/// <summary>Where passage and symbol vectors come from.</summary>
public enum EmbedderKind { Local, Provider, Off }

/// <summary>How vectors are stored at rest.</summary>
public enum VectorQuantization { None, Int8, Binary }

/// <summary>What the index believes about itself right now.</summary>
public enum IndexFreshness { Missing, Building, Partial, Stale, Fresh, Corrupt }

/// <summary>Direction of a reference query.</summary>
public enum ReferenceDirection { Callers, Callees, Both }

/// <summary>Which retrieval leg produced (or helped produce) a block.</summary>
public enum EvidenceLeg { Lexical, Vector, Graph, Symbol, Path }
```

`IndexFreshness` is the whole health story in one value: `Missing` (no database), `Building` (another
process holds the writer lock), `Partial` (a budget cut the build short), `Stale` (files changed since
the build), `Fresh`, `Corrupt` (opened and rejected, rebuild pending).

## 2. Request and result records

```csharp
/// <summary>One search request, already validated.</summary>
public sealed record CodeQuery
{
    public required string Text { get; init; }
    public string? Path { get; init; }        // workspace-relative file or directory
    public string? Kind { get; init; }        // any | class | ... | config
    public int K { get; init; } = 8;          // 1..12
    public int Hops { get; init; } = 1;       // 0..3

    public static CodeQuery Create(string text, string? path = null, string? kind = null, int k = 8, int hops = 1);
}

/// <summary>One citable block: the unit of every retrieval answer.</summary>
public sealed record CodeEvidence
{
    public required string Path { get; init; }        // workspace-relative, '/' separators
    public required int StartLine { get; init; }      // 1-based, inclusive
    public required int EndLine { get; init; }        // 1-based, inclusive
    public required string Sha8 { get; init; }        // first 8 hex chars of the passage's SHA-256
    public required string Text { get; init; }        // the passage itself
    public string Kind { get; init; } = "chunk";
    public string? Symbol { get; init; }              // qualified name of the declaration it holds
    public double Score { get; init; }
    public IReadOnlyList<EvidenceLeg> Legs { get; init; } = [];
    public bool IsStale { get; init; }
    public string? Relation { get; init; }            // calls | called_by | inherits | ... | declares
}

/// <summary>One declaration.</summary>
public sealed record CodeSymbolRef
{
    public required string Key { get; init; }             // Namespace.Type.Member#kind
    public required string Name { get; init; }
    public required string QualifiedName { get; init; }
    public required string Kind { get; init; }
    public required string Path { get; init; }
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
    public string? Signature { get; init; }
    public string? Doc { get; init; }
    public int Callers { get; init; }
    public int Callees { get; init; }
    public string? Community { get; init; }               // stable label, e.g. "c12"
}
```

`CodeQuery.Create` is the only constructor a caller needs: it trims the text, rejects empty input,
clamps `K` to 1–12 and `Hops` to 0–3, validates `Kind` against the closed list and normalises `Path` to
forward slashes. Validation lives in the Domain, not in the tool, so the tool and the CLI reject the
same inputs with the same words.

## 3. Status and lifecycle records

```csharp
public sealed record IndexStatus
{
    public required string WorkspaceRoot { get; init; }
    public required IndexFreshness Freshness { get; init; }
    public required IndexTier Tier { get; init; }
    public required string Embedder { get; init; }        // "hashed-local/512" or "off"
    public required int Dimensions { get; init; }
    public int Files { get; init; }
    public int Chunks { get; init; }
    public int Symbols { get; init; }
    public int Edges { get; init; }
    public int StaleFiles { get; init; }
    public int SkippedFiles { get; init; }
    public int OrphanRows { get; init; }                  // rows with no surviving file
    public bool VectorSearchEnabled { get; init; }
    public bool LexicalFallback { get; init; }
    public DateTimeOffset? BuiltUtc { get; init; }
    public long SizeBytes { get; init; }

    public static IndexStatus Missing(string workspaceRoot);
}

public sealed record IndexBuildReport
{
    public required IndexStatus Status { get; init; }
    public int Added { get; init; }
    public int Changed { get; init; }
    public int Removed { get; init; }
    public int Unchanged { get; init; }
    public int Skipped { get; init; }
    public long ElapsedMilliseconds { get; init; }
    public bool Incomplete { get; init; }                    // the budget ran out
    public IReadOnlyList<string> Notes { get; init; } = [];  // "docs/README.md: larger than 1024 KiB"
}

public sealed record IndexVerification
{
    public required bool IsHealthy { get; init; }
    public int FilesMissingFromIndex { get; init; }
    public int FilesMissingOnDisk { get; init; }
    public int HashMismatches { get; init; }
    public int Orphans { get; init; }                        // rows with no surviving file
    public IReadOnlyList<string> Details { get; init; } = [];
}
```

## 4. Ports

```csharp
namespace SharpCoreAgent.Domain.Index;

/// <summary>Read side of the index: everything the five tools need, and nothing that mutates.</summary>
public interface ICodeIndex
{
    ValueTask<IndexStatus> GetStatusAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<CodeEvidence>> SearchAsync(CodeQuery query, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<CodeSymbolRef>> FindSymbolsAsync(string name, string? kind, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<CodeEvidence>> FindReferencesAsync(string symbol, ReferenceDirection direction, int maxResults, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<CodeEvidence>> GetContextAsync(string target, int hops, int k, CancellationToken cancellationToken);
}

/// <summary>Write side of the index: the CLI command and the turn-start refresh, never a tool.</summary>
public interface ICodeIndexMaintainer
{
    ValueTask<IndexBuildReport> EnsureFreshAsync(TimeSpan budget, CancellationToken cancellationToken);
    ValueTask<IndexBuildReport> RebuildAsync(CancellationToken cancellationToken);
    ValueTask<IndexVerification> VerifyAsync(CancellationToken cancellationToken);
    ValueTask<IndexBuildReport> PruneAsync(CancellationToken cancellationToken);
}
```

Obligations the adapter must keep:

1. **No throw on absence.** A missing, disabled, corrupt or locked index is a value, not an exception:
   queries return empty lists and `GetStatusAsync` names the state. Tools turn "empty + not fresh" into
   the error result FR-019 requires.
2. **Status is cached.** `GetStatusAsync` is called on every empty search; the adapter caches it for the
   process and invalidates on write, so the extra read is free.
3. **Read-only.** `ICodeIndex` never writes. `SharpCoreDbCodeIndex` opens the database with no writer
   lock; `SharpCoreDbIndexMaintainer` takes the lock.
4. **Bounded.** Every list is capped inside the adapter as well as in the tool, so a bug in one does not
   produce an unbounded payload.
5. **Deterministic.** Same index + same query ⇒ same list, same order (FR-013).

## 5. Storage model

One SharpCoreDB database; the DDL is in [`contracts/index-schema.md`](./contracts/index-schema.md).
Concepts, in the order they are written:

| Table | One row is | Written by | Read by |
|---|---|---|---|
| `meta` | one key/value fact about the build | maintainer, last | adapter on open |
| `files` | one indexed file | per file transaction | scanner, status, verify |
| `chunks` | one passage (+ `VECTOR(n)`) | per file transaction | BM25 leg, vector leg, evidence |
| `symbols` | one declaration | per file transaction | `find_symbol`, graph, community |
| `edges` | one typed, weighted relation | after all files in a build | traversal, community |
| `node_embeddings` | one symbol/passage node vector | per file transaction | vector leg for symbols |
| `docs` | one documentation passage | per file transaction | `doc_mentions` boost |

Rows are keyed by integer `id`; the cross-table links are `chunks.file_id`, `symbols.file_id`,
`edges.source`/`edges.target` (symbol ids), `node_embeddings.node_id`. Every link is `PRAGMA
foreign_keys`-checked where the engine supports it, and `VerifyAsync` re-checks them anyway — the engine
is a pre-release and an orphan row is a silent wrong answer.

## 6. Mapping rules

| Storage column | Domain property | Rule |
|---|---|---|
| `files.path` | `CodeEvidence.Path`, `CodeSymbolRef.Path` | already workspace-relative with `/`; re-validated with `Workspace.IsInside` on the full path before use |
| `chunks.start_line` / `end_line` | `CodeEvidence.StartLine` / `EndLine` | stored 1-based, inclusive; `EndLine - StartLine + 1 ≤ 40` enforced at write |
| `chunks.sha256` (64 hex) | `CodeEvidence.Sha8` | first 8 characters, never truncated further |
| `chunks.text` | `CodeEvidence.Text` | as extracted, UTF-8, no BOM |
| `symbols.key` | `CodeSymbolRef.Key` | `<namespace>.<type>[.<member>]#<kind>`, no whitespace |
| `symbols.qualified_name` | `CodeSymbolRef.QualifiedName` / `CodeEvidence.Symbol` | dotted, no `#kind` suffix |
| `symbols.community` | `CodeSymbolRef.Community` | `"c<int>"`, assigned per build by Louvain |
| `edges.rel` | `CodeEvidence.Relation` | `calls`, `called_by`, `inherits`, `implements`, `imports`, `declares`, `doc_mentions` |
| `files.state` | `IndexStatus.StaleFiles` | `ok`, `skipped`, `stale`, `error`; anything but `ok` counts as stale |
| `meta['build_utc']` | `IndexStatus.BuiltUtc` | ISO-8601 UTC |
| `meta['embedder']` + `meta['dimensions']` | `IndexStatus.Embedder`, `Dimensions` | `"hashed-local/512"`, `512` |

## 7. State transitions

```
            ┌─────────┐  first build   ┌────────┐
            │ Missing ├───────────────►│ Fresh  │
            └─────────┘                └───┬────┘
                 ▲                         │ files changed
                 │ delete .sharpcoreagent      ▼
            ┌────┴────┐   budget cut   ┌────────┐
            │ Corrupt ├◄───────────────┤Partial │
            └─────────┘  (bad file)    └────────┘
                 ▲                         │ next refresh completes
                 │                         ▼
                 └─────────────── Stale ◄──┘   (changed files not yet re-indexed)
```

Rules:

- Every write updates `meta['build_utc']` and `meta['build_id']` last, so a crash leaves the previous
  build id in place and `VerifyAsync` can tell a complete build from a truncated one.
- `Building` is not stored; it is reported when the writer lock is held by another process.
- `Corrupt` requires an explicit recovery: the maintainer deletes and recreates the database on the next
  build and records the reason in `IndexBuildReport.Notes`.


