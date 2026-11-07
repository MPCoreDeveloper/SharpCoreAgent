# Tasks — framework cues (058)

- [x] T001 Domain: `CodeCueRef`, `CueKinds`, `ICodeIndex.FindCuesAsync`, `PublicAPI.Unshipped.txt`.
- [x] T002 Indexing: `CueRetriever` rule table; `SharpCoreDbCodeIndex.FindCuesAsync`; `SharpCoreAgentIndex` delegate.
- [x] T003 Infrastructure: `FindCuesTool`; `LeasedCodeIndex` delegate; DI registration.
- [x] T004 Tests: `CueRetrieverTests`; schema/render in `RetrievalToolTests`; integration in `CodeIndexSearchTests`; `LeasedCodeIndexTests` fake; `TaskToolRegistrationTests` surface (28 → 29).
- [x] T005 Docs: README count/list and `contracts/retrieval-tools.md`. The system prompt keeps its six core index tools; `find_cues` is described by its own tool definition, so the demo token-calibration tests stay fixed.
- [x] T006 `dotnet build` (0 warnings) and `dotnet test` (Release) green.

## Contract — `find_cues` schema

```json
{"type":"object","properties":{"kind":{"type":"string","enum":["any","route","test","dependency_injection","database_call"],"description":"Which framework cue to find. Defaults to any."},"text":{"type":"string","description":"Optional text a cue's target, path or captured detail must contain."},"max_results":{"type":"integer","minimum":1,"maximum":100,"description":"Defaults to 40."}},"additionalProperties":false}
```
