# 2. Per-target reasoning policy with effort bucketing

Date: 2026-05-22

## Status

Accepted

## Context

The Anthropic Messages API exposes extended thinking via `thinking: {type: "enabled", budget_tokens: N}` on the request and returns `thinking` content blocks containing the model's reasoning trace. Foundry Chat Completions has no single equivalent — the upstream behavior depends on which Foundry-hosted model receives the request:

- GPT-4o / GPT-4.1 / Qwen / Kimi / DeepSeek-V3 do not produce reasoning at all.
- DeepSeek-R1 always reasons; reasoning text is returned in a non-standard `reasoning_content` field on the assistant message. The request has no knob to enable or disable it.
- o-series (o1, o3, o4-mini) accept `reasoning_effort: "low" | "medium" | "high"` on the request and also return `reasoning_content`.

The adapter therefore needs per-target behavior, not a global rule.

## Decision

Introduce a `ReasoningPolicies` config map, keyed by Foundry target model name, with three policy values:

- `none` — drop the Anthropic `thinking` field from the outbound request; ignore any `reasoning_content` on the response.
- `passthrough` — drop the request `thinking` field; if `reasoning_content` is present on the response, wrap it as an Anthropic `thinking` content block before returning to Claude Code.
- `effort` — translate Anthropic `budget_tokens` to OpenAI `reasoning_effort` using bucket cutoffs `< 2000 → low`, `2000–8000 → medium`, `> 8000 → high`. Wrap `reasoning_content` as a `thinking` block on the response. If the client did not send a `thinking` block at all but the target requires `effort`, default to `medium`.

Targets not present in the map default to policy `none`. Bucket cutoffs are not user-configurable in v1.

## Consequences

Positive:
- Single Anthropic-side feature (extended thinking) maps to three structurally different upstream behaviors with one declarative line of config per target.
- Claude Code's UX (showing the thinking trace) works automatically against DeepSeek-R1 and o-series without code changes.
- Adding a new reasoning-capable Foundry model is just a new map entry.

Negative:
- Bucket cutoffs are a judgment call. `budget_tokens` is continuous; `reasoning_effort` has three values. The chosen cutoffs (2k/8k) are not derived from Anthropic-documented behavior — they're a guess at what a Claude Code user means when they raise the slider.
- The mapping is lossy in both directions. `budget_tokens=50000` and `budget_tokens=10000` both bucket to `high`. A client expecting precise budget enforcement will not get it.
- Operators who want different cutoffs must change code, not config, until v2.

## Alternatives considered

**Global single policy.** Rejected — three target families behave structurally differently; a global setting forces a wrong answer for at least one of them.

**Configurable bucket cutoffs.** Rejected for v1 as premature. Add when at least one operator reports the defaults are wrong for their workload.

**Pass `budget_tokens` through verbatim as a non-standard field.** Rejected — Foundry will reject unknown fields on non-vLLM-hosted models, breaking the request entirely.
