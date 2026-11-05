# Contract: `--browser`

| Platform | Command added |
|---|---|
| Windows | `npx -y @playwright/mcp@latest --browser msedge` |
| other | `npx -y @playwright/mcp@latest --browser chromium` |

| Rule | Behaviour |
|---|---|
| commands | `run`, `chat` |
| other commands | exit 2 with the MCP/ browser message |
| settings/env | never |
| composition | appended after explicit `--mcp` entries |
| tool names | `npx_browser_*` from the existing MCP naming rule |
| startup | the existing MCP client; failure is exit 2 |
| cleanup | the existing process-tree dispose |
| download | none by this feature; Edge/Chromium must exist |

The server's own profile, cookies and output directory are its business; screenshots that reach `content[]` are saved
by `010`'s artefact sink.
