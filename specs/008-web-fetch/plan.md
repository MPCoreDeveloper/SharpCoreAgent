# Plan — 008 web fetch

## Shape

One tool, one policy object, one client of its own.

```text
--web  -----------------------> SharpCoreAgentOptions.WebAccessEnabled
--web-allow <host> (repeat) --> SharpCoreAgentOptions.WebAllowedHosts
                                          |
                                          v
                     ServiceCollectionExtensions.AddWeb (only when enabled)
                     |                    |                       |
                     v                    v                       v
       HttpClient "sharpcoreagent-web"  WebFetchOptions   IWebAddressResolver
       (no cookies, no redirects,       (ceilings,       (DnsWebAddressResolver;
        no default headers)              hosts)            stubbed in tests)
                     |                    |                       |
                     +---------+----------+-----------+-----------+
                               v
                          FetchUrlTool  (ToolSafety.ReadOnly)
                               |
              scheme -> userinfo -> address -> request -> redirect? -> read body
                                              (each hop judged)     -> reduce -> bound
```

Nothing else in the product learns that the network exists: no workspace change, no new path syntax, no
change to the approval model, and no change to the shell tools.

## The tool

`FetchUrlTool` in `Infrastructure/Tools`, beside the other fifteen, with three collaborators injected so
that every one of them can be replaced in a test:

```csharp
public FetchUrlTool(IHttpClientFactory httpClientFactory, WebFetchOptions options, IWebAddressResolver resolver)
```

`ExecuteAsync` is one loop:

1. read `url` and `max_chars` through `ToolArgumentReader`, so a garbage call is an argument error;
2. parse the URL; a non-absolute string is a failure;
3. open one linked `CancellationTokenSource` with the fetch timeout — one budget for the whole fetch;
4. for each hop: judge it, send one `GET`, and either follow a redirect (bounded) or answer.

The answer is a labelled block followed by the text:

```text
url          https://example.com/final
status       200 OK
type         text/html; charset=utf-8
bytes        18402
truncated    yes
redirected   from https://example.com/page

<the text>
```

Labels rather than one separator-joined line because a model reads a label and a developer scanning a
transcript reads a column; the padded width matches `list_roots`.

## The address check

`WebAddressPolicy` is a pure function of an address, in two parts:

- `Classify(IPAddress)` names the refusal — `loopback`, `private`, `link-local`, `unique-local`,
  `carrier-grade NAT`, `unspecified`, `multicast`, `reserved`, `cloud metadata` — or returns `null` for
  the public internet. A v4-mapped v6 address is judged as the v4 address it reaches, which is what
  stops `::ffff:127.0.0.1` from being a way through.
- `IsExempt(host, port, addresses, allowedHosts)` matches an entry exactly: a name, a `host:port`, a
  bracketed `[v6]:port`, or an IP literal. No wildcards.

`IWebAddressResolver` exists so the decision can be tested without DNS; `DnsWebAddressResolver` is the
process's implementation and answers an IP literal with itself.

**The rule is "any address, not the first".** A name with two answers where one is `10.0.0.5` is refused,
because taking the first answer is a decision a hostile zone controls.

**Rebinding is not caught.** The check happens before the request; a resolver that answers differently
for the connection is not re-asked. The honest fix is a `SocketsHttpHandler.ConnectCallback` that
validates at connect time, which needs the resolver to be reachable from the handler rather than the
tool, and is a change to make when a deployment needs it. Recorded here rather than pretended away.

## The HTML reduction

`HtmlToText.Reduce` walks the document once:

- a comment is skipped to `-->`;
- a tag ends at the first `>` that is not inside a quoted attribute value;
- `head`, `script`, `style`, `svg`, `template` and `noscript` are skipped with their content;
- the rest of the tag is dropped, and a block-level name writes one newline;
- text is entity-decoded **after** tagging, so `&lt;b&gt;` stays the three characters a reader sees;
- whitespace in text becomes a single space, and empty lines are dropped.

A regular expression over tags would be shorter and wrong twice: `>` inside an attribute, and a
backtracking pattern over untrusted input. An unterminated tag ends the reduction with what was read.

## The client

```csharp
services.AddHttpClient(WebFetchOptions.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,   // the tool counts and judges hops
        UseCookies = false,          // no page can set state that a later fetch sends back
        ConnectTimeout = TimeSpan.FromSeconds(10),
        AutomaticDecompression = DecompressionMethods.All,
    });
```

`Timeout.InfiniteTimeSpan` on the client because the tool owns the budget: a second ceiling underneath
the first would surface as a `TaskCanceledException` with the wrong story in it.

## The prompt

`PromptLibrary.SystemPromptFor(indexAvailable, webAvailable)` gained a second switch and a `WebRule`
that ships only when the tool does. Same reasoning as `IndexRule`: an instruction naming a tool that is
not registered costs a turn and teaches the model that instructions are approximate.

## The command line

| Flag | Parsed into | Refused on |
|---|---|---|
| `--web` | `CliOptions.Web` → `SharpCoreAgentOptions.WebAccessEnabled` | `models`, `index`, `config`, `setup` |
| `--web-allow <host>` | `CliOptions.WebAllowedHosts` (ordered, repeatable) | the same four, and without `--web` |

No settings key (`SettableNames` untouched), no environment variable, and no session field: a resumed
`chat` starts with the network off unless this invocation says otherwise, which is the same answer
round 7 gave for attachments.

## Complexity Tracking

| Decision | Cost | Why it is worth it |
|---|---|---|
| A second `HttpClient` registration | one more named client in the composition root | the model adapters' client carries `Authorization`; sharing it would leak a provider key to any page |
| An `IWebAddressResolver` seam | one interface, one implementation, one fake in tests | an address rule that could only be tested with DNS would not be tested |
| The HTML reduction, hand-written | ~200 lines in `Infrastructure/Web` | a dependency for tag stripping; the BCL has `WebUtility.HtmlDecode` and nothing else this needs |
| Manual redirect loop | the tool carries the hop counter and the URL | automatic redirects would skip the address check on every hop after the first |
| A `prompt` change for `--web` | one more parameter on a public method | the tool and the rule that governs it must ship or not ship together |
| Byte ceiling by chunked read | a small read loop instead of `ReadAsByteArrayAsync` | the ceiling has to bound what is downloaded, or a chatty server wins |
| No environment variable | a CI job must pass the flag | an inherited variable that opened the internet is the authority-nobody-granted case |

## As built

Landed as specified, with four notes a reader of the plan would otherwise have to rediscover:

1. **`--web-allow` without `--web` is a usage error**, not an accepted no-op. The spec asked for the
   flag to be refused on the wrong commands; refusing it without the switch it modifies is the same
   argument applied one level down.
2. **`SystemPrompt` rule 3 was corrected.** It said every path outside the workspace root is rejected,
   which round 7 made untrue: an absolute path into an attachment is accepted. It now says to read only
   the roots `list_roots` reports. No test pinned the old sentence.
3. **The truncation line is a header row *and* a trailer.** The header says `truncated    yes` so a
   reader who stops at the top still knows; the text ends with `[truncated: kept the first N of M
   characters]` so the cut is visible where it happened.
4. **`Failure` for status ≥ 400 keeps the body.** The stub test pins `404 Not Found` *and* the page text,
   because an error page is often the only place the reason is written.

Suite: 880 tests, 0 warnings, 0 errors on `dotnet build .\SharpCoreAgent.slnx -c Release`.
