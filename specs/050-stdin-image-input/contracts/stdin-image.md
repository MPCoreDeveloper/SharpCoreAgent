# Contract: `--image -`

```text
sharpcoreagent run "<prompt>" --image -
```

| Rule | Behaviour |
|---|---|
| command | `run` only |
| stdin | must be redirected |
| count | one `-` per command |
| types | PNG, JPEG, GIF, WebP signatures |
| ceiling | 4 MiB |
| failure | one sentence, exit 2 |
| persistence | none |
| mixing | may be combined with path images |

A terminal stdin is refused rather than allowed to block.
