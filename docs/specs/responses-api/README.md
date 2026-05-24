# Responses API + Multi-Backend — spec set

**Status:** Draft, 2026-05-24.

Adds OpenAI **Responses API** as a second backend protocol alongside the current Chat Completions path, generalizes the backend so any OpenAI-compatible upstream (Foundry, OpenAI direct, OpenRouter, Groq, Together, vLLM, llama.cpp) works, and adds a content-hash prefix cache that turns multi-turn Anthropic conversations into stateful Responses chains via `previous_response_id`.

## Reading order

1. `adr/0009-responses-api-backend.md` — why add Responses, what it buys, what it doesn't.
2. `adr/0010-multi-backend-generalization.md` — auth schemes + arbitrary baseUrl.
3. `adr/0011-prefix-cache-stateful-chain.md` — content-hash → response_id table.
4. `10-phase-a-backend-config.md` — config schema additions + auth abstraction. **Blocker for B/C/D.**
5. `20-phase-b-responses-translation.md` — request/response/stream translators. **Parallel with C.**
6. `30-phase-c-upstream-abstraction.md` — `IUpstreamClient` + dual-path routing. **Parallel with B.**
7. `40-phase-d-prefix-cache.md` — stateful chain. **Serial after B+C.**
8. `50-phase-e-integration.md` — UI Config toggle, tests, docs, ship.

## Glossary

- **Chat Completions** — original OpenAI completion protocol. `messages[]` array, full history every turn, `chat.completion.chunk` SSE. Default backend kind.
- **Responses** — newer OpenAI protocol. `input` + `instructions`, typed `output[]`, `response.output_text.delta` SSE, stateful via `previous_response_id`.
- **Backend kind** — `ChatCompletions` or `Responses`. Process-wide setting, restart to switch.
- **Auth scheme** — `ApiKey` (Azure/Foundry: `api-key` header) or `Bearer` (OpenAI direct: `Authorization: Bearer …`).
- **Prefix chain** — series of Anthropic requests where each is the previous one plus one new turn. Cache key = stable hash of all but the last user message.
- **Reasoning items** — Responses output items of type `reasoning` (o3, gpt-5.x). Mapped to Anthropic `thinking` content blocks.

## Decision shorthand

| Question | Answer |
|---|---|
| Prefix cache + chain? | Yes |
| Generalize backend kind + auth + URL same iteration? | Yes |
| Reasoning items → Anthropic thinking? | Yes |
| Toggle granularity? | Process-wide, restart to switch |

## Out of scope

- Anthropic→Responses for non-Foundry endpoints with proprietary extensions (MCP servers, computer-use, code interpreter). Pass-through only for `type: "function"` tools.
- Live A/B between backends per request.
- Caching responses across processes (cache is in-memory, lost on restart).
- Anthropic batch API.
