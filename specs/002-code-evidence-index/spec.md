# Feature Specification: Code Evidence Index (SharpAriadne)

**Feature Branch**: `002-code-evidence-index`

**Created**: 2026-10-24

**Status**: Draft

**Input**: User description: "Give the agent a persistent, local index of the workspace — graph, vectors and
full text — so it can find the right passage and the right neighbours before it reads a file, and cite
where every claim came from. Product name: **SharpAriadne**."

## Naming

The product is **SharpAriadne** (the thread through the labyrinth: the index that leads the agent back
to the code). The repository, namespaces and binary stay `SharpCoreAgent` / `sharpcoreagent` — this feature adds
a name, not a rename. `sharpcoreagent --version` prints `Ariadne <version> (SharpCoreAgent)`, and `ariadne` is
accepted as an alias for the binary.

## Why

Today the agent starts every task blind: `search_text` finds literals and `read_file` shows 200 lines.
It has no idea which file *implements* the interface in the stack trace, which functions *call* the one
it is about to change, or which files were touched three turns ago. That costs tokens (it reads whole
files to find one method) and costs accuracy (it guesses instead of following the graph).

This feature gives the workspace a durable, local index that answers three questions in one call:

1. **Where is the text that matches this intent?** — lexical + vector retrieval over bounded passages.
2. **What is this symbol and who depends on it?** — declarations, signatures and a call/inheritance/
   import graph.
3. **What else belongs to this cluster?** — graph communities and weighted neighbourhoods (GraphRAG).

Every answer is **evidence**: a path, a content hash and a line range the model can hand straight back
to `read_file`, or cite in its report. No answer is a summary the model has to trust.

## Scope

In scope: a per-workspace index built from files, stored in a SharpCoreDB database, exposed through
five read-only tools and one CLI command, refreshed incrementally, and injected into the system prompt
as a "search first" rule.

Out of scope: an MCP server (the index is in-process), cross-machine or shared indexes, a
language-server or compiler-semantic model, git history indexing, and any network call by default.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Build and inspect the index (Priority: P1)

A developer runs `sharpcoreagent index` in a repository. The command walks the workspace, extracts files,
passages, symbols and edges, writes them to `.sharpcoreagent/index/` and prints what it did: how many files
were indexed, how many were skipped and why, how many chunks/symbols/edges exist, whether vectors are
present, and how long it took. A second run in a row indexes nothing (`0 changed`) because every file's
hash still matches.

**Why this priority**: without the artifact there is no retrieval, and without a coverage report the
developer cannot tell "index complete" from "index still working". This is the MVP.

**Independent Test**: run `sharpcoreagent index` twice in a temp workspace with five known files; assert the
report of run one (5 indexed, 0 skipped) differs from run two (0 changed), and that `--status` reports
counts that match the files.

**Acceptance Scenarios**:

1. **Given** an empty workspace, **When** `sharpcoreagent index` runs, **Then** it exits 0 and reports
   `0 files` without creating a database that later fails to open.
2. **Given** a workspace with source files, **When** `sharpcoreagent index --rebuild` runs, **Then** every
   row is replaced and `--verify` reports `result fresh`.
3. **Given** a file above the size limit and a binary file, **When** the index runs, **Then** both are
   recorded as `skipped` with a reason and the run still exits 0.
4. **Given** an index built by an older parser version, **When** the workspace is opened, **Then** the
   index is rebuilt rather than mis-queried.

---

### User Story 2 - Find the passage that matters (Priority: P1)

A developer asks "why does the turn loop stop after a refused tool call". The agent calls `search_code`
first and receives five bounded passages, each with `path:start-end`, a content hash, the symbols the
passage declares and *why it matched* (lexical, vector, graph). The agent then reads only the line
ranges it was given instead of four whole files.

**Why this priority**: this is the token and accuracy win the whole feature exists for.

**Independent Test**: against a fixed corpus, a query naming a known symbol returns a passage whose
line range contains that symbol's declaration, ranked first, with the path quoted correctly; the same
query twice returns an identical list in an identical order.

**Acceptance Scenarios**:

1. **Given** an indexed workspace, **When** `search_code` is called with a plain-language query, **Then**
   the result is at most `k` blocks, each with a path, a line range and a hash.
2. **Given** a query that names a symbol exactly, **When** `search_code` runs, **Then** the declaration
   is the first block.
3. **Given** a path argument, **When** `search_code` runs, **Then** no block outside that path is
   returned.
4. **Given** a file edited after the index was built, **When** its passage is returned, **Then** the
   block is marked `stale` and names the file, instead of presenting old text as current.
5. **Given** no index exists, **When** `search_code` runs, **Then** the tool returns an error result
   telling the model to ask the developer to run `sharpcoreagent index` — it does not silently fall back.

### User Story 3 - Follow the graph (Priority: P2)

The agent needs to change a method. `find_symbol` gives it the declaration, the signature, the doc
summary and caller/callee counts. `find_references` lists the call sites grouped per file, so the agent
knows what it will break before it edits.

**Why this priority**: this is what plain text search cannot do, and it is the difference between
"changed the method" and "changed the method and the three callers".

**Independent Test**: in a corpus with a known call chain `A → B → C`, `find_symbol B` reports
`callers: 1, callees: 1`, and `find_references B --direction callers` names `A`.

**Acceptance Scenarios**:

1. **Given** a qualified name, **When** `find_symbol` runs, **Then** the exact match is returned before
   any fuzzy match, and ambiguous names return all candidates with their paths.
2. **Given** an unknown symbol, **When** `find_references` runs, **Then** the result is an empty list
   with a message, not an error.
### User Story 4 - See the neighbourhood (Priority: P2)

Before restructuring, the agent asks `code_context` for a file or symbol: it gets the declaration, its
members, its weighted neighbours one or two hops out, the community it belongs to and any documentation
that mentions it. This is the "what is around this" question, answered from the graph rather than from a
directory listing.

**Why this priority**: it turns the index from a search box into a map, and it is the piece an
off-the-shelf code search cannot provide.

**Independent Test**: `code_context` on a symbol declared in a file with two callers returns the
declaration plus at least one neighbour, with the relationship named (`calls`, `called_by`, `inherits`).

**Acceptance Scenarios**:

1. **Given** a workspace-relative path, **When** `code_context` runs, **Then** the file's declarations
   and its strongest neighbours are returned in one block.
2. **Given** `hops` above the documented maximum, **When** `code_context` runs, **Then** the value is
   refused with a message naming the maximum.

### User Story 5 - Stay fresh without being asked (Priority: P3)

`sharpcoreagent run` and `sharpcoreagent chat` bring the index up to date before the first model call, spending
at most a few seconds. A large repository keeps indexing in the background while the turn proceeds; the
agent is told the coverage it currently has, and files it writes during the turn are re-indexed before
the next turn.

**Why this priority**: an index nobody remembers to refresh is worse than none, but a startup that
blocks for a minute is not acceptable either.

**Independent Test**: edit one file, start a turn, and assert `index_status` reports a build newer than
the edit and zero stale files; then assert a no-op start costs under the budget.

**Acceptance Scenarios**:

1. **Given** an index older than the workspace, **When** a turn starts, **Then** only the changed files
   are re-indexed.
2. **Given** a repository larger than the time budget, **When** a turn starts, **Then** the turn runs
   with partial coverage and `index_status` says `partial`.
3. **Given** a file written by `write_file` during a turn, **When** the next turn starts, **Then** the
   passage for that file reflects the new content.
4. **Given** `--no-index`, **When** a turn starts, **Then** no index is read, written or created.

### Edge Cases

- Workspace on a read-only volume, or `.sharpcoreagent/` not writable → index is unavailable; every tool
  returns a clear error and the agent still works with the file tools.
- Two `sharpcoreagent` processes in one workspace → one writer, many readers; the second writer waits or
  reports "another process is indexing".
- A file deleted or renamed between scan and write → recorded as removed, never left dangling.
- An index file truncated or corrupt → detected on open, rebuilt from scratch, reported.
- A file with CRLF, a BOM, or invalid UTF-8 → decoded with replacement, lines still numbered correctly.
- A source file containing a 4 000-line generated block → chunked at member/blank-line boundaries, not
  truncated mid-line.
- A workspace with no recognised source files → `0 indexed`, exit 0, tools answer "empty index".
- The same file reached twice through a junction → indexed once, by real path.
## Requirements *(mandatory)*

### Functional Requirements

**Index and storage**

- **FR-001**: The system MUST build and persist a per-workspace index in `.sharpcoreagent/index/`, using a
  single SharpCoreDB database, without touching anything outside that directory.
- **FR-002**: The index MUST record, per file: workspace-relative path, language, byte size, last write
  time, content hash, indexing state and counts. Paths use `/`; hashes are lowercase hex SHA-256.
- **FR-003**: The index MUST store bounded passages (start line, end line, text, hash) that never exceed
  40 lines or 1 600 characters.
- **FR-004**: The index MUST store declarations as symbols with a stable key, qualified name, kind,
  line range, signature and one-sentence doc summary when the source has one.
- **FR-005**: The index MUST store typed edges between symbols (`calls`, `references`, `inherits`,
  `implements`, `imports`, `declares`, `doc_mentions`) with a weight, in a table whose columns are named
  `source`, `target` and `weight` so the graph engines can consume it.
- **FR-006**: The index MUST store one vector per passage and one per symbol node, of a fixed dimension
  recorded in the index metadata.
- **FR-007**: The index MUST store its own identity: schema version, parser version, analyzer contract,
  embedder id, dimensions, tier, build id and build timestamp.
- **FR-008**: The system MUST ignore `.git`, `node_modules`, `bin`, `obj`, `.vs`, `.sharpcoreagent`, files
  above the configured size limit, and binary files — the same list the file tools use.
- **FR-009**: The system MUST index incrementally: unchanged files (size and mtime match, hash confirms)
  are skipped; changed files are re-indexed as one transaction per file; removed files are pruned.
- **FR-010**: The system MUST detect a schema or parser version it cannot read and rebuild instead of
  querying.

**Retrieval**

- **FR-011**: `search_code` MUST combine lexical (BM25), vector (cosine over the passage vectors) and
  graph signals, and MUST report which legs contributed to each block.
- **FR-012**: Every evidence block MUST carry path, start line, end line, content-hash prefix, the
  symbols it declares and a staleness flag.
- **FR-013**: Results MUST be deterministic: the same index and the same query produce the same list in
  the same order, with `(score, path, start line)` as the tie-break.
- **FR-014**: `search_code` MUST respect `k`, `path` and `kind` filters, and MUST cap output under
  `MaxToolResultChars` with an explicit truncation marker.
- **FR-015**: `find_symbol` MUST prefer an exact match, then a case-insensitive match, then a
  substring match, and MUST return all candidates with their paths.
- **FR-016**: `find_references` MUST answer callers, callees or both, grouped per file, capped.
- **FR-017**: `code_context` MUST return a declaration plus weighted neighbours within `hops` (1–3) and
  the community both ends belong to.
- **FR-018**: All five tools MUST be `ReadOnly` and MUST report a `Read` effect.
- **FR-019**: When the index is missing, unavailable or empty, each tool MUST return an error result that
  names the remedy, and MUST NOT throw.

**Freshness and lifecycle**

- **FR-020**: `sharpcoreagent index` MUST support `--status`, `--rebuild`, `--verify` and `--prune`, and MUST
  print a coverage report in the shape of `contracts/cli-index.md`.
- **FR-021**: A turn MUST refresh the index within a configurable budget before the first model call,
  and MUST continue with partial coverage rather than blocking past it.
- **FR-022**: Files written or deleted by the agent during a turn MUST be re-indexed before the next
  turn, without a full rebuild.
- **FR-023**: The system MUST expose coverage and freshness to the agent through `index_status`,
  including the number of stale files and whether vector search is available.
- **FR-024**: The system MUST hold a single writer lock per workspace and allow concurrent readers.
- **FR-025**: `--no-index` MUST disable every index read, write and refresh for the process.

**Configuration and safety**

- **FR-026**: The embedder MUST default to a local, deterministic, dependency-free implementation; a
  provider-backed embedder MUST be opt-in and MUST NOT be used unless selected.
- **FR-027**: The index MUST be opened without encryption by default, because it is derived, rebuildable
  data; encryption MUST be opt-in through configuration.
- **FR-028**: No tool may read, write or query outside the workspace root or outside the index file.
- **FR-029**: The index MUST never contain a credential, a transcript or a file excluded by FR-008.
- **FR-030**: The system prompt MUST tell the model to search the index before reading whole files,
  naming the five tools and the `path:start-end` citation form, and MUST omit that instruction when the
  index is disabled with `--no-index`.

### Key Entities

- **File record**: one indexed file; identity is the workspace-relative path; carries the hash that makes
  staleness detectable and the state that makes "skipped" explainable.
- **Passage**: a bounded, citable slice of a file; the unit of retrieval and the unit the model then
  reads with `read_file`.
- **Symbol**: a declaration; identity is a stable key (`Namespace.Type.Member#kind`), so references in
  reports and edges survive a rebuild.
- **Edge**: a typed, weighted relationship between two symbols; input to traversal and community
  detection.
- **Vector**: a fixed-dimension embedding of a passage or a symbol node.
- **Index metadata**: the identity of the artifact (schema/parser/analyzer versions, tier, embedder,
  dimensions, build id, build time).
- **Evidence block**: what a tool returns — a passage plus locator, hash, symbols and provenance legs.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: On a fixed 200-file corpus with 25 recorded queries, the expected file appears in the top
  five for at least 22 of 25 queries, and first for at least 18.
- **SC-002**: A cold index of a 5 000-file, 50 MB source tree completes in under 90 seconds on a laptop;
  an incremental run with one changed file completes in under 3 seconds.
- **SC-003**: A `search_code` call on a 20 000-passage index answers in under 250 ms at p95.
- **SC-004**: On the five recorded benchmark tasks, a turn that uses `search_code` before `read_file`
  consumes at least 30% fewer input tokens than the same turn with the file tools only.
- **SC-005**: 100% of evidence blocks name a path, a line range and a hash; 0 blocks point outside the
  workspace.
- **SC-006**: The index directory stays under 50% of the size of the indexed source tree with int8
  quantisation.
- **SC-007**: Two runs over an unchanged workspace produce identical query results and index metadata,
  the build timestamp excepted.
- **SC-008**: With no network available, every requirement above still holds.

## Assumptions

- The workspace is a single directory tree on local disk and `.sharpcoreagent/` is writable; when it is not,
  the feature degrades to "index unavailable" rather than failing the turn.
- Source files are UTF-8 or single-byte; invalid sequences are replaced, not rejected.
- The SharpCoreDB `2.1.1` packages (`SharpCoreDB`, `.Graph`, `.Graph.Advanced`, `.Search`,
  `.VectorSearch`, `.HybridSearch`) are published on nuget.org and target `net11.0` like this
  repository.
- The local embedder is deliberately lexical-hashed rather than neural: zero download, deterministic,
  offline (FR-026). A provider embedder is a later opt-in, not part of this feature.
- Language support in v1 is heuristic: C# gets the richest facts; other text-like languages get passages
  and identifiers; a Roslyn extractor is a later, separately justified dependency behind the same port.
- Tools remain the only door to the machine (Principle II): the agent reaches the index through tools,
  never by the loop opening the database.




