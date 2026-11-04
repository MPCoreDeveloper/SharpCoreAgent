# Contract: MCP logging notifications

| Input | Output |
|---|---|
| `notifications/message` with string `data` | `server log [level] logger: text` |
| object/array `data` | `server log [level] logger: <compact JSON>` |
| missing level | `info` |
| missing logger | no logger segment |
| missing data | empty message |
| any other notification | `notification: <method>` |
| message longer than 500 chars | first 500 + `…` |

The output goes only to the verbose log callback. It is not a tool result, transcript message or stdout write.
