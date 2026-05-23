# Admin HTTP routes — frozen contract

Source of truth for all `/api/admin/*` HTTP routes consumed by the Adapter Console. Frozen in phase 0; both phase 1 (backend) and phase 2 (frontend) build against this.

## Conventions

- Base URL: `http://127.0.0.1:8787`.
- All requests and responses are `application/json` unless noted otherwise.
- All routes are bound to `127.0.0.1` only.
- Mutating methods (`POST`, `PUT`, `DELETE`, `PATCH`) **require** request header `X-C2F-Admin: 1`. Missing or invalid → `400 missing X-C2F-Admin`.
- CORS: `Access-Control-Allow-Origin: null` on every response. Preflight (`OPTIONS`) rejected unless `Origin` is `http://127.0.0.1:8787` or (in dev mode only) `http://127.0.0.1:5173`.

## Routes

### `GET /api/admin/config`

Get the current effective configuration (base file overlaid with local file).

**Response 200:**
```json
{ "proxy": { /* full ProxyConfig with api-key value masked */ } }
```

The `Proxy.ApiKeyEnv` field is exposed as-is (it's a name, not a secret). The resolved API key value is **never** in this response.

---

### `GET /api/admin/config/schema`

Get the JSON Schema (Draft 2020-12) describing `ProxyConfig`. UI uses this to render forms.

**Response 200:** a JSON Schema document. See [`json-schema.md`](./json-schema.md) for the frozen shape.

---

### `POST /api/admin/config`

Save the full `Proxy` section to `<datadir>/appsettings.local.json`.

**Request:**
```json
{ "proxy": { /* full ProxyConfig */ } }
```

**Response 200:**
```json
{ "ok": true, "restartRequired": false, "fields": [] }
```

When the change touches a **bootstrap field** (`Proxy.BackendUrl` or `Proxy.ApiKeyEnv`):
```json
{ "ok": true, "restartRequired": true, "fields": ["Proxy.BackendUrl"] }
```

**Response 400 (validation failure):**
```json
{
  "error": {
    "type": "invalid_config",
    "issues": [
      { "path": "Proxy.BackendUrl", "message": "must end with /openai/v1/" },
      { "path": "Proxy.ModelAliases.claude-opus-4-7", "message": "value cannot be empty" }
    ]
  }
}
```

---

### `POST /api/admin/config/test-connection`

One-shot probe against Foundry using a **proposed** config (does not persist). Used by the "Test connection" button next to `BackendUrl`.

**Request:**
```json
{ "proxy": { /* full ProxyConfig */ } }
```

**Response 200:**
```json
{ "ok": true, "latencyMs": 142, "modelCount": 47 }
```

**Response 502:**
```json
{
  "error": {
    "type": "foundry_unreachable",
    "message": "[Foundry] 401 Unauthorized",
    "latencyMs": 320
  }
}
```

---

### `POST /api/admin/restart`

Drain in-flight requests (60 s grace) and exit with code 75.

**Response 200:**
```json
{ "draining": true, "graceMs": 60000 }
```

Process exits shortly after. Client should treat connection drop after this response as expected.

**Response 409 (wrapper absent):**
```json
{
  "error": {
    "type": "wrapper_absent",
    "message": "C2F_WRAPPER env var not set; restart not available in-process"
  }
}
```

---

### `GET /api/admin/health`

Rich health snapshot for the Health page.

**Response 200:**
```json
{
  "uptimeSec": 12345,
  "version": "0.2.0",
  "dotnetVersion": "10.0.0",
  "dataDir": "/home/user/.local/share/claude2foundry",
  "foundry": {
    "reachable": true,
    "lastProbeMs": 142,
    "lastProbeAt": "2026-05-23T10:00:00Z",
    "lastFailureAt": null,
    "lastFailureMessage": null
  },
  "config": {
    "proxy": { /* effective config, api-key value masked */ },
    "localFileError": null
  },
  "inFlight": 2,
  "ringBuffer": { "occupancy": 87, "capacity": 500 },
  "capture": { "mode": "hybrid", "scope": "session" },
  "logFile": {
    "path": "/home/user/.local/share/claude2foundry/logs/requests-2026-05-23.jsonl",
    "sizeBytes": 1234567
  },
  "wrapperPresent": true
}
```

`localFileError` is `null` unless `appsettings.local.json` failed to parse, in which case it carries the parse-error string.

---

### `GET /api/admin/events`

Server-Sent Events stream. See [`sse-events.md`](./sse-events.md) for the event schema.

**Query params (optional):**
- `since=<iso8601>` — replay records newer than this timestamp on connect.

**Response:** `text/event-stream`.

---

### `GET /api/admin/events/full/{id}`

Fetch the full captured bodies for a specific correlation id (used when expanding a Monitor row that was captured in hybrid mode).

**Path params:**
- `id` — correlation id (matches `x-c2f-request-id` value).

**Response 200 (still cached):**
```json
{
  "id": "abc123",
  "phase": "complete",
  "anthropicBody": { /* original request body */ },
  "openaiBody": { /* translated request body */ },
  "responseBody": { /* assembled response */ },
  "headers": { /* request headers, secrets masked */ }
}
```

**Response 200 (expired):**
```json
{ "expired": true }
```

**Response 404:** unknown id.

---

### `POST /api/admin/capture-mode`

Toggle the request-capture mode.

**Request:**
```json
{ "mode": "hybrid", "scope": "session" }
```

- `mode` ∈ `{ "hybrid", "full" }`.
- `scope` ∈ `{ "session", "persistent" }`. `"persistent"` writes `Monitor.CaptureMode` to `appsettings.local.json` (via the same path as `POST /api/admin/config`); `"session"` lives only in the current process and resets on restart.

**Response 200:**
```json
{ "ok": true, "mode": "hybrid", "scope": "session" }
```

---

### `POST /api/admin/test-request`

Run a one-shot request through the full pipeline against the current live config, for the Test page.

**Request:**
```json
{
  "model": "claude-opus-4-7",
  "messages": [{ "role": "user", "content": "hello" }],
  "thinking": null,
  "stream": false
}
```

**Response 200:**
```json
{
  "anthropicResponse": { /* the response that would be returned to Claude Code */ },
  "openaiRequest": { /* the body the adapter sent to Foundry */ },
  "openaiResponse": { /* the body Foundry returned */ },
  "elapsedMs": 1234
}
```

**Response 502:** same envelope as `/api/admin/config/test-connection` 502.

---

## Error envelope

All admin error responses use this shape:

```json
{
  "error": {
    "type": "<machine-readable type>",
    "message": "<human-readable, optionally prefixed with [Foundry] or [Adapter]>",
    "issues": [ /* optional, for invalid_config */ ]
  }
}
```

The admin error envelope is **distinct** from the Anthropic-shaped error envelope used by `/v1/*`. Do not reuse one for the other.
