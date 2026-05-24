# ADR-0011 — Content-hash prefix cache for Responses chains

**Status:** Accepted, 2026-05-24.
**Applies only when:** `Proxy.BackendKind == Responses`.

## Context

Responses API supports `previous_response_id` + `store:true`. Caller sends only the new user message; Foundry/OpenAI replays prior context server-side.

Claude Code is stateless: every `/v1/messages` carries full conversation history. Adapter has no signal "this is turn N+1 of conversation X." Without one, the Responses path sends the same full history every turn — zero payload reduction.

## Decision

Adapter maintains an in-memory **prefix cache**: stable content hash → `response_id`. On each incoming Anthropic request:

1. Identify the **prefix**: all messages except the last user message and any trailing tool_result blocks that belong to it. (Pre-tool-result state.)
2. Hash the prefix with a canonical-JSON-serializer-stable hash (BLAKE3 or SHA-256 over a normalized JSON; details in Phase D).
3. Look up `prefixHash → response_id` in the cache.
4. **Hit:** translate as `{previous_response_id: id, input: [<delta_only>], store: true}`. Delta = the new user message plus any tool_result items as `function_call_output` items.
5. **Miss:** translate as `{input: [<full_history>], store: <per ResponsesStorePolicy>}`.
6. After the response completes, compute hash of the **full** request (prefix + last message + assistant response) and store the new `response_id` against it. The chain extends.

### Cache key normalization

To survive Claude Code reformatting messages between turns:

- Strip whitespace inside JSON keys/values where semantically irrelevant.
- Sort `tool_use.input` and `tool_result` JSON object keys.
- Drop top-level `metadata` and timestamps if any.
- Normalize content blocks: a single-string `content` and a single-element array `[{type:"text", text:"…"}]` hash identically.
- Drop the `system` field from the hash **only if** it's been stable across the session — but safer: include it. If Claude Code rotates system prompts, that's a cache miss, which is correct.

### Cache eviction

- LRU, capacity `Proxy.PrefixCache.Capacity` (default 256 entries).
- TTL: `Proxy.PrefixCache.TtlMinutes` (default 30, well under Foundry's 30-day server-side store window).
- On `Proxy.BackendKind` change (config save), invalidate entire cache.
- On adapter restart: empty cache (in-memory).

### Failure modes

- **Stale `response_id` (server purged).** Foundry returns 404 / "previous_response_id not found". Adapter catches, drops the cache entry, retries as a miss (full replay). Surfaced in Monitor as `chainFallback: "stale_id"`.
- **Tool result correlation.** When sending tool_result back to a chained call, the `function_call.call_id` must match a function_call that was emitted in the chained-from response. If client edits history mid-chain (deletes a turn), correlation breaks. Detection: scan incoming tool_results' `tool_use_id`s against the assistant message of the prefix — if any are missing from the prefix's emitted function_calls, fall back to full replay.
- **`store:false` requested by client.** Anthropic doesn't have a `store` field. We honor a hypothetical adapter-side header `X-C2F-No-Store: 1` (future), default off. Until that header lands, `ResponsesStorePolicy` controls it.

## Monitor visibility

Every request gets two new fields in its JSONL line:

```json
{ "chainStatus": "hit" | "miss" | "fallback", "chainPrefixHash": "blake3:abc…", "previousResponseId": "resp_…" | null }
```

Surfaced in the Monitor list as a small badge (✓ chain, ✗ miss, ⚠ fallback) and in the detail panel.

## Consequences

**Gain:**
- Multi-turn sessions in Claude Code send only the delta per turn after first hit — bandwidth + adapter→upstream latency drop noticeably on long sessions.
- Reasoning context preserved server-side across chained turns (Responses-specific bonus).

**Lose:**
- ~300 LOC of correctness-critical caching with subtle invalidation rules. Tests must cover edit-history, tool dance, parallel requests, restart, expiry, stale-id, system-prompt-change.
- Server-side state (`store:true`) holds prompts for up to 30 days at Foundry/OpenAI. Privacy disclosure needed in README + UI Config page tooltip.
- Billing: prior tokens still billed every chained turn (per OpenAI docs). Gain is bandwidth, not cost.

## Rejected alternatives

- **Hash full request including last message.** Cache key changes every turn → never hits. Defeats purpose.
- **Hash just turn count + first message.** False positives (different conversations with same opener). Unsafe.
- **Manual chain ID via Anthropic metadata.** Claude Code doesn't expose a hook to inject conversation IDs into outbound requests.
