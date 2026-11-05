# Quickstart: Image input on `run`

```powershell
sharpcoreagent run "what is wrong with this chart?" --image docs/chart.png
```

Repeat the flag to attach more than one picture:

```powershell
sharpcoreagent run "compare these two screenshots" --image before.png --image after.png
```

Rules:

- PNG, JPEG, GIF and WebP only; 4 MiB per file.
- The path must be inside the workspace or a read-only attachment.
- The model must declare image support; `deepseek-flash` and the Grok 4.3+ entries do.
- The prompt is still required.
- `--image <path>` also accepts `-` for a piped image; see `050-stdin-image-input`.
