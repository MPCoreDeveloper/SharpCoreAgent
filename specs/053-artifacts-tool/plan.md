# Implementation Plan: Artefact catalogue

`ListArtifactsTool` resolves the fixed `McpArtefactSink.Folder` under the workspace root, enumerates top-level files,
skips reparse points, sorts by UTC write time and formats a bounded block. `ToolArgumentReader` validates `limit`, so a
malformed call is refused before the directory is touched. It is registered beside `list_roots`, unconditionally and
read-only.

The tool does not call `PathSafety` for a caller path because it has no path argument; the directory itself is fixed and
the entries are only named, not opened.

## Tests

`ListArtifactsToolTests` covers an absent folder, newest-first ordering with a limit, the tool name, safety and schema.
