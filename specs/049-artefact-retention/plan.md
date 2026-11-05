# Implementation Plan: Artefact directory retention

`McpArtefactSink.EnsureRoom` runs after the per-file ceiling and measured decode, and before the name is chosen. It
lists only the top-level files in the dedicated folder, sums their lengths, and returns `null` when the incoming file
fits. Otherwise it walks the files ordered by `LastWriteTimeUtc` and name, deleting each and subtracting its length until
the sum fits. A failure to read or delete one file is swallowed and the next candidate is tried; if the sum is still over
the cap, the sink returns a retention sentence through the existing `Refuse` path.

The folder is resolved from the workspace root, not from a caller-supplied path, so the sink cannot delete outside it.
The name selection happens after pruning, so a deleted file's name is available for reuse and the ordinal rule is
unchanged.

## Tests

`TheOldestArtefactsAreDeletedWhenTheFolderWouldExceedItsCeiling` creates two 600 KiB files under a 1 MiB cap, sets the
older timestamp explicitly, runs a 16-byte image save, and asserts the oldest is gone, the newer remains and the new file
exists.
