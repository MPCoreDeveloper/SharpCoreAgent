# Contract: Artefact retention

| Item | Value |
|---|---|
| property | `McpClientOptions.MaxArtefactDirectoryBytes` |
| default | 256 MiB |
| valid range | 1 MiB–4 GiB |
| scope | top-level files in `.sharpcoreagent/artifacts/` |
| order | `LastWriteTimeUtc`, then file name |
| dedup | none |
| locked file | skipped; continue with the next oldest |
| still over cap | refuse the new artefact with one sentence |
| outside folder | never touched |

The sentence names the measured total, the incoming size and the cap.
