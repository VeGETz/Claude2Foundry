# SSE events — frozen contract

Event schema for `GET /api/admin/events`. Frozen in phase 0.

## Connection

- Endpoint: `GET /api/admin/events`.
- Optional query param: `?since=<iso8601>` — replay records newer than this timestamp on connect (the server's own clock).
- Response: `Content-Type: text/event-stream`. Connection held open until client disconnects or server shuts down.
- `Last-Event-ID` header (sent by the browser on automatic reconnect): the server resumes from the next sequence number after that id when possible (ring contents permitting). If the requested id is no longer in the ring, the server emits a fresh `replay.snapshot` and continues.

## Common envelope

Every event is encoded as:

```
id: <monotonic-sequence-number>
event: <event-type>
data: <single-line JSON>

```

(blank line terminator). The `id:` field is a per-connection-monotonic integer used for `Last-Event-ID` resume.

The JSON payload always carries the correlation id of the request being described (`"id": "<x-c2f-request-id>"`), except for `replay.snapshot` which is batch.

## Event types

### `replay.snapshot`

Emitted as the **first** frame on every connection. Carries the current ring buffer contents as compact summaries (one per request, not per phase).

```json
{
  "records": [
    {
      "id": "abc123",
      "ts": "2026-05-23T10:00:00.123Z",
      "originalModel": "claude-opus-4-7",
      "resolvedModel": "DeepSeek-V4-Pro",
      "status": "ok",
      "elapsedMs": 1234,
      "usage": { "input": 542, "output": 187 },
      "error": null,
      "phase": "complete"
    },
    { "...": "..." }
  ]
}
```

Phases: `"received"` | `"translated"` | `"foundry-sent"` | `"streaming"` | `"complete"` | `"error"`.

---

### `request.received`

Emitted when an inbound request finishes parsing and the correlation id has been assigned.

```json
{
  "id": "abc123",
  "ts": "2026-05-23T10:00:00.123Z",
  "originalModel": "claude-opus-4-7",
  "stream": true,
  "headers": { "user-agent": "claude-cli/x.y", "x-c2f-request-id": "abc123" },
  "bodyPreview": { /* first 8 KB of the Anthropic request, or full body in full-capture mode */ }
}
```

`bodyPreview` is `null` when hybrid mode is in effect and the body exceeds the preview budget.

---

### `request.translated`

Emitted after `RequestTranslator` builds the OpenAI body.

```json
{
  "id": "abc123",
  "resolvedModel": "DeepSeek-V4-Pro",
  "openaiBody": { /* full OpenAI request body, or null if hybrid + over budget */ }
}
```

---

### `foundry.request.sent`

Emitted right before the outbound HTTP `POST` to Foundry.

```json
{ "id": "abc123", "ts": "2026-05-23T10:00:00.456Z" }
```

---

### `foundry.chunk`

Emitted per upstream SSE chunk during streaming, **or** per logical delta during non-streaming response materialization (in non-streaming mode this event fires zero or one time).

```json
{
  "id": "abc123",
  "seq": 42,
  "deltaText": "Hello, ",
  "deltaToolCall": null,
  "reasoningDelta": null
}
```

Exactly one of `deltaText`, `deltaToolCall`, `reasoningDelta` is non-null per chunk event.

`deltaToolCall` shape (when present):
```json
{ "index": 0, "id": "call_xyz", "name": "search", "argumentsDelta": "{\"q" }
```

`seq` is monotonic per request. Backpressure may drop `foundry.chunk` events; the client detects gaps via `seq` and renders `[N chunks dropped]`.

---

### `foundry.complete`

Emitted when the upstream call is complete (stream end or non-stream response received).

```json
{
  "id": "abc123",
  "usage": { "input": 542, "output": 187 },
  "finishReason": "end_turn"
}
```

`finishReason` follows OpenAI vocabulary (`stop`, `length`, `tool_calls`, `content_filter`); translation to Anthropic vocabulary happens later in `response.sent`.

This event is **never dropped** by backpressure.

---

### `response.sent`

Emitted when the final byte has been sent to the downstream client (Claude Code).

```json
{
  "id": "abc123",
  "elapsedMs": 1234,
  "anthropicAssembled": { /* the assembled Anthropic response object, or null in hybrid + over budget */ }
}
```

This event is **never dropped** by backpressure.

---

### `error`

Emitted whenever any phase fails. Replaces any further events for that id (subsequent `*.complete` / `response.sent` are not emitted for failed requests).

```json
{
  "id": "abc123",
  "phase": "foundry-sent",
  "origin": "Foundry",
  "message": "[Foundry] 429 Too Many Requests"
}
```

`origin` ∈ `{ "Adapter", "Foundry" }`. `message` is the same string returned to Claude Code in the Anthropic-shaped error response.

This event is **never dropped** by backpressure.

---

## Backpressure policy

Each connected client has a bounded outbound queue (default 256 events). On overflow:

- Drop the **oldest** non-terminal events first (i.e. `foundry.chunk`).
- Never drop `*.complete`, `response.sent`, or `error` events — they carry the terminal state of a request and the UI needs them to mark rows finalized.
- Never drop the most recent `request.received` for any in-flight id, so the row always appears.

Clients detect drops via `seq` gaps in `foundry.chunk` events and display a per-request `[N chunks dropped]` indicator.

## Heartbeat

Server emits a comment line (`: ping\n\n`) every 15 seconds to keep idle connections alive through middle-boxes. Browsers ignore comment frames; the SSE channel stays open.
