# Contract: provider-scoped settings

| Key | Meaning |
|---|---|
| `providers.<provider>.model` | The model used when that provider is selected and no higher layer names one. |
| `providers.<provider>.base-url` | The endpoint used when that provider is selected and no higher layer names one. |

`<provider>` must be one of `ModelCatalog.Providers`; `<setting>` must be `model` or `base-url`.

## Precedence

1. command-line flag
2. environment variable for the same setting
3. matching `providers.<selected>.setting`
4. flat setting
5. built-in default

A section for a provider that is not selected has no effect. `config get` can still read it from the document.

## Config commands

```text
config set providers.grok.model grok-4.7
config get providers.grok.model
config unset providers.grok.model
config show
```

`show` includes the selected provider's section rows; other providers' sections stay in the file and are listed when that
provider is selected. Unknown providers or settings are usage errors.

## Storage

The settings document remains a flat JSON object of string values; no nested `providers` object is written.
