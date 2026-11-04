# Quickstart: Per-provider settings

```powershell
sharpcoreagent config set providers.deepseek.model deepseek-flash
sharpcoreagent config set providers.grok.model grok-4.7
sharpcoreagent config set providers.openai-compatible.base-url https://example.invalid/v1
```

Switch providers without rewriting the file:

```powershell
sharpcoreagent chat --provider grok
```

The Grok section applies only to that command. A one-run override still wins:

```powershell
sharpcoreagent chat --provider grok --model grok-4.6
```

Read a section directly, even while another provider is selected:

```powershell
sharpcoreagent config get providers.grok.model
```

`config show` prints the selected provider's section with its value and source. `config unset providers.grok.model`
removes it. Only `model` and `base-url` can be scoped; budgets and approval remain session-wide.
