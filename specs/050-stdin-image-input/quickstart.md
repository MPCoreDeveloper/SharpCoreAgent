# Quickstart: Stdin image input

Pipe a file:

```powershell
Get-Content shot.png -AsByteStream -Raw | sharpcoreagent run "what is wrong here?" --image -
```

Use a clipboard tool that writes bytes to stdout, or save the paste first. `--image -` reads the bytes once; it cannot
be combined with a second `-`. The type is detected from the bytes, the per-file ceiling is 4 MiB, and the prompt is
still required.
