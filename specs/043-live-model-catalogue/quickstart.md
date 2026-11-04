# Quickstart: Refresh the model catalogue

The catalogue is checked in and pinned; this is a maintainer task, not a runtime lookup.

```powershell
# Inspect the current identifiers against the checked-in lists.
sharpcoreagent models

# Live check (requires the provider key; never print the key).
$headers = @{ Authorization = "Bearer $env:DEEPSEEK_API_KEY" }
(Invoke-RestMethod https://api.deepseek.com/models -Headers $headers).data.id

$headers = @{ Authorization = "Bearer $env:XAI_API_KEY" }
(Invoke-RestMethod https://api.x.ai/v1/models -Headers $headers).data.id
```

Update the matching `Catalog`, its comments, and the exact-list tests in `ModelCatalogTests`. Keep retired identifiers
for transcripts; do not add a model the provider advertises only for image/video generation. A context window or image
capability comes from the provider's metadata or model page, never from a guess.

The current defaults remain `deepseek-flash` and `grok-4.7`.
