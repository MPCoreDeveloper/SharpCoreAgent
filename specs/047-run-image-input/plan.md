# Implementation Plan: Image input on `run`

## Parsing

`CliOptions.Images` is a per-invocation list filled by repeated `--image`. The `chat` branch refuses a non-empty list.
The flag is not a settings key and has no environment variable, for the same reason `--add-root` and `--mcp` do not:
it is authority for one task.

## Loading

`ImageFile.TryLoad` resolves the path through `PathSafety.TryResolveRead`, maps the extension to a provider media type,
checks `FileInfo.Length` against `TextLimits.MaxImageBytes`, reads the bytes, and hands the base64 to
`ImagePayload.TryCreate`. That reuses the ceiling and normalisation the MCP and fetch doors already use.

`RunCommand` loads every path before the first model call, refuses a text-only model, and checks the total against
`AgentOptions.MaxTurnImageBytes`.

## The turn

`CodingAgent.RunTurnAsync` gains an overload taking `IReadOnlyList<ImageContent>` before the observer. The original
four-argument method delegates with an empty list, so every existing caller and test keeps its meaning. The prompt
message is `ChatMessage.User(prompt, images)`, and `TurnState.SeedImages` charges their bytes to the turn's picture
budget and adds them to the already-shown set, so a tool cannot re-attach the same bytes or exceed the ceiling.

## Why the prompt stays required

An image-only `run` would need a new empty-prompt rule for every command and provider. The flag is meant to pair a
picture with an instruction, and the current error stays the clearer one.

## Tests

`ImageFileTests` covers a valid PNG, unsupported extension, outside path and the 4 MiB ceiling. `CliOptionsTests`
covers run-only parsing. `CodingAgentTests` covers the image on the first request, text-only refusal and no request.
