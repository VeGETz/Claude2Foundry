# 50 — Monitor rewrite: JSONL-only capture

**Status:** Draft, 2026-05-24. Supersedes the in-memory ring + FullBodyCache + hybrid/full toggle described in earlier phase 1 docs.

## Why this exists

The current Monitor pipeline has three storage tiers (in-mem ring, FullBodyCache LRU, JSONL writer) and two capture modes (hybrid / full), all coordinated by a background thread that serializes events. Across phase 1–3 it produced:

- a `JsonElement` lifecycle bug (pooled buffer recycled before background serialization), patched four times before the JsonNode refactor
- silent body-capture loss when serialization ordered before accumulation
- camelCase / PascalCase contract drift between SSE snapshot and UI
- a runtime regression that broke `/v1/messages` itself

Cost-to-value is upside-down. Adapter core is the product; Monitor is a debug aid. **Core must work first.** Monitor gets the minimum design that makes shape of the bug visible.

## Design

**Single store. JSONL on disk. Purged on startup. Monitor reads from disk.**

No ring buffer. No FullBodyCache. No hybrid mode. No capture-mode toggle. No background serialization thread that can outlive the request.

### Storage

- Path: `<datadir>/logs/requests-<bootId>.jsonl`
- `<bootId>` = process start timestamp in `yyyyMMdd-HHmmss-<pid>` format
- On startup: enumerate `<datadir>/logs/requests-*.jsonl`, delete all (best-effort; warn on failure, continue boot). Create fresh file.
- Append-only writes. One JSON object per line. UTF-8, `\n` terminator.
- Single `FileStream` opened with `FileShare.Read` so the Monitor reader can open concurrently.
- All writes serialized via a single `Channel<JsonLine>` consumer (one writer task). No locks at write sites — they just enqueue.
- On shutdown: drain channel, flush, close. Hook into `IHostApplicationLifetime.ApplicationStopping`.

### Event shape

One line per logical event. Matches the existing console-logging cadence:

```jsonc
{
  "id": "abc123",                            // x-c2f-request-id
  "ts": "2026-05-24T10:00:00.123Z",
  "kind": "request.received",                // see kinds below
  "model": "claude-opus-4-7",                // present on request.received only
  "data": { /* event-specific payload */ }
}
```

**Kinds (mirrors existing `Console.WriteLine` points in `FoundryLoggingHandler` + the four emission sites in `Program.cs`):**

| kind | when written | `data` contents |
|---|---|---|
| `request.received` | after Anthropic body bound, before translation | `anthropicBody`, `headers` (auth masked) |
| `request.translated` | after `RequestTranslator` runs | `openaiBody` |
| `response.received` | after Foundry call returns (non-stream: body; stream: assembled OpenAI chunks joined post-stream) | `openaiResponse`, `status`, `latencyMs` |
| `response.sent` | after `ResponseTranslator` builds the Anthropic response | `anthropicResponse`, `elapsedMs` |
| `request.error` | on any exception escaping the pipeline | `phase`, `message`, `stackTrace` |

Streaming requests follow the existing console pattern: `response.received` is written **once**, after the upstream SSE stream finishes and the OpenAI chunks have been assembled into a single body — same shape as a non-stream response. Per-frame writes are explicitly out of scope. Monitor sees the request appear in the list on `request.received`, then sees bodies fill in as the request progresses; in-flight requests show as `running`.

### Body cap

- Config: `Monitor.MaxBodyBytes` in `appsettings.json`. Default `10485760` (10 MB).
- Applied per body. If `JsonNode` serialized length exceeds cap: write a truncation marker instead:
  ```json
  { "__truncated": true, "originalBytes": 23456789 }
  ```
- Cap is a hard limit measured on serialized output, not input.

### Capture using JsonNode

Reuse the post-`c1fcae4` `JsonNode`-only DTO shape. No `JsonElement`. No `JsonSnapshot.Take()` defense-in-depth wrapper — kill it.

Each event is serialized **synchronously at the call site** before enqueue. Channel carries `string` lines, not object graphs. No background-thread lifecycle exposure to pooled buffers.

If serialization throws: log to `ILogger` at Warning, write a `request.error` line for that id with `phase: "serialize"`, continue. Never let capture failure surface to `/v1/messages`.

### Reader API

Three routes only:

#### `GET /api/admin/monitor/list?limit=200&before=<id>`

Tail the JSONL file. Returns the most recent N requests, optionally paginated by id.

Response 200:
```json
{
  "items": [
    {
      "id": "abc123",
      "ts": "2026-05-24T10:00:00.123Z",
      "model": "claude-opus-4-7",
      "originalModel": "claude-opus-4-7",
      "mappedModel": "Kimi-K2.6",
      "status": "ok",                  // ok | error | running
      "latencyMs": 1234,
      "promptTokens": 123,
      "completionTokens": 456
    }
  ]
}
```

Implementation: scan JSONL backward (read whole file into memory if <50 MB else mmap + reverse-scan), group by `id`, project newest state. List rendering does **not** include body content.

#### `GET /api/admin/monitor/{id}`

Return all events for one id, joined.

Response 200:
```json
{
  "id": "abc123",
  "ts": "2026-05-24T10:00:00.123Z",
  "model": "claude-opus-4-7",
  "status": "ok",
  "latencyMs": 1234,
  "anthropicBody": { /* or null */ },
  "openaiBody": { /* or null */ },
  "openaiResponse": { /* or null */ },
  "anthropicResponse": { /* or null */ },
  "headers": { /* or null */ },
  "error": { /* or null */ }
}
```

Implementation: scan JSONL forward (or use a tiny in-memory `Dictionary<id, long fileOffset>` for first occurrence — built on append, kept in memory only, ephemeral), read lines for matching id, merge `data` payloads by kind.

Response 404: id not found in current boot's JSONL.

#### `GET /api/admin/monitor/events` (SSE)

Live tail. On connect:
1. Send last `limit` items as `replay` events (no bodies, list-shape).
2. Switch to live: every new line appended to JSONL fires an `append` event with the parsed line.

Implementation: single in-process `Channel<string>` subscriber list, fed by the same writer that appends to JSONL. No file watcher needed — events broadcast in-process at write time.

### What gets removed

- `Monitor/RequestCapturePipeline.cs` — gone.
- `Monitor/RingBuffer.cs` / in-mem ring — gone.
- `Monitor/FullBodyCache.cs` — gone.
- `Monitor/JsonSnapshot.cs` — gone.
- `Monitor.CaptureMode` config + `POST /api/admin/capture-mode` — gone.
- `GET /api/admin/events/full/{id}` — replaced by `GET /api/admin/monitor/{id}`.
- `GET /api/admin/events` — replaced by `GET /api/admin/monitor/events`.
- Monitor UI controls for hybrid/full toggle, "Load full bodies" button — gone. Bodies are always there or never there.

### What stays

- Header redaction (Authorization, x-api-key).
- Existing `FoundryLoggingHandler` Console output — keep it. JSONL capture is additive, not replacing stdout logs.
- Correlation id generation (`x-c2f-request-id` middleware).
- Token counting fields surfaced in list view.

## Config

```jsonc
{
  "Monitor": {
    "MaxBodyBytes": 10485760,
    "Enabled": true
  }
}
```

`Enabled: false` skips all JSONL writes (no file created). Default `true`.

Drop `Monitor.CaptureMode`, `Monitor.RingSize`, `Monitor.FullBodyCacheSize`, `Monitor.JsonlPath` (path is now derived).

## Acceptance criteria

1. `/v1/messages` works end-to-end with `Monitor.Enabled=true` and `Monitor.Enabled=false`. Streaming + non-stream + tool-use + system prompt all pass.
2. On startup, all `requests-*.jsonl` files in `<datadir>/logs/` are deleted before a new one is opened.
3. After a non-stream request: JSONL contains 4 lines for that id (`request.received`, `request.translated`, `response.received`, `response.sent`). All bodies populated.
4. After a streaming request: same 4 lines, `response.received` contains the assembled OpenAI body.
5. After a request that errors mid-pipeline: JSONL contains the events that ran before the error, plus one `request.error` line.
6. Bodies exceeding `MaxBodyBytes` are replaced with `{ "__truncated": true, "originalBytes": N }`. Adapter never OOMs on huge requests.
7. Monitor list and detail render bodies directly from JSONL. No "Not captured" or "Load full bodies" affordances exist.
8. Killing the adapter mid-request loses at most the in-flight events; subsequent boot starts a fresh JSONL.
9. Concurrent requests (10 in parallel) all land their events in JSONL without interleaving within a single line.
10. Existing translation/token tests still pass. JsonElement is not reintroduced anywhere.

## Out of scope

- Cross-boot history. (Intentional. Use `git`-like external log shipping if needed.)
- Per-frame SSE capture. (Streaming response written once, assembled.)
- Capture-mode toggle. (Removed.)
- UI changes beyond the three new endpoints and the removal of stale controls. Frontend agent gets a follow-up spec once backend lands.

## Migration steps for engineer

1. Delete the four files listed under "What gets removed".
2. Remove their registrations from `Program.cs` (DI, route maps, options binding).
3. Add `Monitor/JsonlWriter.cs`: channel + writer task + `IHostedService` for lifecycle.
4. Add `Monitor/JsonlReader.cs`: `list`, `getById`, subscribe-for-tail.
5. Add the three `/api/admin/monitor/*` routes.
6. Replace the four emission call sites in `Program.cs` with synchronous `JsonlWriter.Enqueue(kind, id, dataNode)` calls. Build the `JsonNode` payload right there, no helper wrappers.
7. Add startup hook to purge old `requests-*.jsonl`.
8. Run full test suite. Smoke test via Claude Code → Foundry round-trip with tools + system.
9. Frontend spec to follow once these endpoints land.

## Open questions

None. Spec is intentionally narrow. Engineer should not add modes, toggles, or fallbacks beyond what's written.
