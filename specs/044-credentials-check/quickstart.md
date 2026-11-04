# Quickstart: credential check

```powershell
sharpcoreagent credentials --check
```

```text
provider    credential                  source
deepseek    DEEPSEEK_API_KEY            file:C:\Users\me\AppData\Roaming\sharpcoreagent\credentials.env:1
grok        XAI_API_KEY                 environment
            GROK_API_KEY                missing
openai-compatible
            OPENAI_API_KEY              missing
demo        (no credential required)
```

Narrow the report:

```powershell
sharpcoreagent credentials --check --provider grok
sharpcoreagent credentials --check --credential-file .\keys.env
```

Exit code 0 means every listed provider has at least one accepted key; 1 means one does not. A malformed or unreadable
file exits 3 with the existing file/line message. No value is ever printed.

`models` still shows the catalogue; `credentials --check` answers the narrower question of where the key came from.
