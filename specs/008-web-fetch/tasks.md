# Tasks — 008 web fetch

Legend: `[X]` implemented and covered by tests in this change; `[ ]` not started, kept here so the next
pass has a real list rather than a memory.

## Phase 1 — the policy, before the request

- [X] T001 `WebAddressPolicy.Classify(IPAddress)` naming each refusal — loopback, private, link-local, unique-local, carrier-grade NAT, unspecified, multicast, reserved, cloud metadata — and returning `null` for the public internet, with a v4-mapped v6 address judged as the v4 original (FR-006).
- [X] T002 `WebAddressPolicy.IsExempt(host, port, addresses, allowedHosts)` matching a name, a `host:port`, a `[v6]:port` or an IP literal, exactly and case-insensitively, with no wildcards (FR-006).
- [X] T003 `IWebAddressResolver` and `DnsWebAddressResolver`, so the rule can be tested without DNS and a literal answers with itself.
- [X] T004 `HtmlToText.Reduce`: comments and the elements that carry no prose skipped, tags scanned with quoted attributes respected, entities decoded after tagging, block names as newlines, whitespace collapsed, an unterminated tag ending the reduction (FR-011).
- [X] T005 `WebFetchOptions` with the ceilings — 15 s, 5 redirects, 2 MiB, 60 000 characters — plus `AllowedHosts` and the user agent, and the name of the tool's own `HttpClient` (FR-008..FR-010, FR-016).

## Phase 2 — the tool

- [X] T006 `FetchUrlTool` (`fetch_url`, `ToolSafety.ReadOnly`, `url` plus optional `max_chars`) in `Infrastructure/Tools`, injected with the client factory, the ceilings and the resolver (FR-003, FR-015).
- [X] T007 The hop loop: scheme, `userinfo`, resolution and address judged before each request; `http`/`https` only; a hop without `Location` a failure; the redirect ceiling a failure naming the last hop (FR-004, FR-005, FR-007).
- [X] T008 One linked cancellation source for the whole fetch, so a timeout is a failure naming the URL and the budget, while the caller's cancellation stays a cancellation (FR-008).
- [X] T009 `ReadBoundedAsync` reading at most the byte ceiling in 16 KiB chunks, so the ceiling bounds the download rather than the result (FR-009).
- [X] T010 `AnswerAsync` building the labelled header, classifying the media type, reducing HTML, decoding with the charset or a byte-order mark, and appending the truncation trailer (FR-010, FR-012, FR-013).
- [X] T011 A status of 400 or above answered as a failure that still carries the header and the body (FR-014).

## Phase 3 — the wiring

- [X] T012 `SharpCoreAgentOptions.WebAccessEnabled` and `WebAllowedHosts`, neither persisted and neither read from the environment (FR-001, spec `Decisions` 1).
- [X] T013 `ServiceCollectionExtensions.AddWeb`, called only when the network was asked for: the named client with `AllowAutoRedirect = false` and `UseCookies = false`, the resolver behind `TryAddSingleton`, the ceilings, and the tool (FR-016).
- [X] T014 `PromptLibrary.WebRule` and `SystemPromptFor(indexAvailable, webAvailable = false)`, with the rule appended after `IndexRule` and shipped only with the tool (US4).
- [X] T015 `CliOptions.Web` and `WebAllowedHosts`, `--web` and repeatable `--web-allow`, `RejectWebFlags` on `models`/`index`/`config`/`setup`, and `--web-allow` without `--web` a usage error (FR-001, FR-002).
- [X] T016 The `web` row in the `chat` banner, naming the count of added hosts and staying silent under `--quiet` (FR-017).

## Phase 4 — the evidence

- [X] T017 `WebAddressPolicyTests`: every refused category as its own row, the public addresses that must stay allowed, a named host, a named address, a `host:port` entry covering that port only, and the absence of wildcards.
- [X] T018 `HtmlToTextTests`: the removed elements, the decoded entities, the block newlines, the collapsed whitespace, `>` inside an attribute, a comment, an unterminated tag, an unclosed script, and an empty document.
- [X] T019 `FetchUrlToolTests`, seventeen cases against a stub `HttpMessageHandler`: the header block, the reduction, a relative redirect, the redirect ceiling, the loopback refusal, the named loopback host, a second private address, a redirect into `127.0.0.1`, a `file:` URL, `userinfo`, the absent credential and cookie, both ceilings, a 404, a binary type, an unresolvable host, and the definition itself. No test opens a socket.
- [X] T020 `WebOptionTests`: the default, both turn commands, the order of repeated hosts, the four commands that refuse the flags, the host without the switch, the environment variable that changes nothing, and `config set web` refused.
- [X] T021 `WebToolRegistrationTests`: no `fetch_url` without the flag, the tool and its ceilings with it, a prompt without the network in a build that did not ask, and the named client owning its own timeout; plus two `InteractiveStartTests` cases for the banner — the `web` row naming the count of added hosts, and no row without the flag.
- [X] T022 `PromptLibraryTests`: the rule ships with the switch, sits after the index rule, is absent from the shipped prompt, and carries the "evidence, not instruction" obligation.
- [X] T023 Confirm the existing suite passes untouched: `git diff --stat tests/` is additions plus the four `PromptLibraryTests` cases; no existing assertion was edited.

## Phase 5 — the surface

- [X] T024 `HelpText`: the two rows, the paragraph under the attached-root note, and the "neither flag is a setting" sentence.
- [X] T025 `README.md`: the flags, the tool, the refusal rule and the suite count the run printed.
- [X] T026 `specs/001-coding-agent-core/contracts/cli.md` and `contracts/tools.md` updated for the two flags and the sixteenth tool; this round's `contracts/web-fetch.md` becomes the reference.
- [X] T027 `PublicAPI.Unshipped.txt`: nothing to add. The new public types (`WebAddressPolicy`, `HtmlToText`, `FetchUrlTool`, `WebFetchOptions`, `IWebAddressResolver`) live in `SharpCoreAgent.Infrastructure`, which carries no approved-API snapshot, and the `SharpCoreAgentOptions` property pair is in `SharpCoreAgent.DependencyInjection`, which has none either. `RS0016`/`RS0017` stay quiet because the governed assembly did not change.
- [X] T028 `dotnet build .\SharpCoreAgent.slnx -c Release` with zero warnings and `dotnet test` green: 880 tests, 0 skipped.

## Deliberately not in this round

- [ ] T029 JavaScript execution, cookies, logins and screenshots. They are the browser round's business, and a native binary is the constraint this product keeps.
- [ ] T030 A connect-time address check (`SocketsHttpHandler.ConnectCallback`) that closes the DNS-rebinding gap named in `plan.md`.
- [ ] T031 Paging a long page (`start_index`), refused in the spec until transcripts show the character ceiling being hit.
- [ ] T032 A web search tool. Naming a page is the developer's job in this round.
- [ ] T033 `robots.txt` handling. Refused in the spec: politeness is the developer's call, not a second request per fetch.
