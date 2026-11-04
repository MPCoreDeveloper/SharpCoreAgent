# Tasks: Per-provider settings

- [x] T001 Add `ProviderSetting.TryParse` for `providers.<provider>.model|base-url`.
- [x] T002 Teach `CanonicalName`, `EnsureSettable` and `WithSetting` the provider form.
- [x] T003 Apply sections after global settings and only for the selected provider; command-line/environment wins.
- [x] T004 Support `config set/get/unset/show` for sections.
- [x] T005 Tests: selected/unselected provider, command-line precedence, set/get/show and unknown provider.
- [x] T006 Update `006` T018, roadmap, help and README.
- [x] T007 Release build, suite and recorded evaluation.

## Evidence

- `AProviderSectionAppliesOnlyWhenThatProviderIsSelected`: Grok section sets `grok-4.6` under `provider: grok` and is
  `null` under `--provider deepseek`.
- `ACommandLineModelBeatsAProviderSection`: `--model grok-4.7` wins.
- `SetAndGetAProviderSection` and `ShowPrintsTheSelectedProvidersSection` round-trip the flat key.
- `AnUnknownProviderSectionIsAUsageError` keeps the setting list honest.

## Out of scope

Per-provider budgets, approvals, credential files, nested JSON objects and `config edit` (T017).
