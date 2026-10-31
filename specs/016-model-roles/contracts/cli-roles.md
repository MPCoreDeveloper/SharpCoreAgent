# Contract — roles and the pasted key

## Roles

| Surface | Value | Default | Notes |
|---|---|---|---|
| `--planner <model>` | `provider:id`, an alias, or a bare id | session model | the model a plan turn runs on |
| `--executor <model>` | same | session model | the model an act turn runs on; what `/model` starts from |
| `--reviewer <model>` | same | session model | the model `/review` runs its read-only turn on |
| setting `planner` | same | — | `config set planner grok:grok-4.7` |
| setting `executor` | same | — | — |
| setting `reviewer` | same | — | — |
| `SHARPCOREAGENT_PLANNER_MODEL` | same | — | `SHARPCOREAGENT_EXECUTOR_MODEL`, `SHARPCOREAGENT_REVIEWER_MODEL` likewise |

Resolution order inside one value: `provider:id` (the provider must be known, or the command line is refused with
exit `2`) → a catalogue alias (`ds`, `r1`, `reasoner`, `grok`, `g4`, `g3`) → a bare identifier sent to `--provider`
as written. The last form is resolved after the whole line is parsed, so flag order never matters.

Precedence for the *choice* of value is the one every setting already has: command line, then environment, then
settings file, then the built-in default (which is "no role", i.e. the session model).

## Session behaviour

| Event | Effect |
|---|---|
| `run` with `--mode plan` | the turn runs on the planner's model; with `--mode act`, the executor's; neither named → the `--model`/`--provider` model |
| new `chat` session | the model for the mode it opens in |
| resumed `chat` session | the model recorded in the transcript, whatever the roles say |
| `/mode plan` | switches to the planner's model when one is named, otherwise to the session model; prints `model: <provider:id> (the planner model)` on stderr when it moves |
| `/mode act` | the executor's model, otherwise the session model |
| `/model <x>` | switches the model and makes it the session model, so a later mode switch falls back to it |
| `/roles` | three lines: `plan`, `act`, `review`, each the resolved `provider:id` or `<model> (session model)` |
| `/review [focus]` | one turn on the reviewer's model with approval read-only, then the previous approval mode and model are restored |

## `/review`

- Prompt: `PromptLibrary.ReviewInstruction`, followed by `Focus: <text>` when one was given.
- Approval: `ReadOnly` for the duration, whatever the session was set to. Restored in a `finally`.
- Model: the reviewer's when one is named; otherwise the model the conversation is on. Restored in a `finally`.
- Mode: unchanged. A plan turn would hide the tools a review reads with.
- The turn is appended to the transcript and saved like any other, so `/usage`, `/cost` and `/files` see it.

## First-run key prompt

| Condition | Behaviour |
|---|---|
| every model's provider has a key | nothing is asked |
| a key is missing, both streams are terminals | one question per provider: `paste <NAME>? [Y/n]`, then a masked read |
| a key is missing, either stream is redirected | nothing is asked; the three routes are printed and the command exits `3` |
| the answer is `n` | nothing is written for that provider; the routes are printed if it is still missing |
| the pasted value is blank | nothing is written; `nothing was pasted, so nothing was written.` |
| the paste succeeds | `NAME=value` written to the named credential file or `credentials.env` beside the settings document; the variable is set for this process; `credential-file` recorded in the settings document when the developer named no file |

`config set api-key` stays refused: the prompt writes a credential file, never the settings document, and the only
thing it ever writes into that document is a path.
