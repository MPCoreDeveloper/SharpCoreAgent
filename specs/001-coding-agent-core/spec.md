# Feature Specification: Coding Agent Core

**Feature Branch**: `001-coding-agent-core`

**Created**: 2026-10-23

**Status**: Draft

**Input**: User description: "We want to build our own coding agent in C# 15 / .NET 11 that supports
several AI models, but mainly targets DeepSeek and Grok."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - One request becomes a finished change (Priority: P1)

A developer points the agent at a repository, describes a change in plain language ("rename `Foo`
to `Bar` in the service layer and fix the call sites"), and walks away from the keyboard. The agent
explores the repository, reads the files it needs, makes the edits, runs the build and the tests,
and reports what it changed and whether the repository is still green.

**Why this priority**: every other feature is an improvement to this loop. Without a loop that
reads, edits and verifies, there is no coding agent, only a chat client.

**Independent Test**: On a small sample repository, ask for a change that touches three files and
is verifiable by a test. Without any further configuration, the agent completes the change, the
build passes, and the report names the files it touched.

**Acceptance Scenarios**:

1. **Given** a workspace with a failing test, **When** the developer asks the agent to fix the
   test, **Then** the agent edits at least one source file, runs the test suite, and reports the
   final status as passing or failing with the output that shows it.
2. **Given** a request that requires reading a file first, **When** the agent starts, **Then** the
   developer sees which file is being read while it is being read, not only afterwards.
3. **Given** a request the agent cannot satisfy with the tools it has, **When** the turn ends,
   **Then** the report explains what was missing instead of claiming success.
4. **Given** an agent turn that has ended, **When** the developer reads the report, **Then** it
   lists every file created, modified or deleted, and every command run, in order.

---

### User Story 2 - The same request, a different model (Priority: P2)

A developer wants the second opinion of a different model, or wants to fall back to a cheaper one
for a routine change. They switch the active model — between DeepSeek and Grok — and repeat the
request in the same session, without rewriting the prompt or restarting the workspace context.

**Why this priority**: the product claim is that the model is swappable. That claim is worth
nothing if a switch costs the developer their context or their prompt.

**Independent Test**: With credentials for both providers, run the same small request twice in one
session, switching the active model in between. Both runs complete, the second one still sees the
workspace and the prior turn, and each run's report names the model that produced it.

**Acceptance Scenarios**:

1. **Given** a session created with DeepSeek as the active model, **When** the developer switches
   to Grok, **Then** the next turn is served by Grok and the report names Grok as the model used.
2. **Given** a conversation that already contains turns, **When** the model is switched, **Then**
   the earlier turns remain visible to the new model and nothing in the workspace changes because
   of the switch.
3. **Given** a model that does not accept tool requests, **When** it is selected for a task that
   needs tools, **Then** the developer is told before the turn starts that this model cannot run
   that task, instead of receiving a partial or fabricated result.
4. **Given** no credentials for a selected model, **When** the developer starts a turn, **Then**
   the turn fails before any network call with a message that names the missing setting.

### User Story 3 - Nothing destructive without a decision (Priority: P3)

A developer runs the agent against a repository that matters. Before the agent writes a file,
deletes one, or runs a command, the developer either approves it, or has chosen a mode in which
only reading is allowed, or has explicitly allowed a class of action for the session.

**Why this priority**: an agent that can write and run commands is an agent that can lose a day of
work. The safe default must exist before the tool belt grows.

**Independent Test**: In the strictest mode, ask for a change that requires a write. The write is
refused, the refusal is visible, and the file on disk is unchanged. In the permissive mode, the
same request completes.

**Acceptance Scenarios**:

1. **Given** the strictest mode, **When** the agent requests a write, **Then** the request is
   refused, the refusal is recorded, and the agent is told why so it can propose a different plan.
2. **Given** the approval mode, **When** the agent requests a write or a command, **Then** the
   developer sees the exact path or command line and can approve or refuse that single request.
3. **Given** a refusal, **When** the agent continues, **Then** it does not retry the same refused
   action unchanged, and the turn ends with a report that names the refused action.
4. **Given** any mode, **When** a tool is asked to touch a path outside the workspace, **Then** the
   request is refused regardless of the approval mode.

---

### User Story 4 - I can see what it cost and what it did (Priority: P4)

A developer reviews a session afterwards: which model served each turn, how many tokens went in and
out, what the loop did step by step, and what it changed. The session can be written to disk and
re-opened later instead of being lost with the terminal.

**Why this priority**: cost and auditability are the difference between a toy and something a team
lets near a shared repository.

**Independent Test**: Run a two-turn session, save it, restart the process and re-open it. The
re-opened session shows the same turns, the same model per turn and the same usage figures, and can
continue with a new turn.

**Acceptance Scenarios**:

1. **Given** a completed turn, **When** the report is shown, **Then** it states input tokens,
   output tokens and the model for that turn, or says "unknown" when the provider did not report
   them.
2. **Given** a session total, **When** more than one turn has run against different models, **Then**
   the total is broken down per model.
3. **Given** a saved session, **When** it is re-opened, **Then** the transcript renders identically
   and a new turn continues the same conversation.
4. **Given** a session file, **When** it is inspected, **Then** it contains no credentials.

---

### User Story 5 - Bring your own endpoint (Priority: P5)

A developer already runs a model behind an OpenAI-compatible API — a local server, a gateway, a
regional provider. They add it by configuration, and the agent treats it like any other model.

**Why this priority**: it costs almost nothing once the provider seam exists, and it keeps the
product from being a two-vendor tool.

**Independent Test**: Configure a custom endpoint against a local stand-in server, run one turn,
and see the turn complete and report the configured model name.

**Acceptance Scenarios**:

1. **Given** a configured endpoint with a base address, model name and credential setting, **When**
   a turn runs, **Then** it completes and the report names the configured model.
2. **Given** a configured endpoint that returns an error, **When** a turn runs, **Then** the error
   is reported as a model failure with the status, and the session survives.

### Edge Cases

- **No credential**: the turn fails before the network call and names the environment variable or
  configuration key that is missing. The value itself is never echoed.
- **Rate limiting or provider outage**: the failure is reported as retryable, and a retry respects
  the provider's instruction when one is given, up to a bounded number of attempts.
- **Stream dies mid-response**: the partial text is kept and marked incomplete; the turn does not
  look successful.
- **Unknown tool requested by the model**: the model receives a tool error naming the available
  tools; the session continues.
- **Tool arguments that are not valid JSON, or that do not match the contract**: the tool does not
  run, and the model receives a validation error.
- **Path escape** (`..`, an absolute path outside the workspace, a symbolic link pointing out): the
  tool refuses the call and the refusal is recorded.
- **Huge or binary file**: the read tool truncates with an explicit marker instead of returning
  megabytes or invalid text.
- **Command that never finishes**: the shell tool has a timeout, kills the process tree, and
  reports the timeout as a tool error.
- **Interrupted by the developer**: the turn stops within one event, the transcript keeps what
  happened, and the session remains usable.
- **Budget exhausted**: the turn ends with a report naming the exhausted budget instead of
  continuing to spend.
- **Model without tool support**: selecting it for an agentic request is refused up front with a
  message that names the limitation.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST accept a natural-language coding request and carry it to completion
  without further developer input, using several model round trips when needed.
- **FR-002**: The system MUST let the model list a directory, read a file, search file contents,
  write a file, delete a file and run a shell command, each as a separately named, described
  capability with a machine-readable argument contract.
- **FR-003**: The system MUST stream the model's output to the developer as it arrives, and MUST
  report each capability invocation before its result.
- **FR-004**: The system MUST support DeepSeek and Grok (xAI) as first-class providers, each
  selectable by name, each with its own documented default endpoint and model identifier.
- **FR-005**: The system MUST support any OpenAI-compatible endpoint through configuration of base
  address, model identifier and credential source, without code changes.
- **FR-006**: The system MUST allow the active model to be changed within a session and MUST
  attribute every turn and every usage figure to the model that served it.
- **FR-007**: The system MUST refuse to serve a turn with a model that cannot honour the request
  when that model lacks tool support.
- **FR-008**: The system MUST enforce a tool-approval mode: read-only, ask-per-action, and
  allow-all. The default MUST be the most restrictive mode that still allows the primary scenario,
  and the effective mode MUST be shown in the session.
- **FR-009**: The system MUST confine every path-taking capability to the workspace root, refusing
  absolute paths outside it, `..` traversal and links that resolve outside it.
- **FR-010**: The system MUST validate tool arguments against the declared contract and report a
  validation failure to the model instead of executing the capability.
- **FR-011**: The system MUST bound every capability result and mark a truncated result as
  truncated.
- **FR-012**: The system MUST bound a turn by iteration count, per-turn output tokens and total
  session tokens, with defaults that are finite, and MUST report which bound was hit.
- **FR-013**: The system MUST apply a timeout to any shell command and terminate the process tree
  on expiry.
- **FR-014**: The system MUST retry a failed model call a bounded number of times when the failure
  is transient, and MUST NOT retry a failure that is not.
- **FR-015**: The system MUST report input and output tokens per turn and per model, and MUST
  report them as unknown rather than zero when a provider does not supply them.
- **FR-016**: The system MUST be able to save a session to disk and re-open it, continuing the
  conversation.
- **FR-017**: The system MUST never write credentials to a session file, a log line, a report or a
  tool result.
- **FR-018**: The system MUST allow a turn to be cancelled, leaving a transcript the developer can
  inspect.
- **FR-019**: The system MUST summarise a completed turn as the files touched and the commands run,
  in order.
- **FR-020**: The system MUST run on Windows and Linux without a platform-specific build.

### Key Entities

- **Session**: a conversation with a workspace, an active model, an approval mode and accumulated
  usage. Contains ordered turns.
- **Turn**: one developer request and everything the agent did to answer it: messages, capability
  invocations, the final answer and the usage it consumed.
- **Message**: a role (developer, assistant, tool result), text content, and — for an assistant
  message — the capability invocations it requested.
- **Capability (tool)**: a name, a description, an argument contract, a safety classification
  (read or destructive) and an execution result.
- **Model descriptor**: a provider, a model identifier, a display name, whether it supports
  capabilities and streaming, and its context limit when known.
- **Approval decision**: the capability invocation, the mode in force, the decision and the reason.
- **Usage record**: model identifier, input tokens, output tokens, or unknown.
- **Workspace**: the root directory a session may touch, and the boundary every path is resolved
  against.
- **Report**: the outcome of a turn (completed, refused, budget exhausted, cancelled, failed) plus
  the files touched, commands run and usage.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A developer can turn a one-sentence change request into a verified change — edit,
  build, test — without typing another command, in under 5 minutes for a change that touches fewer
  than five files.
- **SC-002**: The first streamed character of a model answer reaches the developer within 2 seconds
  of the provider's first token, and the console never appears frozen for more than 2 seconds while
  a turn is running.
- **SC-003**: Switching the active model costs zero re-prompting: the same request is reused and
  the previous turns stay in context in 100% of switches.
- **SC-004**: In the default approval mode, no file outside the workspace and no unapproved write
  or command is executed, across a test suite that includes 100% of the path-escape cases
  (absolute outside path, `..` traversal, escaping link).
- **SC-005**: A refused or failed capability never ends the session: the session survives and can
  accept a next request in 100% of the failure scenarios enumerated above.
- **SC-006**: Cost is answerable after the fact: for any session, a developer can state the tokens
  per model in under one minute from the saved transcript.
- **SC-007**: A developer can add a new OpenAI-compatible model by editing configuration only, in
  under 5 minutes, with no rebuild.
- **SC-008**: A run of the automated suite takes under 60 seconds on a developer machine and makes
  no outbound network call.

## Assumptions

- **Audience**: developers who already have credentials for at least one provider and are
  comfortable in a terminal. No GUI is in scope for this feature.
- **Workspace model**: the agent operates on a single local directory tree; multi-root workspaces
  and remote repositories are out of scope.
- **Language coverage**: the tools are language-agnostic (read, search, write, shell). Running a
  project's build and tests happens through the shell capability, so no language-specific
  intelligence is required for the MVP.
- **Approval default**: "ask per destructive action" is the default because it is the most
  restrictive mode that still allows the primary scenario to complete unattended after the first
  approval.
- **Providers**: DeepSeek and Grok are reached through their OpenAI-compatible HTTP surface; no
  vendor SDK is assumed or required.
- **Model capability data**: the set of models per provider is configuration, not a hardcoded
  catalogue that must be released to stay current.
- **Persistence**: sessions are files in the workspace, not a database; concurrent use of the same
  session is not supported.
- **Cost display**: token counts are shown as reported by the provider; price tables are out of
  scope for this feature.





