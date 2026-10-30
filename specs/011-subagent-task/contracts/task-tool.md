# Contract: the `task` tool

**Feature**: `011-subagent-task` — see [../spec.md](../spec.md) and [../plan.md](../plan.md)

This is the whole surface a model sees and the whole behaviour a caller may rely on. Anything not stated here is not
implemented.

## Schema advertised to the model

```json
{
  "type": "object",
  "properties": {
    "description": {
      "type": "string",
      "description": "Short label for the work, shown in the transcript and in an approval prompt: 'find every caller of PriceCalculator'."
    },
    "prompt": {
      "type": "string",
      "description": "The complete instruction. The sub-agent sees this and nothing else of this conversation, so state the goal, the paths you already know, and what done looks like."
    },
    "max_iterations": {
      "type": "integer",
      "minimum": 1,
      "maximum": 24,
      "description": "Round trips the sub-agent may take before it must report. Defaults to 12."
    }
  },
  "required": ["prompt"],
  "additionalProperties": false
}
```

| Argument | Required | Rule | Refusal |
|---|---|---|---|
| `prompt` | yes | non-empty string | `ToolArgumentException`: `prompt must not be empty` |
| `description` | no | ≤ 120 characters; defaults to the prompt's first line, ellipsised | a longer one is cut, not refused — it is a label |
| `max_iterations` | no | 1–24, default 12 | out of range is `ToolArgumentException` naming the range |

## Result

Success is one string in two parts, with the child's effects attached:

```text
task · <description> · <N> model calls · <T> tokens
<the child's final message, verbatim>
```

- `<N>` is `AgentReport.Iterations`; `<T>` is the input plus output tokens of `AgentReport.Usage`, or `unknown` when
  the provider did not say.
- The text is bounded by `AgentOptions.MaxToolResultChars`, and `ToolResult.IsTruncated` is set when it was cut.
- **Effects**: every `Created`, `Modified`, `Deleted` and `CommandRun` effect from the child's
  `AgentReport.ToolResults`, in order. `Read` effects are dropped. This is what lets the parent's `VerifyGate`,
  `FilesTouched`, `CommandsRun` and console record describe the child's work.
- **No content from inside the child** — its messages, tool results and reasoning never appear in the parent's
  transcript.

Failure is a `ToolResult` with `IsError` set and the same header, followed by one sentence:

| Child outcome | Result text after the header |
|---|---|
| `Completed`, no final text | `the sub-agent completed without saying anything.` |
| `BudgetExhausted` | `the sub-agent ran out of its <n> model calls without answering (BudgetExhausted). <detail>` |
| `Failed` | `the sub-agent could not run (Failed). <detail>` |
| `Cancelled` (child only) | `the sub-agent was cancelled before it answered.` |

A failure carries the child's effects too: a child that edited a file and then ran out of model calls did change the
disk, and a `task` failure that reported nothing would let the parent's turn look untouched.

A cancelled *parent* is not a result: `OperationCanceledException` propagates, as the tool contract requires.

## Approval matrix

`task` is `ToolSafety.Destructive`. It is refused before it runs in plan mode and in `readonly` approval; the child's
mode is derived from the parent's, and the child never prompts.

| Parent `Approval` | `task` runs? | Child `Approval` | Child may write? |
|---|---|---|---|
| `ReadOnly` | no — refused | — | — |
| `Ask` | yes, after one approval of the prompt | `ReadOnly` | no; a write is refused and reported to the child |
| `AllowAll` | yes, no prompt | `AllowAll` | yes |

The preview shown at approval is the delegated prompt and the iteration ceiling, not the argument JSON.

## Invariants

1. **No recursion.** The child's registry is the registry this tool was given, minus `task`, filtered by name at
   execution time.
2. **No nested prompts.** The child's mode is `AllowAll` or `ReadOnly` and the tool holds a refusing `IToolApprover`,
   so no code path from inside a `task` call can raise a question.
3. **One level.** A child cannot delegate, so the depth of any call tree is one.
4. **Bounded.** At most 24 child model calls per invocation, whatever the model asks for.
5. **Cheap to fail.** Every failure is a `ToolResult` the model can read and react to, except cancellation.
6. **Registered unconditionally.** `AddTools` puts `task` in the default build — no flag adds it and none removes it — and
   rule 8 of `PromptLibrary.SystemPrompt` says when to call it, so the instruction and the tool always ship together. The
   child's system prompt is `PromptLibrary.SubAgentPrompt`, which says in its first paragraph that the delegating
   conversation is not visible to it.

