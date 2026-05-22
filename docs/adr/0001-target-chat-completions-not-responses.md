# 1. Target Foundry Chat Completions API, not Responses API

Date: 2026-05-22

## Status

Accepted

## Context

Azure AI Foundry exposes two OpenAI-compatible surfaces:

- `POST /openai/v1/chat/completions` — stateless, classic OpenAI shape, supported by every model in the Foundry catalog (GPT, o-series, DeepSeek, Kimi, Qwen, etc.).
- `POST /openai/v1/responses` — stateful (server keeps conversation by `previous_response_id`), first-class reasoning/compaction features, but currently restricted to OpenAI's own GPT and o-series deployments.

Claude2Foundry adapts the Anthropic Messages API for Claude Code into one of these. The Anthropic Messages API is itself stateless — the client sends the full conversation history on every turn.

## Decision

Target Chat Completions. The adapter calls `POST <foundry>/openai/v1/chat/completions` for every Claude Code request.

## Consequences

Positive:
- Stateless-to-stateless mapping. No session fingerprinting, no `(claude_session → previous_response_id)` cache to maintain.
- Full Foundry model catalog reachable, which is the original motivation for this project (DeepSeek, Kimi, Qwen, GPT all in one adapter).
- vLLM's `vllm/entrypoints/anthropic/serving.py` already implements this exact translation direction and can be ported with minimal redesign.

Negative:
- Cannot expose Foundry's server-side compaction. Acceptable — Claude Code has no protocol surface for it.
- Reasoning content arrives as `reasoning_content` on the assistant message (model-dependent) rather than as structured reasoning items. Mapped to Anthropic `thinking` blocks at the translator layer.
- If Foundry deprecates Chat Completions in favor of Responses, this adapter must be reworked. Considered low risk in the 12-24 month horizon because non-OpenAI Foundry models depend on Chat Completions.

## Alternatives considered

**Responses API.** Rejected because (a) non-OpenAI models on Foundry don't support it, defeating the project goal; (b) the stateless-vs-stateful impedance would force the adapter to either fake state or send full history each call (wasting the API's main feature).

**Both, switched by model alias config.** Rejected for v1 as premature complexity. Reconsider if a future feature in Responses becomes load-bearing for Claude Code's UX.
