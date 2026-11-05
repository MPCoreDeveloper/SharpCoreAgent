# Contract: `list_artifacts`

| Field | Rule |
|---|---|
| name | `list_artifacts` |
| safety | read-only |
| arguments | optional `limit`, 1–500, default 100 |
| folder | `<workspace>/.sharpcoreagent/artifacts` |
| order | `LastWriteTimeUtc` descending |
| output | count, then path · size · UTC time |
| missing folder | `artifacts: 0`, success |
| links | skipped |
| effect | `Read` on the folder |
| index | never included |
