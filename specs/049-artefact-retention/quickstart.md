# Quickstart: Artefact retention

The default cap is 256 MiB, enforced when a server returns an image:

| Event | Result |
|---|---|
| folder below cap | file written normally |
| folder plus incoming over cap | oldest files deleted until it fits |
| a file cannot be deleted | next oldest is tried; refusal only if still over |
| folder missing | no pruning needed |

The transcript keeps the path it recorded even if an old screenshot later ages out. Set a different value only in code
through `McpClientOptions.MaxArtefactDirectoryBytes`; it is not a CLI flag or settings key. There is no deduplication.
