# Implementation Plan: Per-provider settings

**Feature**: `045-provider-settings`
**Status**: implemented 2026-11-04

## Parsing

`ProviderSetting.TryParse` recognizes `providers.<provider>.<setting>` when the provider is in `ModelCatalog.Providers`
and the setting is `model` or `base-url`. It canonicalizes provider case and setting spelling.

`CliOptions.CanonicalName` checks flat settings first, then the provider form. `EnsureSettable` therefore accepts both
without widening `SettableNames` (which remains the list of global settings). `WithSetting` applies a section only when
the current `Provider` matches; otherwise it returns the options unchanged.

## Merge order

`ApplySettings` walks flat keys first, so `provider` from the file is known before sections are considered. It collects
provider sections, validates every one, then applies those whose provider equals the merged provider. Before applying, it
skips a section when the same setting was typed on the command line or supplied by its environment variable.

`ConfigCommand.Set` validates a provider section against a temporary `Provider` so `config set providers.grok.model`
checks the value against Grok even when the current session is DeepSeek. `Get` reads the document for an unselected
provider; `Show` appends the selected provider's section rows; `Unset` removes through canonical matching.

## Why only model and base-url

A model and an endpoint are properties of a backend. Approval, mode, budgets and a credential file are properties of a
session: a per-provider copy would create two active values with no visible precedence. Keeping the section small also
keeps `config show` readable.

## Tests

`ConfigCommandTests` covers selected/unselected provider application, command-line precedence, set/get/show round-trip
and an unknown provider section. The settings file remains a flat sorted map, so the existing save/read tests stand.
