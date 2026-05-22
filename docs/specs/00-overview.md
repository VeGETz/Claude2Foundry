# 00 — Overview

## What this is

Claude2Foundry is a stateless HTTP adapter. It accepts requests on the Anthropic Messages API and forwards them, after rewriting the request and response shape, to the OpenAI-compatible Chat Completions endpoint of Azure AI Foundry. The downstream client is Claude Code (Anthropic's CLI). The upstream backend is a single Azure AI Foundry resource hosting any model from its catalog: GPT-4o/4.1, o-series, DeepSeek-V3 and -R1, Qwen, Kimi, etc.

## Why

Claude Code only speaks the Anthropic Messages API. Foundry hosts many useful non-Claude models behind an OpenAI-compatible API. The adapter lets a developer use Claude Code with any Foundry-hosted model.

## Scope (in)

- Translate `POST /v1/messages` (Anthropic) ↔ `POST /v1/chat/completions` (Foundry/OpenAI), streaming and non-streaming.
- Translate `POST /v1/messages/count_tokens` locally using a configurable tokenizer per Foundry target.
- Map Anthropic-only fields (tool blocks, thinking, cache_control, etc.) to OpenAI equivalents or drop them deterministically.
- Map upstream HTTP errors to Anthropic-shaped error responses Claude Code can render and retry on.
- Single-target deployment: one adapter instance points at one Foundry resource.

## Scope (out)

- No model hosting, no local inference, no engine.
- No support for Foundry's stateful Responses API (`/openai/v1/responses`). See [ADR-0001](../adr/0001-target-chat-completions-not-responses.md).
- No support for Foundry Local (on-device runtime).
- No multi-vendor routing (no DeepSeek-direct, no OpenRouter — Foundry only).
- No TLS termination in v1 — Kestrel's standard config remains available but is not part of the product surface.
- No daemon/service hosting in v1 — foreground process only.
- No AAD / managed-identity auth in v1 — API key from env var only.

## Success criteria

1. Claude Code (latest CLI) with `ANTHROPIC_BASE_URL=http://localhost:8787` can complete an interactive chat session against `DeepSeek-V3` on Foundry: prompt → response, tool use, multi-turn.
2. Streaming responses arrive in Claude Code's UI token-by-token, with tool call arguments streamed.
3. Switching `ModelAliases` and restarting the adapter changes the upstream model with no other change.
4. A reasoning-enabled target (`o4-mini` with `ReasoningPolicies` set to `effort`, or `DeepSeek-R1` with `passthrough`) surfaces the model's reasoning trace as Anthropic `thinking` blocks in Claude Code's UI.
5. An upstream 429 from Foundry surfaces as a Claude-Code-recognizable `rate_limit_error` and Claude Code retries.

## Reference implementation

`vllm/vllm/entrypoints/anthropic/` in this repo. Three files matter:

- `protocol.py` — Pydantic models for both Anthropic and OpenAI sides. Port to C# records.
- `serving.py` — translators. All `_convert_*` instance methods are pure translation and portable. Anything calling `self.create_chat_completion`, `self.render_chat_request`, or referencing `EngineClient` is engine glue and replaced with `HttpClient` to Foundry.
- `api_router.py` — FastAPI routes. The two route shapes (`/v1/messages`, `/v1/messages/count_tokens`) port to Minimal API endpoints.

The translation logic in vLLM is the design source of truth where this spec is silent. Where this spec contradicts vLLM, this spec wins.
