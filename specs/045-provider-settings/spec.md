# Feature Specification: Per-provider settings

**Feature**: `045-provider-settings`
**Status**: implemented 2026-11-04

## Problem

`config set model …` is a single value, so keeping two backends configured means switching the top-level `provider` and
`model` together every time. `006` T018 asks for provider sections such as `providers.grok.model`, while command-line and
environment decisions must still win.

## What ships

- `providers.<name>.model` and `providers.<name>.base-url` settings.
- The section applies only when that provider is the selected provider.
- A global `--provider` (or `SHARPCOREAGENT_PROVIDER`) selects which section applies.
- A command-line `--model`/`--base-url` or the matching environment variable beats the section.
- `config set/get/unset/show` understand the section keys.
- Unknown providers and non-provider settings are refused with the existing setting list.

## User stories

1. **Keep both backends configured.** `config set providers.deepseek.model deepseek-flash` and
   `config set providers.grok.model grok-4.7` coexist.
2. **Switch provider only.** `--provider grok` picks the Grok section without rewriting the file.
3. **Override one run.** `--model grok-4.6` beats `providers.grok.model`.
4. **Read one value.** `config get providers.grok.model` prints the stored value even when Grok is not selected.
5. **Repair the file.** `config unset providers.grok.model` removes the exact section.

## Functional requirements

- **FR-001**: A provider section is `providers.<provider>.<setting>` with a known provider and setting in
  `model`/`base-url`.
- **FR-002**: `CanonicalName`, `EnsureSettable`, `WithSetting`, `RemoveMatching` and `Get` understand the form.
- **FR-003**: Global settings merge first; provider sections then apply only when `merged.Provider` matches.
- **FR-004**: A section for another provider is ignored, not applied to the current session.
- **FR-005**: Command-line and environment values for the same setting win over the section.
- **FR-006**: `config show` appends the selected provider's sections with their value and source.
- **FR-007**: `config get` on a section reads the document directly when that provider is not selected.
- **FR-008**: Only `model` and `base-url` are scoped; budgets, approval, mode and credential-file stay global.
- **FR-009**: No schema migration is needed: the settings document remains a flat string map.

## Non-goals

- Per-provider budgets, approvals or credential files.
- Nested JSON provider objects.
- Changing the precedence of the existing flat settings.
- `config edit` (T017 remains open).

## Success criteria

- A Grok section applies under `provider: grok` and is ignored under DeepSeek.
- Command-line model wins over the section.
- `config set/get/show/unset` round-trip the key.
- Full suite, Release build and recorded evaluation remain green.
