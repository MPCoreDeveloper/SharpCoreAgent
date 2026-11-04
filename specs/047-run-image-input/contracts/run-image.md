# Contract: `run --image`

```text
sharpcoreagent run "<prompt>" --image <path> [--image <path> ...]
```

| Rule | Behaviour |
|---|---|
| command | `run` only; `chat` exits 2 |
| resolution | workspace or read-only attachment |
| type | extension: `.png`, `.jpg`, `.jpeg`, `.gif`, `.webp` |
| per file | at most 4 MiB |
| per turn | sum at most `MaxTurnImageBytes` |
| model | `SupportsImages` required |
| message | first user message carries the images |
| budget | `SeedImages` charges bytes and marks them shown |
| persistence | not written to a session file |

Exit code 2 covers parsing, resolution, type, size and text-only refusals. No model request is made for any refusal.
