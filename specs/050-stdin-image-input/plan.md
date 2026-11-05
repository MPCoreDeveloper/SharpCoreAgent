# Implementation Plan: Stdin image input

`ImageFile.TryLoadStdin` reads the stream in 80 KiB chunks into a `MemoryStream`, stopping before writing a byte that
would take it over `TextLimits.MaxImageBytes`. It then sniffs the four signatures and delegates to
`ImagePayload.TryCreate`, so the media type and size rules are the same ones the path and MCP doors use.

`RunCommand` detects `-`, refuses a second one, refuses a terminal stdin, and reads `Console.OpenStandardInput()`. The
result joins the same `images` list as a path image; the model-support check, `MaxTurnImageBytes` check and
`RunTurnAsync` call are unchanged.

## Why signatures rather than a flag

A separate `--image-type` flag would be a second thing to get wrong, and an extension would be absent. The four formats
have unambiguous signatures, which is the one fact stdin provides.

## Tests

`ImageFileTests` covers a valid PNG, unknown bytes and the 4 MiB cap. The path-loader tests remain.
