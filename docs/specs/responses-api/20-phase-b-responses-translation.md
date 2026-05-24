# 20 — Phase B: Responses API translation

**Mode:** Parallel with Phase C. Both depend on Phase A. Worktree-isolated.

Implement the full Anthropic↔Responses translation path: request, non-stream response, SSE stream. Wire it behind `BackendKind == Responses`. No prefix cache yet (every turn full-replay).

## Owned files (write)

- `src/Claude2Foundry/Protocol/OpenAIResponses.cs` (new) — DTOs for Responses request, response, output items (`message`, `reasoning`, `function_call`), input items (`message`, `function_call_output`), stream events.
- `src/Claude2Foundry/Protocol/SerializerContext.cs` — register new DTOs in `AppJsonSerializerContext`.
- `src/Claude2Foundry/Translation/RequestTranslatorResponses.cs` (new) — Anthropic → Responses request.
- `src/Claude2Foundry/Translation/ResponseTranslatorResponses.cs` (new) — Responses → Anthropic non-stream response.
- `src/Claude2Foundry/Translation/StreamTranslatorResponses.cs` (new) — Responses SSE → Anthropic SSE.
- `src/Claude2Foundry/Translation/ToolCallCorrelator.cs` (new) — per-request map `function_call.call_id ↔ Anthropic tool_use.id`. Used by both request (to translate tool_result back) and response (to assign Anthropic-shape ids when emitting tool_use).
- `src/Claude2Foundry/Program.cs` — branch on `BackendKind` to pick translators.
- `tests/Claude2Foundry.Tests/Translation/RequestTranslatorResponsesTests.cs` (new).
- `tests/Claude2Foundry.Tests/Translation/ResponseTranslatorResponsesTests.cs` (new).
- `tests/Claude2Foundry.Tests/Translation/StreamTranslatorResponsesTests.cs` (new).
- `tests/Claude2Foundry.Tests/Translation/ToolCallCorrelatorTests.cs` (new).

## Translation rules — request

Anthropic `MessagesRequest` → Responses `CreateResponseRequest`:

| Anthropic | Responses |
|---|---|
| `system` (string or `[{type:text, text}]`) | `instructions` (string; concatenate text blocks) |
| `messages[]` | `input[]` (item array; see below) |
| `model` (after alias map) | `model` |
| `temperature`, `top_p`, `top_k`, `max_tokens` (alias `max_output_tokens`) | same names, with `max_output_tokens` not `max_tokens` |
| `stop_sequences[]` | not supported in Responses — pass through anyway if upstream accepts; drop with a Monitor warning otherwise |
| `tools[]` (Anthropic shape) | `tools[]` (Responses shape) |
| `tool_choice` | `tool_choice` (rename `auto`/`any`/`tool` mapped: `auto`→`auto`, `any`→`required`, `{type:"tool", name}`→`{type:"function", name}`) |
| `metadata` | `metadata` |
| `stream` | `stream` |
| `thinking: {type, budget_tokens}` | `reasoning: {effort: <budget→effort mapping>}` per ReasoningPolicies config |

**`input[]` construction from Anthropic `messages[]`:**

For each Anthropic message:
- `role: "user"` with string content → `{type:"message", role:"user", content:[{type:"input_text", text}]}`
- `role: "user"` with array content:
  - text blocks → `input_text` items
  - `tool_result` blocks → emit as separate top-level `{type:"function_call_output", call_id: <correlator.lookup(block.tool_use_id)>, output: <stringified content>}` items. **These appear at the top level of `input[]`, not inside a message.**
  - image blocks → `{type:"input_image", image_url: "data:…" | url}` (Responses uses `image_url` for both)
- `role: "assistant"` with array content:
  - text blocks → `{type:"message", role:"assistant", content:[{type:"output_text", text}]}`
  - `tool_use` blocks → `{type:"function_call", call_id: <correlator.register(tool_use.id)>, name, arguments: <stringified input>}`
  - `thinking` blocks (preserving for round-trip) → drop on outbound; Responses won't re-ingest reasoning items from caller-supplied input. Document this.

**Tools translation:**

Anthropic:
```json
{ "name": "x", "description": "y", "input_schema": { … } }
```
Responses:
```json
{ "type": "function", "name": "x", "description": "y", "parameters": { … } }
```
(Note: flat, not nested under `function`. `input_schema` → `parameters`.)

**`store` field:** populate per `Proxy.Responses.StorePolicy`:
- `Always` → `store: true`
- `Never` → `store: false`
- `WhenChaining` → `store: false` in Phase B (no chaining yet). Phase D will switch this to true on chainable requests.

## Translation rules — response (non-stream)

`Response.output[]` → Anthropic `MessagesResponse.content[]`:

| Output item | Anthropic content block |
|---|---|
| `{type:"reasoning", summary:[{type:"summary_text", text}], id}` | `{type:"thinking", thinking: <concat summary>, signature: id}` |
| `{type:"message", role:"assistant", content:[{type:"output_text", text, annotations}]}` | `{type:"text", text}` (drop annotations for now) |
| `{type:"function_call", call_id, name, arguments}` | `{type:"tool_use", id: <correlator.assign(call_id)>, name, input: JSON.parse(arguments)}` |
| `{type:"web_search_call"}`, MCP tool calls, computer_call | drop with Monitor warning (out of scope) |

Preserve order. If a `reasoning` item precedes a `message`, the Anthropic content array starts with `thinking` then `text`.

**Stop reason mapping:**

| Responses `status` / `stop_reason` | Anthropic |
|---|---|
| `completed`, `stop` | `end_turn` |
| `max_output_tokens` | `max_tokens` |
| `tool_calls` (any function_call in output and `status: completed` without further turn) | `tool_use` |
| `content_filter` | `stop_sequence` (closest match) |
| `error` | bubble as 5xx via existing error mapping |

**Usage:**

| Responses | Anthropic |
|---|---|
| `usage.input_tokens` | `input_tokens` |
| `usage.output_tokens` | `output_tokens` |
| `usage.input_tokens_details.cached_tokens` | `cache_read_input_tokens` |
| `usage.output_tokens_details.reasoning_tokens` | add to `output_tokens` (already included by spec) — surface separately in Monitor only |

## Translation rules — stream (SSE)

Responses event names → Anthropic event names:

| Responses event | Anthropic event |
|---|---|
| `response.created` | `message_start` (build message envelope) |
| `response.output_item.added` (item type `message`) | `content_block_start` w/ `type: "text"` |
| `response.output_item.added` (item type `reasoning`) | `content_block_start` w/ `type: "thinking"` |
| `response.output_item.added` (item type `function_call`) | `content_block_start` w/ `type: "tool_use"`, id, name |
| `response.output_text.delta` | `content_block_delta` w/ `type: "text_delta"` |
| `response.reasoning_summary_text.delta` | `content_block_delta` w/ `type: "thinking_delta"` |
| `response.function_call_arguments.delta` | `content_block_delta` w/ `type: "input_json_delta"` |
| `response.output_item.done` | `content_block_stop` |
| `response.completed` | `message_delta` w/ stop_reason + final usage, then `message_stop` |
| `response.error` | bubble as Anthropic `error` event |

Maintain per-stream state: current content block index, current item type, accumulated tool_call arguments string (for token counting / correlation).

## Tool call correlation

`ToolCallCorrelator` is scoped per request (DI scope `Scoped`). Two methods:

```
public sealed class ToolCallCorrelator {
    string Register(string anthropicToolUseId);   // outbound: stores anthropic→opaque; returns opaque to emit as Responses call_id
    string Lookup(string responsesCallId);        // inbound: returns the original anthropic tool_use id (or generate new for first sight)
    string Assign(string responsesCallId);        // response→anthropic: stable id for content block
}
```

Implementation notes:
- For round-trip continuity, when reading prior assistant `tool_use` from Anthropic input and emitting matching Responses `function_call`, **use the same id** as `call_id`. Anthropic ids are already opaque strings safe for Responses.
- When Responses emits a new `function_call` in output (no prior Anthropic id), generate `toolu_<base32(8)>` via `Assign`.

## Acceptance criteria

1. With `BackendKind: "Responses"` and `BackendUrl` pointing at Foundry/OpenAI, a non-stream Anthropic request returns a valid Anthropic response. Smoke: `curl … '{"model":"…","messages":[{"role":"user","content":"hi"}],"max_tokens":50}'`.
2. Same, with `stream: true`, returns a valid Anthropic SSE stream that Claude Code consumes without retrying.
3. Tool round-trip works: tool_use emitted, tool_result sent back, second turn completes.
4. System prompt translates to `instructions`.
5. Reasoning content from o3 / gpt-5.x surfaces as Anthropic `thinking` blocks in both stream and non-stream paths.
6. `BackendKind: "ChatCompletions"` (default) path unchanged — all existing tests still pass.
7. `RequestTranslatorResponsesTests`, `ResponseTranslatorResponsesTests`, `StreamTranslatorResponsesTests`, `ToolCallCorrelatorTests` cover translation rules above. Each translator has ≥10 cases.
8. Monitor JSONL records show `openaiBody` and `openaiResponse` containing Responses-shape payloads when `BackendKind: "Responses"`.

## Out of scope

- Prefix cache / `previous_response_id` chain (Phase D).
- MCP tools, code_interpreter, web_search, computer_use (`type: "function"` only).
- `image_url` for non-data URLs that require fetching by the upstream (assume upstream supports same URL types as Chat path).

## Risks

- **Reasoning token counting.** `output_tokens` includes reasoning tokens per OpenAI spec. Don't double-count.
- **Stream event ordering edge.** `response.output_item.done` for one item may interleave with `response.output_item.added` for the next. The translator must close the prior Anthropic content block before opening the next.
- **`output_text.delta` empty bursts.** Some upstreams emit empty deltas as keep-alive. Don't forward as Anthropic deltas (Claude Code may reject zero-length text_delta).

## Engineer prompt

```
Implement /mnt/c/@Projects/Claude2Foundry/docs/specs/responses-api/20-phase-b-responses-translation.md exactly. Scope is translation only — no prefix cache, no upstream client changes beyond routing path. Requires Phase A merged. All 8 acceptance criteria pass before PR. Include a real end-to-end smoke test (curl against live Foundry with BackendKind=Responses, document the model name used).
```
