# ADR-0009 — Add OpenAI Responses API as alternate backend protocol

**Status:** Accepted, 2026-05-24.
**Supersedes:** `docs/adr/0001-target-chat-completions-not-responses.md` (which deferred Responses).

## Context

Current adapter targets OpenAI Chat Completions on Azure Foundry. Responses API is the newer, GA, OpenAI-native protocol with:

- `input` + `instructions` instead of `messages[]`.
- Typed `output[]` array (reasoning, message, function_call) instead of `choices[].message`.
- Stateful multi-turn via `previous_response_id` + `store:true` — caller sends only the new user message after the first turn.
- Different SSE event names (`response.output_text.delta`, `response.completed`, `response.function_call_arguments.delta`, `response.error`).
- Flat tool spec (`{type:"function", name, parameters}`) vs Chat's nested (`{type:"function", function:{name, parameters}}`).
- Tool result item is `{type:"function_call_output", call_id, output}` inside `input[]` instead of `{role:"tool", tool_call_id, content}` in `messages[]`.

Foundry endpoint: `POST {baseUrl}/openai/v1/responses`. Same `api-key` auth. v1 GA.

## Decision

Add `Proxy.BackendKind` ∈ `{ChatCompletions, Responses}`. Default `ChatCompletions` (no behavior change for existing users). When `Responses` is selected:

1. Adapter posts to `/openai/v1/responses` not `/openai/v1/chat/completions`.
2. Anthropic→OpenAI request translation uses Responses shape.
3. OpenAI→Anthropic response translation reads `output[]` typed items.
4. SSE translation maps Responses event names to Anthropic event names.
5. Reasoning items map to Anthropic `thinking` content blocks (see ADR-0009 §Reasoning).
6. Tool calls map via `call_id ↔ tool_use.id` correlation table maintained per request.

Switch requires restart (bootstrap field, like `BackendUrl`).

## Reasoning items mapping

Responses emits `output[].type == "reasoning"` items with `summary[]` (and optional `content[]` for some models). Anthropic Messages has `content[].type == "thinking"` with `thinking: string` and `signature: string`.

- `reasoning.summary[].text` → concatenate → Anthropic `thinking` block `thinking` field.
- `reasoning.id` → Anthropic `signature` field (opaque to client).
- If `summary` empty but `content` non-empty (some o3 variants), fall back to `content[].text`.
- Reasoning items appear before message items; preserve order in assembled response.
- Streaming: `response.reasoning_summary_text.delta` → Anthropic `content_block_delta` with `type: "thinking_delta"`.

## Consequences

**Gain:**
- Parity with OpenAI clients that target Responses.
- Foundation for prefix-chain (ADR-0011), which is where payload reduction actually lives.
- Better reasoning visibility for o3/gpt-5.x (current Chat path drops reasoning).
- Cleaner output shape (typed items vs single `content` string with tool_calls sidecar).

**Lose:**
- Two parallel translator paths to maintain. Mitigated: shared DTOs for tool result correlation, shared token counter, separate stream translator.
- Test surface ~2x for translation modules.
- Stream translator is new code (different event vocabulary).

## Rejected alternatives

- **Drop Chat Completions, force Responses.** Breaks every existing endpoint that hasn't shipped Responses (older Foundry deployments, llama.cpp, vLLM until they catch up).
- **Per-model-alias backend kind.** Way more config surface for marginal flexibility. Process-wide is simpler and adequate.
- **Auto-detect via probe.** Brittle. Endpoint may exist but be flagged for some models only. Explicit config wins.

## Open questions

- Do we expose `store:false` for the Responses path? Yes, opt-out via `Proxy.ResponsesStorePolicy` ∈ `{Always, Never, WhenChaining}` (default `WhenChaining` — only persists when we plan to chain). Detailed in Phase B.
