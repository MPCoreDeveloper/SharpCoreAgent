# Plan — 007 external roots

## Shape

One new concept, threaded through the type that already answers every path question.

```text
CliOptions --add-root  ---+
                          +--> SharpCoreAgentOptions.AdditionalRoots
SHARPCOREAGENT_ADD_ROOTS -+                |
                                           v
                        Workspace.Create(root).WithAttachments(roots)
                                           |
                 +-------------------------+-------------------------+
                 v                                                   v
     TryResolve  (write path)                            TryResolveRead  (read path)
     primary root only                                    primary + attachments
                 |                                                   |
                 v                                                   v
  write / replace / delete / shell                    read_file, list_directory, search_text,
                                                      list_roots, /roots
```

## The workspace change

`Workspace` stays one value and gains one collection. No new type, no new member on
`ToolInvocationContext`, and therefore no place where "which workspace" can disagree with "which roots".

```csharp
public sealed class Workspace
{
    public string Root { get; }
    public IReadOnlyList<Workspace> Attachments { get; }
    public bool HasAttachments => Attachments.Count > 0;

    public static Workspace Create(string root);                     // unchanged
    public Workspace WithAttachments(IEnumerable<string> roots);     // validate, normalise, de-duplicate

    public bool TryResolve(string path, out string full, out string? error);   // unchanged: primary only
    public bool TryResolveRead(string path, out string full, out Workspace? owner, out string? error);
    public bool IsInside(string fullPath);                           // unchanged: primary only
    public bool IsInsideAny(string fullPath);
}
```

`WithAttachments` returns a new value rather than mutating, because the existing tests and the
composition root both treat a `Workspace` as the immutable fact it is. Each attachment is itself a
`Workspace`, which is what lets the containment rule be "the prefix comparison that already exists",
used once per root, instead of a second implementation of it.

`TryResolveRead` is three lines of policy:

- relative → `TryResolve` against the primary root only (FR-004);
- absolute → the first root whose `IsInside` accepts it, returned as `owner`;
- absolute and inside nothing → refused, with a message that names the primary root, the attachment
  count, and `list_roots`.

`owner` is what lets `read_file` say `shared/src/Order.cs` for a file in an attachment instead of
printing an absolute path the model then has to shorten itself.

## The physical check

`PathSafety.TryResolve` keeps its signature and its behaviour; a sibling `TryResolveRead` walks to the
deepest existing ancestor exactly as it does today and then asks `IsInsideAny` instead of `IsInside`:

```csharp
if (physical is null || workspace.IsInsideAny(physical))
{
    error = null;
    return true;
}
```

Link resolution, the 64-deep cap and the "an unreadable link is judged lexically" rule are shared, not
copied, so a future fix to link handling cannot fix one path and miss the other.

## Addressing: absolute paths, not a prefix

The alternative — `@shared/src/Order.cs` — was rejected in the spec. The implementation consequence is
worth stating: nothing parses a path, so a model that already knows how to name a file on Windows or
POSIX needs no new grammar, and `list_roots` is an ordinary read-only tool whose only job is to say
which absolute prefixes are legal.

The one cost is that a relative path cannot reach an attachment. That is deliberate: two repositories
that both contain `src/Order.cs` must not make `read_file("src/Order.cs")` mean whichever root was
listed first. The refusal names `list_roots`, so the model is taught the correct move instead of the
ambiguous one.

## Which tools change

| Tool | Resolver | Why |
|---|---|---|
| `read_file`, `list_directory`, `search_text` | `PathSafety.TryResolveRead` | the three that answer "what is in that file, this tree, this text" |
| `list_roots` (new) | none | it reports the roots themselves |
| `write_file`, `replace_in_file`, `delete_file` | `PathSafety.TryResolve` (unchanged) | the write path, and the whole point is that it did not move |
| `run_shell`, `shell_output`, `shell_stop` | unchanged | the shell was never confined; `--approval` is its boundary |
| the five index tools | unchanged | one index describes one tree |

`Workspace.TryResolve` keeps its message verbatim, because the existing suite pins it and FR-003 says
so. The three write tools therefore enrich a failure with one extra sentence from a new
`Workspace.DescribeOutside(string path)` helper, which returns `null` for an ordinary escape and
`"'…\src\Order.cs' is inside the read-only attachment 'D:\repos\Shared.Contracts'; writes are confined
to 'D:\repos\App'"` for a path that an attachment would have accepted. A refusal that names the reason
is the difference between "the tool is broken" and "attach it and read it instead".

## Where the index stops

`SharpCoreAgentIndex.Open(workspace)` and `IndexPaths.For(workspace)` read `workspace.Root`, so
attachments never enter the index and never will by accident. The five index tools keep using the
primary-root resolver and answer an attachment path with a message that says it is not indexed. That
preserves one index, one staleness story and one meaning for `index verify`.

## CLI and composition

- `CliOptions.AdditionalRoots` is a `List<string>`, filled by repeated `--add-root` and then by
  `SHARPCOREAGENT_ADD_ROOTS` split on `Path.PathSeparator`. `--add-root` is accepted on `run` and
  `chat` only; on any other command it is a usage error, in the same place `--json` is rejected.
- `SharpCoreAgentOptions.AdditionalRoots`, and `CreateWorkspace()` becomes
  `Workspace.Create(WorkspaceRoot).WithAttachments(AdditionalRoots)`. It is the single place
  attachments are applied, so the workspace the CLI hands to `AgentSession` and the workspace the
  index registration creates at `ServiceCollectionExtensions` line 178 cannot disagree.
- `CliHost.TryOpenWorkspace` already maps a throw from `Workspace.Create` to exit 2 with the message;
  `WithAttachments` throws the same `DirectoryNotFoundException`, so a bad root needs no new error
  plumbing at all. It fails before the first request because that call happens before `CliHost.Build`.

## One formatter for roots

`/roots` and `list_roots` print the same thing, so they print it from one function:
`RootListing.Describe(Workspace)` in `SharpCoreAgent.Application`, returning lines of
`primary      D:\repos\App` and `read-only    D:\repos\Shared.Contracts`. The tool wraps it in a
`ToolResult`, the command writes it to the console, and the banner uses only the count, because one
line of chrome is enough.

## Complexity Tracking

- **A four-parameter read resolver (`path`, `full`, `owner`, `error`).** The `owner` out-parameter is
  what stops every read tool from re-deriving which root answered it; the alternative is a second
  prefix comparison living in `read_file`, which is exactly the duplication principle II forbids.
- **`TryResolveRead` beside `TryResolve` instead of a `bool allowAttachments` parameter.** A boolean
  defaulting to `false` is the same code with one more way to pass the wrong constant at a call site
  that a reviewer must catch. Two names cannot be confused.
- **A new public method, `IsInsideAny`, rather than making `IsInside` mean "any".** `IsInside` is the
  write promise; broadening it in place would silently widen what a write tool accepts.
- **The refusal hint is a separate helper, not a reworded `TryResolve`.** FR-003 and the existing
  assertions both pin the current message; adding a sentence where it is useful is cheaper than
  rewriting what already works.

