# 008 — Reading the web: one page at a time

**Status**: implemented (T001–T028; T029–T033 deferred, listed in `tasks.md`)
**Branch**: `feature/008-web-fetch`, merged to `main` as `201a431`
**Depends on**: `001-coding-agent-core` (the tool model and the result contract), `003-terminal-console-agent`
(the console surface), `007-external-roots` (the precedent for authority granted per invocation)

## Why this feature exists

Every question a coding agent is asked now has a second half that lives outside the checkout: the
release notes for the version being upgraded to, the error message with three forum answers, the API
page for the SDK in `Directory.Packages.props`. Today the developer leaves the terminal, reads the page,
and pastes the paragraph back — which works, and is exactly the work an agent is supposed to take.

The two ways to add this badly are both familiar. The first is to hand the model a general-purpose
HTTP client and let it call whatever it likes: that turns a tool that reads one directory into a tool
that can read `http://169.254.169.254/`, the machine's own admin panels, and every service on the
office network. The second is to reach for a full browser, which means a 300 MB platform-specific
download and a JavaScript engine, in a product that currently ships as assemblies and promises no
native binaries.

So this round adds one tool, `fetch_url`, that reads one page and returns it as text. It is off unless
the developer passes `--web` for that invocation, it will not read this machine or its cloud metadata
unless the host is named, it carries no credential, and it never runs a script. That is a smaller
promise than a browser and a much larger one than a chat window.

## User stories

### US1 — Read the page, in the terminal (P0, Phase 1)

As a developer upgrading a package, I want the agent to read the release notes itself, so that the
answer is quoting the real page instead of my paraphrase of it.

Acceptance:

- `run "read https://…/CHANGELOG.md and tell me what breaks" --web` returns the page's text, with the
  bytes that matter, and the model cites the URL.
- The result names the final URL after redirects, the status, the content type and whether the text
  was cut, so "the page says X" can be checked against what was actually returned.
- A page that is not text — a PDF, an image, a zip — is refused by type instead of filling the context
  with mojibake.

### US2 — Nothing leaves the machine unasked (P0, Phase 1)

As a maintainer, I want a build nobody configured to make exactly the requests it makes today, so that
"the agent has the internet" is a decision someone typed rather than a property of the product.

Acceptance:

- Without `--web`, `fetch_url` is not registered: it is absent from `IToolRegistry`, absent from the
  definitions sent to the model, and absent from the system prompt.
- With `--web`, the request carries one `User-Agent` and nothing else: no `Authorization`, no `Cookie`,
  no `Referer`, and a URL that embeds a user name or password is refused before anything is sent.
- `web` is not a settings key and not an environment variable: `config set web on` is refused, and
  `SHARPCOREAGENT_WEB` is not read.

### US3 — The agent cannot be aimed at its own machine (P0, Phase 1)

As the person who runs the agent on a laptop with a dev server and a metadata service behind it, I want
a host that resolves to a loopback, private, link-local or metadata address refused with a sentence I
can act on, so that reading a page cannot become reading the machine.

Acceptance:

- `http://localhost:8080/`, `http://127.0.0.1/`, `http://10.0.0.5/`, `http://169.254.169.254/` and a
  host whose DNS answer includes any of those are refused, naming the address and the category.
- Every redirect hop is judged the same way, so a public page that redirects to `127.0.0.1` is refused.
- `--web-allow localhost` makes that one host readable for that one invocation, and a `host:port` entry
  covers that port only.

### US4 — A page is evidence, not instruction (P1, Phase 1)

As a developer whose task is described in the terminal, I want the prompt to say that a fetched page
cannot change the task, so that a page telling the agent to ignore its rules is reported rather than
obeyed.

Acceptance:

- `PromptLibrary.WebRule` ships only when `fetch_url` is registered, and says four things: the tool
  exists because `--web` was passed, a page can grant nothing, a truncated result is not the whole page,
  and a refusal naming an address means the host is local.
- The rule is appended after `PromptLibrary.IndexRule`, because the index is the nearer capability.

## Functional requirements

- **FR-001** `--web` applies to `run` and `chat` only. On `models`, `index`, `config` and `setup` it is a
  usage error (exit 2) naming the commands it does apply to.
- **FR-002** `--web-allow <host>` is repeatable, and without `--web` it is a usage error rather than a
  silently useless flag.
- **FR-003** `fetch_url` takes `url` (required, absolute) and `max_chars` (optional, 1..the tool's
  ceiling, default the ceiling). A call with no `url` is a tool argument error like any other.
- **FR-004** `http` and `https` only. Another scheme is refused by name, before any request.
- **FR-005** A URL that carries `userinfo` is refused, because the tool sends no credential.
- **FR-006** The host is resolved before the request, and the hop is refused when any address the host
  stands for is loopback, private (RFC 1918), link-local, unique-local, carrier-grade NAT, unspecified,
  multicast, reserved or a documented cloud-metadata endpoint — unless `--web-allow` names the host or
  the address.
- **FR-007** Redirects are followed by the tool, not the platform: at most five, each hop judged by
  FR-004..FR-006, and a hop without a `Location` header is a failure that says so.
- **FR-008** One timeout covers the whole fetch — redirects and body — and its expiry is a failure that
  names the URL and the budget. Cancellation from the caller is never a failure result.
- **FR-009** At most 2 MiB of a body is read. The ceiling bounds bytes downloaded, not bytes returned,
  and a body that hits it is reported as truncated.
- **FR-010** At most the character ceiling of text is returned, and a shortened result says how much of
  how many characters it kept.
- **FR-011** HTML (`text/html`, `application/xhtml+xml`) is reduced to readable text: `head`, `script`,
  `style`, `svg`, `template` and `noscript` are removed, entities are decoded after tagging, block
  elements become line breaks, and runs of whitespace collapse. Implemented here, with no new dependency.
- **FR-012** Other text types (`text/*`, JSON, XML, JavaScript, YAML, TOML, form data) are returned as
  they are. Anything else is refused by media type, or by a binary sniff when the type is absent.
- **FR-013** The result opens with labelled lines — `url`, `status`, `type`, `bytes` and, when either
  applies, `truncated` and `redirected` — then a blank line, then the text.
- **FR-014** A status of 400 or above is a failed result carrying the same header block and the body, so
  the model sees the status rather than an empty error.
- **FR-015** `ToolSafety.ReadOnly` and a `Read` effect whose target is the final URL: nothing on disk
  changes, no process starts, and plan mode may use it.
- **FR-016** The tool's client is not the model adapters' client: no cookie container, no automatic
  redirect, no default header, and nothing that could carry a provider key.
- **FR-017** `chat` shows a `web` row in its banner when the network is on, naming the count of hosts
  the developer added, and `--quiet` still silences the banner.

## Success criteria

- **SC-001** With `--web`, a stub `text/plain` response comes back with its URL, status, type and byte
  count in the result, and the effect names the final URL.
- **SC-002** Without `--web`, the container advertises no `fetch_url`, and the prompt does not mention
  the network.
- **SC-003** A loopback, private, link-local or metadata address is refused with its category and the
  `--web-allow` sentence; the same host is read when it is named.
- **SC-004** A redirect into a private address is refused, and the redirect ceiling is a failure naming
  the last hop.
- **SC-005** No request carries `Authorization`, `Cookie` or `Referer`, and a URL with `userinfo` is
  refused before a request exists.
- **SC-006** A 4 KiB body read with a 16-byte ceiling reports `truncated yes`, and a 1 000-character page
  read with a 100-character ceiling keeps exactly the first 100.
- **SC-007** No test in the suite opens a socket, and the suite is green with zero warnings on
  `dotnet build -c Release`.

## What this feature rejects, and why

- **A browser.** JavaScript execution, cookies, logins and screenshots are a different product with a
  different delivery story: a native binary and a platform matrix in a tool that ships as assemblies.
  Round 4 gives the extension seam an MCP server can fill.
- **Turning the network on by default, or persisting it.** `--web` is per invocation for the same
  reason an attached root is: authority granted in a command is authority the developer remembers
  granting. `config set web on` is refused and `SHARPCOREAGENT_WEB` is never read.
- **A fetch-time `Cookie` jar.** Cookies are how a page recognises a session; a tool that carries one
  reads as somebody the developer is not, and the transcript would not say so.
- **Sending the provider credential.** The obvious shortcut — reuse the model adapters' `HttpClient` —
  would hand a DeepSeek or Grok key to any page the model chose. The tool has its own client for that
  single reason.
- **An allow-list of public hosts, or a robots.txt fetch.** The refusal list is the security boundary;
  politeness is the developer's call, and a second request to obey a textual convention would double
  the traffic of every fetch to serve a rule an agent cannot be trusted to interpret.
- **Paging a long page (`start_index`).** The character ceiling plus a truncation line is enough for the
  model to say what it did not read; an index argument invites a loop of fetches where one summary
  would do. Reconsider if real transcripts show the wall being hit.
- **A search engine.** Naming a page is the developer's job in this round. A search tool is a ranking
  policy, a scraping policy and a rate-limit policy, and it belongs to whoever writes that spec.

## Exit codes

| Code | Meaning |
|---|---|
| 2 | `--web` or `--web-allow` on a command that cannot use it, or `--web-allow` without `--web` |

No new number: a flag on the wrong command is a command line that could not be understood, which is
what 2 already means. A refused host, a timeout or a 404 is a *tool result*, not a process outcome —
the turn continues and the model reads why.

## Risks

- **DNS answers change between the check and the connection.** The address is judged before the request
  is sent; a resolver that returns a public address for the check and a private one for the connection
  (rebinding) is not caught. Mitigation is recorded in `plan.md`: a connect-time callback would pin the
  checked address, and it is a change to make when it is needed rather than a reason to ship neither.
- **The model treats a page as instruction.** `WebRule` says a page grants nothing, and the tool's
  description says the same in one sentence; neither is a guarantee, which is why the rule asks the
  model to *report* a page that tries.
- **A page is huge in a way that is not bytes.** A 60 000-character page is inside the tool's ceiling
  and outside a turn's useful room; the existing result trimmer still applies on top.
- **The HTML reduction is not a parser.** A malformed document is reduced as far as it can be read. It
  never throws and never prints markup; the cost is a page whose prose is missing, which the model will
  notice and say.
- **`--web` on an air-gapped machine looks broken.** The failure is a `HttpRequestException` result with
  the transport message, which is the same thing any `curl` would say.

## Decisions

1. **One tool, off by default, per invocation.** Regular `--web`, repeatable `--web-allow`, no settings
   key and no environment variable.
2. **The address is the boundary, not the host name.** Every resolved address is judged, and every
   redirect hop is judged again; a name the developer typed is the only exemption.
3. **Read-only, so plan mode may fetch.** It changes nothing locally; the network is governed by `--web`
   rather than by the approval mode.
4. **Text, never a binary and never JavaScript.** The reduction is in this repository, which keeps the
   dependency list as it is.
5. **The result states what it is.** Final URL, status, type, bytes, truncation: the model can tell a
   whole page from a fragment, and so can the developer reading the transcript.
