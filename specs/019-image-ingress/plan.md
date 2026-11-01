# Plan — 019 image ingress

## Shape

Two doors, one rule, no new tool. `ImagePayload` (new, `Infrastructure/Images`) owns the three facts that both doors
need — which media types a provider accepts, how big a base64 payload really is, and what the ceiling is — and every
other change is a call site adopting it:

```
McpServerConnection.CallAsync ──► McpContent.FlattenWithImages ──► McpCallResult(Text, Images)
                                                                        │
                                                          McpTool.ExecuteAsync
                                                          ├─ gate on context.Model.SupportsImages
                                                          ├─ ceiling MaxImageBytes, count MaxImagesPerResult
                                                          ├─ ToolResult.Images  (nothing deleted from Content)
                                                          └─ one line per un-attached picture, with its reason

FetchUrlTool.AnswerAsync ──► ImagePayload.TryMediaType (response Content-Type)
                          ├─ whole body, within both ceilings, model can see → ToolResult.Image + Read effect
                          └─ otherwise the 008 header block and one sentence that no longer lies about binaries
```

`Domain` gains nothing. `Application` gains one constant and one ternary: the loop already attaches
`ToolResult.Images` to a user message, so a plural sentence is the only loop change.

## Decisions and their alternatives

| Decision | Why | What it costs |
|---|---|---|
| Attachment is additive, not a replacement for the description | The path is what survives a session reload (round 9's FR-009) and what a text-only model reads; the picture is what a reading model sees. Deleting either loses a reader | One tool result can now say the same thing twice. Deliberate: the two halves are for two readers |
| The gate is the *context's* model, not a constructor flag | `McpTool` and `fetch_url` are built once and called from turns that can use different models (`016`'s roles are per job, not per process) | A tool that attached on trust would send a picture to a model that refuses it, failing the whole request |
| `image/bmp` is saved by the sink but never attached | The sink's table exists so a developer has the file; FR-002's table exists so a provider has a payload it accepts. They are different questions with different answers | A saved `.bmp` is reachable by neither `view_image` (round 9's five extensions) nor attachment. Named in the spec rather than papered over |
| `image/jpg` normalised to `image/jpeg` | One spelling is what every provider documents; the other is what servers send | A payload is relabelled, which FR-010 otherwise forbids. The exception is the media-type alias, not the bytes |
| A truncated body is refused, never attached | Base64 of half a PNG is a corrupt picture, and a provider's failure would arrive as a 400 far from the cause | An image larger than `WebFetchOptions.MaxBytes` (2 MiB) cannot be fetched at all. Named in the refusal, with both numbers |
| `MaxImagesPerResult` = 4 | A browser server can return a screenshot, a full-page capture and two console images in one answer; four is a readable turn and a bounded request | A fifth picture is named and not shown. The alternative — no cap — makes one call able to spend a turn's context |
| The plural sentence is a second constant, not a formatted one | `PromptLibrary` is constants read by the prompt tests; a format string would put a `{}` in a shipped sentence | Two sentences to keep in step. They share the same rule, and one test pins that they cannot diverge in meaning |
| No `Accept: image/*` | Changing what a text fetch negotiates would change what pages return for every existing caller, for the benefit of a site that only serves images when asked | A site that content-negotiates still returns HTML. Out of scope, named |

## Where the risk is

1. **The MCP seam is text-shaped.** `McpCallResult` is `(bool, string)` and four call sites depend on it. The change
   is a `record` with an `init` property defaulted to `[]`, so `McpCallResult.Failure` and every failure path keep
   compiling with no image, and `Flatten` keeps its two existing overloads.
2. **Bounding.** `McpTool` bounds the text at `_options.MaxResultChars` *before* the reasons are appended; the reasons
   are one short line each and at most one per picture, so the bound still holds to within a few hundred characters.
   The alternative — appending first — would let a server with 200 images push the description out of its own result.
3. **The `fetch_url` contract change.** `008`'s refusal sentence is quoted in its spec and its tasks; both get a
   correction in this round rather than a silent edit, the way `006` T016 and `010` T035 were closed in commits
   `ce78ced` and `b4c539c`.

## Verification

| Gate | Command | Expectation |
|---|---|---|
| Build | `dotnet build .\SharpCoreAgent.slnx -c Release` | 0 warnings, 0 errors |
| Suite | `dotnet test .\SharpCoreAgent.slnx -c Release --no-build` | all green; the count rises by the new classes and is copied into `README.md` |
| Layering | review | no `Domain` change; `ImagePayload` is `Infrastructure`; `Application` sees only `Domain` types |
| No new door | review | `Process.Start` still only in the shell tool and the MCP client; `fetch_url` still the only network tool and still behind `--web` |
| Determinism | review | the same scripted server answer produces the same message sequence and the same text |

## Complexity Tracking

No violation of a principle, and one recorded cost: `fetch_url` now returns a binary payload for four media types,
which narrows the meaning of its own description ("return the page as readable text") and of `008`'s promise. The
narrowing is written into the tool description, the spec, and `008`'s tasks, because a tool whose description is
narrower than its behaviour is the defect this repository treats as a bug in one of the two.
