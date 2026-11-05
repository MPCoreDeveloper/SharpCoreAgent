# Quickstart: Stdin image input

From a shell that can pipe raw bytes (cmd, bash, or a tool that writes the image to stdout):

```powershell
cmd /c "sharpcoreagent run \"what is wrong here?\" --image - < shot.png"
```

PowerShell's own pipeline converts native input to text, so use `cmd /c` with input redirection when the bytes must stay
binary. `--image -` reads the bytes once; it cannot be combined with a second `-`. The type is detected from the bytes,
the per-file ceiling is 4 MiB, and the prompt is still required.
