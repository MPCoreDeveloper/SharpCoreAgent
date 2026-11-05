# Contract: writable attached roots

| Approval | Absolute attachment path | Relative path |
|---|---|---|
| `readonly` | read-only; write refused | primary root |
| `ask` | read-only; write refused | primary root |
| `allow` | readable and writable | primary root |

Tools using the write resolver: `write_file`, `replace_in_file`, `delete_file`, `run_shell` (working directory),
`dotnet_verify` (project), `python_verify`/`node_verify`/other runtime verifiers (target).

A link whose physical target leaves every root is refused in every mode.
