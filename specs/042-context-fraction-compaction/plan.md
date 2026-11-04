# Implementation Plan: Context-fraction automatic compaction

**Feature**: `042-context-fraction-compaction`
**Status**: implemented 2026-11-04

## Measurement

`PromptTokenEstimator` sums:
- each message's content plus a fixed per-message overhead;
- each tool call's name and raw argument JSON;
- each image's base64 length;
- each advertised tool's name, description and schema.

The sum is divided by four, clamped to `int.MaxValue`. The estimate deliberately errs high for images and source text;
it is a compaction trigger, not a usage report.

## Trigger

`CodingAgent.CompactAsync` first applies the existing `MaxContextMessages` rule. It then reads the answered model's
`ContextWindow`; with a positive window and a positive fraction it computes
`threshold = floor(contextWindow * fraction)`.

While the current `MessagesFor`/`DefinitionsFor` estimate is above the threshold and a cut is possible, it calls
`AgentSession.Compact(1)`. The newest message is always preserved. If the newest message is a tool result, `Compact`
refuses to orphan it and the loop breaks; the request goes out unchanged rather than becoming invalid.

The event is emitted once after all cuts with the total `Dropped` and the last successful `Kept`.

## Options and CLI

- `AgentOptions.CompactAtContextFraction`, default `0.8`, validated as `0` or `[0.1, 1]`.
- `SharpCoreAgentOptions.CompactAtContextFraction` and `CliOptions.CompactAtContextFraction`.
- `--compact-at <fraction>` is parsed on `run` and `chat`; `ToSharpCoreAgentOptions` carries it into the loop.

The message-count limit is still independent: a host can disable one trigger without disabling the other.

## Safety

- `AgentSession.Compact` keeps leading system messages and never cuts in front of a tool result.
- No provider-specific tokenizer, network call or new tool is added.
- The loop has a progress guard (`Messages.Count` must fall) to prevent a summary/system floor from spinning.
- The existing `ContextCompacted` observer event is the only new user-visible surface.
