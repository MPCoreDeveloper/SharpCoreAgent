# Contract — `fetch_url` and the two flags that enable it

The reference for round `008`. Everything here is a promise to a model, a script or a developer reading a
transcript; the tests in `tests/SharpCoreAgent.Tests/Tools/FetchUrlToolTests.cs`,
`Infrastructure/WebAddressPolicyTests.cs`, `Infrastructure/HtmlToTextTests.cs`, `Cli/WebOptionTests.cs` and
`Infrastructure/WebToolRegistrationTests.cs` hold it.

## Flags

| Flag | Commands | Default | Meaning |
|---|---|---|---|
| `--web` | `run`, `chat` | off | register `fetch_url` for this process |
| `--web-allow <host>` | `run`, `chat` | none | also read a host whose address is refused; repeatable |

- On `models`, `index`, `config` or `setup` either flag is a usage error, exit 2:
  `--web and --web-allow apply to run and chat.`
- `--web-allow` without `--web` is a usage error, exit 2: `--web-allow needs --web.`
- Neither is a settings key (`config set web on` is refused as an unknown name) and neither is read from
  the environment. `SHARPCOREAGENT_WEB` and `SHARPCOREAGENT_WEB_ALLOW` do not exist.
- A resumed `chat` starts with the network off unless the new invocation passes `--web` again.

Without `--web`: `fetch_url` is not in `IToolRegistry`, is not in the definitions sent to the model, and
`PromptLibrary.WebRule` is not in the system prompt.

## Tool

```json
{
  "name": "fetch_url",
  "safety": "read-only",
  "parameters": {
    "type": "object",
    "properties": {
      "url": { "type": "string", "description": "Absolute http or https URL to read." },
      "max_chars": { "type": "integer", "minimum": 1, "maximum": 60000,
                     "description": "Longest text to return; defaults to the tool's ceiling." }
    },
    "required": ["url"],
    "additionalProperties": false
  }
}
```

- `ToolSafety.ReadOnly`, so no approval mode prompts for it and `--mode plan` may call it.
- One effect on success: `Read` with the final URL (after redirects) as the target.
- A call with no `url` is a `ToolArgumentException` like any other tool's missing argument.

## Success result

```text
url          https://example.com/docs/page
status       200 OK
type         text/html; charset=utf-8
bytes        18402
truncated    yes
redirected   from https://example.com/docs

<the reduced text, at most max_chars characters>
```

- `url` is the final URL, after every redirect.
- `status` is `<code> <reason>`; the reason is omitted when the platform did not supply one.
- `type` is the media type without parameters, followed by `; charset=<value>` when the response named one
  and the value is not blank. `unknown` when the response named no type.
- `bytes` is how many bytes of the body were read, followed by
  ` (read stopped at the <n>-byte ceiling)` when the byte ceiling ended the read.
- `truncated    yes` appears when either ceiling cut the answer. A text cut by the character ceiling also
  ends with `[truncated: kept the first <n> of <m> characters]`.
- `redirected   from <requested>` appears only when at least one redirect was followed.
- One blank line separates the header from the text; the text is trimmed.

## Failure and refusal results

A **failure** is a tool result with `IsError` set, which the model reads and reacts to:

| Situation | Message begins |
|---|---|
| not an absolute URL | `'<url>' is not an absolute URL. Give one address, scheme included, …` |
| host does not resolve | `<host> does not resolve: <transport message>` |
| redirect without a location | `<url> answered <status> without a Location header, …` |
| redirect ceiling | `gave up after 5 redirects; the last one pointed at <url>.` |
| timeout | `no answer from <url> within 15 seconds.` |
| transport error | `<url> could not be reached: <message>` |
| status ≥ 400 | the header block, then the body |
| not text | the header block, then `the response is <type>, which fetch_url does not read: …` |

A **refusal** is a tool result with `IsRefused` set — the same shape the path tools use when a path is
outside the workspace:

| Situation | Message begins |
|---|---|
| scheme is not `http`/`https` | `fetch_url reads http and https only, and '<scheme>' is neither. Nothing was requested.` |
| URL carries `userinfo` | `fetch_url sends no credential, so a URL that carries a user name or password is refused.` |
| address refused | `refused: <host> resolves to <addresses>, which is <category>. … --web-allow <host>` |

## The address rule

Judged for the requested URL **and every redirect hop**, before that hop is requested.

| Category | Ranges |
|---|---|
| `loopback` | `127.0.0.0/8`, `::1`, and their v4-mapped forms |
| `private` | `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16` |
| `link-local` | `169.254.0.0/16`, `fe80::/10` |
| `unique-local` | `fc00::/7` |
| `carrier-grade NAT` | `100.64.0.0/10` |
| `unspecified` | `0.0.0.0`, `::` |
| `multicast` | `224.0.0.0/4`, `ff00::/8` |
| `reserved` | `240.0.0.0/4` and above |
| `cloud metadata` | `169.254.169.254`, `fd00:ec2::254` |

A host is refused when **any** address it resolves to falls in one of those rows, and exempt when
`--web-allow` names it. An entry matches a host (`localhost`), a host and port (`localhost:5000`), a
bracketed IPv6 host and port (`[::1]:5000`), or an IP literal (`127.0.0.5`). Matching is exact and
case-insensitive; there are no wildcards and no suffix matching.

## What the request carries

`GET <url>`, with:

- `User-Agent: sharpcoreagent`
- `Accept: text/html,application/xhtml+xml,text/plain;q=0.9,*/*;q=0.1`

and nothing else. No `Authorization`, no `Cookie`, no `Referer`, no cookie container, no automatic
redirect (`AllowAutoRedirect = false`), and no shared handler with the model adapters.

## Ceilings

| Ceiling | Default | Where |
|---|---|---|
| whole fetch, redirects and body | 15 s | `WebFetchOptions.Timeout` |
| redirects | 5 | `WebFetchOptions.MaxRedirects` |
| bytes read from the body | 2 MiB | `WebFetchOptions.MaxBytes` |
| characters returned | 60 000, overridable per call | `WebFetchOptions.MaxChars`, `max_chars` |

None is a CLI flag: a fetch budget is a property of the tool, and the model already has `max_chars` for
the one value it might legitimately want smaller.

## The reduction

`text/html` and `application/xhtml+xml` are reduced by `HtmlToText.Reduce`; every other text type is
returned as it arrived, trimmed. In the reduction:

- `<!-- … -->` comments are removed;
- `head`, `script`, `style`, `svg`, `template` and `noscript` are removed with their content;
- every other tag is removed, and a block-level name writes one line break;
- HTML entities are decoded **after** tagging, so `&lt;b&gt;` is the three characters a reader sees;
- runs of whitespace become one space, and empty lines are dropped.

`image/*`, `application/pdf`, `application/octet-stream` and every other type that is not text are refused
rather than decoded, and a response with no type at all is judged by a binary sniff (a NUL byte in the
first 8 KiB).
