# 40 — Phase D: Prefix cache + stateful chain

**Mode:** Serial after Phase B+C merged. One agent.

Wire `previous_response_id` chaining onto the Responses path. In-memory LRU + TTL prefix cache. Monitor surfacing.

## Owned files (write)

- `src/Claude2Foundry/Chain/PrefixCache.cs` (new) — LRU cache (`Dictionary` + `LinkedList`), capacity + TTL.
- `src/Claude2Foundry/Chain/PrefixHasher.cs` (new) — canonical hash of Anthropic prefix.
- `src/Claude2Foundry/Chain/ChainCoordinator.cs` (new) — orchestrates lookup, request transformation, post-response insert.
- `src/Claude2Foundry/Translation/RequestTranslatorResponses.cs` (edit) — accepts an optional `previousResponseId` and `inputOverride` (delta only) to emit chained payload.
- `src/Claude2Foundry/Program.cs` — call `ChainCoordinator` when `BackendKind == Responses` && `PrefixCache.Enabled`.
- `src/Claude2Foundry/Monitor/JsonlWriter.cs` (edit) — append `chainStatus`, `chainPrefixHash`, `previousResponseId` to `request.translated` line.
- `ui/src/components/MonitorRow.tsx` — small badge for chain status.
- `tests/Claude2Foundry.Tests/Chain/PrefixHasherTests.cs` (new).
- `tests/Claude2Foundry.Tests/Chain/PrefixCacheTests.cs` (new) — LRU eviction, TTL expiry, restart-clear.
- `tests/Claude2Foundry.Tests/Chain/ChainCoordinatorTests.cs` (new) — hit/miss/fallback flows.
- `tests/Claude2Foundry.Tests/Integration/StatefulChainE2E.cs` (new) — full round-trip through fake upstream simulating `previous_response_id` semantics.

## Algorithm

### Inbound

```
ChainCoordinator.HandleAsync(AnthropicRequest req):
  if !PrefixCache.Enabled or BackendKind != Responses:
    return Translate(req, previousResponseId: null, inputOverride: null)

  (prefix, delta) = SplitPrefix(req)            // delta = last user msg + trailing tool_results
  prefixHash = PrefixHasher.Hash(prefix)
  hit = cache.TryGet(prefixHash) → response_id?

  if hit and ValidateToolDance(req, prefix):
    translated = Translate(req, previousResponseId: response_id, inputOverride: delta)
    chainStatus = "hit"
  else:
    translated = Translate(req, previousResponseId: null, inputOverride: null)  // full
    chainStatus = "miss"

  result = await Upstream.Invoke(translated)

  if result.Failed with "previous_response_id not found":
    cache.Remove(prefixHash)
    chainStatus = "fallback"
    translated = Translate(req, previousResponseId: null, inputOverride: null)
    result = await Upstream.Invoke(translated)

  if result.Succeeded and BackendKind == Responses:
    newHash = PrefixHasher.Hash(req with assistant_response appended)
    cache.Insert(newHash, result.ResponseId)

  emit Monitor record { chainStatus, chainPrefixHash, previousResponseId }
  return result
```

### `SplitPrefix`

```
prefix = req.messages[0..n-1]    where n-1 is the last message
last = req.messages[n-1]

if last.role == "user":
  delta_messages = [last]
elif last.role == "assistant":
  # Shouldn't happen in Claude Code flow; treat as full replay
  return (req, null)

# tool_result blocks may be in last.user message content; they go in delta as separate function_call_output items
```

### `ValidateToolDance`

For each `tool_result` block in `delta`:
- `tool_use_id` must reference a `tool_use` block emitted in the cached chain (i.e., one of the assistant messages between `prefix` start and now). Adapter tracks per-`response_id` the set of `function_call.call_id`s emitted.
- If any `tool_use_id` not found → return false (fallback to full).

To support this, `ChainCoordinator` also maintains a sidecar `Dictionary<response_id, ISet<call_id>>` populated when storing a chain entry.

### `PrefixHasher.Hash`

Canonical JSON of:
```json
{
  "system": <normalized>,
  "tools": <normalized>,
  "messages": <normalized>
}
```

Where normalization:
- Object keys sorted lexicographically.
- Whitespace stripped.
- Text content blocks unwrap single-element arrays: `[{type:"text", text:"x"}]` → `"x"`.
- `tool_use.input` → re-serialized with sorted keys.
- Drop `metadata`, drop `stream`, drop `max_tokens`, drop `temperature`, drop sampling params (they don't affect server-side conversation context).
- Algorithm: BLAKE3-256 (use existing dependency if any; else SHA-256 — collision risk negligible for adapter scale).

Output: `"<algo>:<hex>"` e.g. `"blake3:abc123…"`.

### Cache structure

```
class PrefixCache {
  int capacity;
  TimeSpan ttl;
  LinkedList<Entry> lru;
  Dictionary<string hash, LinkedListNode<Entry>> index;
  Dictionary<string responseId, ISet<string> callIds> toolHistory;

  record Entry(string hash, string responseId, DateTime insertedAt);

  TryGet(hash) → (responseId, callIds)? // touches LRU, checks TTL
  Insert(hash, responseId, callIds)     // evicts LRU tail if capacity exceeded
  Remove(hash)
  Clear()                                // called on BackendKind change
}
```

Thread-safe via single `lock` (cache touches are infrequent vs request rate).

### `store:true` activation

When `ResponsesStorePolicy == "WhenChaining"`:
- Set `store: true` whenever the request **could become a chain link** — i.e., always for Responses path (every response is a potential chain root). The chain hash insertion happens post-response; we don't know in advance which will be chained, so always store.

When `Always`: same effect.
When `Never`: `store: false`. Prefix cache becomes inert (any `previous_response_id` we attempt will fail because nothing was stored). Detect this combo in validator and warn.

## Monitor schema

`request.translated` JSONL line gains:
```json
{
  "chainStatus": "hit" | "miss" | "fallback" | "disabled",
  "chainPrefixHash": "blake3:abc…" | null,
  "previousResponseId": "resp_…" | null,
  "deltaBytes": 234,           // size of input[] when chain hit
  "fullBytes": 18472           // size if we had sent full (only computed on hit; null otherwise)
}
```

`deltaBytes` and `fullBytes` together let the user see realized savings in the UI.

`/api/admin/monitor/{id}` adds:
```json
"chain": {
  "status": "hit",
  "prefixHash": "blake3:…",
  "previousResponseId": "resp_…",
  "savedBytes": 18238
}
```

UI MonitorRow renders a small chain badge: `🔗 18 KB saved` for hits, `−` for miss/disabled, `⚠ stale` for fallback.

## Acceptance criteria

1. With `BackendKind: ChatCompletions`, prefix cache is bypassed entirely. No change to behavior.
2. With `BackendKind: Responses` + `PrefixCache.Enabled: false`, every request full-replays. Monitor records `chainStatus: "disabled"`.
3. With `BackendKind: Responses` + cache enabled:
   - First request → miss, full replay. Cache stores hash → response_id.
   - Second request that extends the first by one user turn → hit. Upstream receives only `previous_response_id` + delta. `deltaBytes < fullBytes`.
   - Third request after editing the first message → miss (different prefix hash).
   - Restart adapter → first request after restart → miss (cache cleared).
4. Stale `response_id` fallback: simulate upstream returning 404 on `previous_response_id`. Coordinator retries with full replay, marks `fallback`, removes stale cache entry.
5. Tool dance validation: synthetic test sends a tool_result referencing a tool_use that the cached chain never emitted → full replay (not silent corruption).
6. `PrefixCacheTests`: LRU evicts oldest after capacity exceeded; TTL expiry removes entry after `TtlMinutes`; `Clear` empties cache.
7. `PrefixHasherTests`: identical-meaning Anthropic requests with whitespace / key-order differences hash equal. Semantically different requests hash unequal. Tool_use input key order doesn't affect hash.
8. `StatefulChainE2E`: 5-turn conversation, first turn full, turns 2–5 chained. Final assembled assistant text matches expectation. Monitor JSONL shows correct chain statuses.

## Out of scope

- Cross-process cache (lost on restart by design).
- Persistent disk-backed cache.
- Cache for ChatCompletions path (no `previous_response_id` analog).
- Image content hashing (skip image blocks in hash — assume images aren't re-sent on every turn; if they change, miss is acceptable).

## Risks

- **Race between concurrent chained requests.** If two requests arrive with the same prefix hash before the first response stores the new id, both go as full replays — wasteful but correct. Acceptable.
- **Hash collision.** BLAKE3-256 / SHA-256 collisions negligible.
- **Server-side response not yet flushed when next request arrives.** Foundry indexes by `response_id` synchronously on response completion; not observed in practice. If it happens, fallback path catches it.

## Engineer prompt

```
Implement /mnt/c/@Projects/Claude2Foundry/docs/specs/responses-api/40-phase-d-prefix-cache.md exactly. Requires Phase B + C merged. All 8 acceptance criteria pass. Include a real 5-turn smoke test against live Foundry with BackendKind=Responses, attach the Monitor JSONL excerpt showing chainStatus transitions.
```
