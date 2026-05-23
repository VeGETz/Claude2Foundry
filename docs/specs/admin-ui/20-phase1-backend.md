# 20 — Phase 1: Backend (parallel with phase 2)

## Mode

Parallel with phase 2 (frontend). One agent per worktree. **Do not edit files in phase 2's "Owned files" list.**

## Owned files (write)

- `src/Claude2Foundry/Admin/AdminApi.cs` — fully implemented handlers for every route declared in phase 0.
- `src/Claude2Foundry/Admin/CorsAndCsrfGuard.cs` — full middleware implementation.
- `src/Claude2Foundry/Admin/ConfigWriter.cs` (new) — writes `appsettings.local.json` atomically (write-temp + rename).
- `src/Claude2Foundry/Admin/ConfigSchemaProvider.cs` (new) — emits JSON Schema for `ProxyConfig`.
- `src/Claude2Foundry/Admin/RestartCoordinator.cs` (new) — drain + `Environment.Exit(75)` mechanism.
- `src/Claude2Foundry/Admin/TestRequestRunner.cs` (new) — single-shot pipeline runner for the Test page.
- `src/Claude2Foundry/Monitor/RequestRecord.cs` — full DTO definitions.
- `src/Claude2Foundry/Monitor/RequestCapturePipeline.cs` — ring buffer + per-phase ingestion + SSE fan-out.
- `src/Claude2Foundry/Monitor/JsonlWriter.cs` — rolling file writer (daily + size rotation, retention sweep).
- `src/Claude2Foundry/Monitor/CaptureModeController.cs` — hybrid/full toggle, session vs persistent scope.
- `src/Claude2Foundry/Monitor/FullBodyCache.cs` — short-TTL in-memory cache for full bodies in hybrid mode.
- `src/Claude2Foundry/Config/ProxyConfig.cs` — **extend** with new `Monitor` sub-section (`CaptureMode`, `LogMaxBytes`, `LogRetentionDays`). Existing fields untouched.
- `src/Claude2Foundry/Config/ConfigValidation.cs` — **extend** with validation for new `Monitor` section + tightened validation for `BackendUrl` (must end with `/openai/v1/` or `/openai/v1`).
- `src/Claude2Foundry/Backend/FoundryHealthProbe.cs` (new) — caller for `/models` probe, used by both existing `/health` and new `/api/admin/health`.
- `src/Claude2Foundry/Translation/RequestTranslator.cs`, `ResponseTranslator.cs`, `StreamTranslator.cs` — **extend** to accept an injected `IRequestCaptureSink` and emit phase events. Translation logic itself unchanged.
- `src/Claude2Foundry/Backend/FoundryClient.cs`, `FoundryLoggingHandler.cs` — **extend** to emit `foundry.request.sent` / `foundry.complete` events to the sink. No behavior change.
- `src/Claude2Foundry/Program.cs` — **extends only**, never replaces phase 0's scaffolding. Wire up DI for all new services, register `IOptionsMonitor` watch on `appsettings.local.json` from the data dir, register `RequestCapturePipeline` as a singleton. Do **not** modify the route declarations phase 0 set up; only add `services.AddXxx(...)` calls and middleware registrations. Phase 3 will further extend the same file to wire the embedded UI provider.
- `src/Claude2Foundry/appsettings.json` — add example `Monitor` section with defaults.
- `tests/Claude2Foundry.Tests/Admin/*` and `tests/Claude2Foundry.Tests/Monitor/*` — xUnit coverage.

## Consumed contracts (read-only)

- `docs/specs/admin-ui/10-phase0-contracts.md` — frozen HTTP and SSE shapes.
- `docs/specs/admin-ui/contracts/http-routes.md`, `sse-events.md`, `json-schema.md`.
- `src/Claude2Foundry/Config/DataDirResolver.cs` (phase 0 output).
- `src/Claude2Foundry/Admin/AdminApi.cs` route declarations (phase 0 stub) — keep route signatures stable.

## Must NOT touch (phase 2 owns these)

- `ui/**` (entire directory).
- `src/Claude2Foundry/wwwroot/_ui/**` (filled by phase 3 from phase 2's build output).
- `docs/specs/admin-ui/contracts/*.md` (frozen in phase 0; if a backend implementation discovers a contract gap, escalate to Tech Lead — do not unilaterally edit).

## Tasks

### 1. Config layering + reload

1.1 In `Program.cs`, after the existing `AddJsonFile("appsettings.json")`, add `AddJsonFile(Path.Combine(dataDir, "appsettings.local.json"), optional: true, reloadOnChange: true)`.

1.2 Drain semantics: handlers capture a `ProxyConfigSnapshot` (a POCO clone of the current effective config) at request entry and thread it down the call stack to `RequestTranslator` / `ResponseTranslator` / `StreamTranslator` instead of those classes resolving config from DI. The mechanism for "current effective config at this moment" is an implementation choice — `IOptionsMonitor<ProxyConfig>.CurrentValue`, scoped `IOptionsSnapshot<ProxyConfig>` in a request-scoped service, or a custom `IConfigCurrent` singleton over `IConfigurationRoot` are all acceptable. The **contract** is: in-flight requests never observe a mid-flight config change; new requests pick up the new config on their next entry.

1.3 On `appsettings.local.json` parse failure, log `Error` once and continue with base config. Surface via `/api/admin/health` (`config.localFileError: "..."`).

### 2. Admin API handlers

For each route in the phase 0 table, implement the handler:

2.1 `GET /api/admin/config` — return `{ "proxy": currentEffectiveConfig }` with api-key **value** masked (the env var **name** is exposed; only the resolved value is masked).

2.2 `GET /api/admin/config/schema` — return JSON Schema. Implementation may use `NJsonSchema` or hand-built `JsonObject`. The output must match the frozen shape in `contracts/json-schema.md`.

2.3 `POST /api/admin/config` —
- Validate the incoming `proxy` against `ConfigValidation` + schema.
- On failure, return `400` with `issues` list.
- On success: diff against current `BackendUrl` + `ApiKeyEnv`; build `restartRequired` + `fields` list. Atomically write `<datadir>/appsettings.local.json` (write to `.tmp`, `fsync`, rename). Trigger `IOptionsMonitor` reload by writing the file (which is already watched). Return `{ "ok": true, "restartRequired": bool, "fields": [string] }`.

2.4 `POST /api/admin/config/test-connection` — build a temporary `HttpClient` from the **proposed** config (not the live one), call `GET <BackendUrl>/models`, time it, return latency + model count. On failure return `502` with structured error.

2.5 `POST /api/admin/restart` —
- If `C2F_WRAPPER` env var unset → `409 wrapper_absent`.
- Else: log "restart requested". Begin drain: stop accepting new requests via a `CancellationTokenSource` exposed to handlers. Wait up to 60 s for in-flight count to reach 0; then `Environment.Exit(75)`. Respond `200` with `{ "draining": true, "graceMs": 60000 }` before exit (response must flush).

2.6 `GET /api/admin/health` — assemble the response per the phase 0 shape. The Foundry probe is a 5 s-cached call to `FoundryHealthProbe`; the existing `/health` endpoint reuses the same probe.

2.7 `GET /api/admin/events` — SSE stream. On connect: emit `replay.snapshot` from the ring buffer (filtered by `?since` if provided). Then subscribe to live capture events and forward. Backpressure per phase 0.

2.8 `GET /api/admin/events/full/{id}` — look up the full body in `FullBodyCache`. If missing → `{ "expired": true }` 200, not 404 (the `404` only fires if the id was never seen).

2.9 `POST /api/admin/capture-mode` — toggle `CaptureModeController`. Persistent scope writes the field to `appsettings.local.json` (same write path as 2.3).

2.10 `POST /api/admin/test-request` — call `TestRequestRunner`, which invokes the live `RequestTranslator` + `FoundryClient` + `ResponseTranslator` pipeline against the current config and returns the trace.

### 3. Capture pipeline

3.1 `RequestCapturePipeline` is a singleton with:
- A bounded `Channel<CaptureEvent>` (capacity 1024) shared by all producers (handlers, translators, Foundry client).
- A ring buffer (`Deque<RequestRecord>` capped at 500, configurable later).
- A list of subscriber sinks (each SSE client is one subscriber with its own bounded outbound queue).
- A background `Task` that drains the channel, updates the ring, and fans out to subscribers (drop-oldest on subscriber overflow).

3.2 `JsonlWriter` is a singleton with:
- A serial async writer (`Channel<string>` → background `Task` writing to current file).
- Daily rotation at local midnight; size rotation at `Monitor.LogMaxBytes`.
- Retention sweep on startup + once per hour.
- File path: `<datadir>/logs/requests-YYYY-MM-DD.jsonl`. Rotation suffix `-N` when same-day size cap hits: `requests-2026-05-23-2.jsonl`.

3.3 `CaptureModeController` exposes:
- Current effective mode (session override > persistent config).
- `SetSession(mode)` and `SetPersistent(mode)` methods, the latter invoking `ConfigWriter`.

3.4 `FullBodyCache` is a bounded LRU (capacity 100) keyed by correlation id, storing full bodies. Insertion happens at `response.sent` time. On hybrid mode, eviction is FIFO; on full mode the cache is bypassed (everything goes straight to ring + JSONL).

3.5 Translators emit events via `IRequestCaptureSink` injected through DI. Translators **must** finalize the record on success and on exception. A `try { ... } finally { sink.Finalize(id, error?) }` wrapper sits in the inbound request handler.

3.6 Header redaction lives in one place: `RequestRecordBuilder.RedactHeaders(headers)`. Redacts `api-key`, `Authorization`, `x-api-key` (case-insensitive). Everything else verbatim.

### 4. Validation extensions

4.1 `ConfigValidation` gains:
- `BackendUrl` must match `^https?://.*/openai/v1/?$`. Trailing slash optional.
- `Monitor.LogMaxBytes` ≥ `1 048 576` (1 MB), default `104 857 600` (100 MB).
- `Monitor.LogRetentionDays` ≥ `0`, default `7`. `0` disables persistence (JSONL writes go to a sink that no-ops).
- `Monitor.CaptureMode` ∈ `{"hybrid","full"}`, default `"hybrid"`.

4.2 Validation errors are returned as the `issues` array shape in phase 0.

### 5. Background hygiene

5.1 `JsonlWriter`'s retention sweep runs at startup and on a 1-hour `PeriodicTimer`.

5.2 `FoundryHealthProbe` is called on demand by `/api/admin/health` and `/health`; results cached 5 seconds.

5.3 Graceful shutdown: on `ApplicationStopping`, drain `RequestCapturePipeline`'s channel + `JsonlWriter`'s channel before exiting.

## Acceptance criteria

1. All routes from the phase 0 table return the documented shapes when called with valid input (manual `curl` smoke OK + automated tests for happy/sad paths).
2. Saving config via `POST /api/admin/config` writes `<datadir>/appsettings.local.json`; subsequent `GET` returns the new value; `IOptionsMonitor` fires within 2 s; next `/v1/messages` request uses the new config.
3. Changing `BackendUrl` and saving: response includes `restartRequired: true, fields: ["Proxy.BackendUrl"]`.
4. Restart endpoint: with `C2F_WRAPPER=1`, calling it returns 200, drops connections after grace, exits process with code 75. Without `C2F_WRAPPER`, returns 409.
5. A streaming `/v1/messages` request triggers SSE events on `/api/admin/events` in order: `request.received` → `request.translated` → `foundry.request.sent` → multiple `foundry.chunk` → `foundry.complete` → `response.sent`. Sequence numbers monotonic per request.
6. JSONL file is created at expected path; one line per request; rotates at midnight or at size cap; old files deleted per retention.
7. CSRF guard: `POST /api/admin/config` without `X-C2F-Admin: 1` → `400`. With → handler invoked.
8. Hot-reload drain: while a 30-second stream is in flight, saving `ModelAliases` does not interrupt the stream; the stream completes on the old alias map; a subsequent request uses the new map.
9. `appsettings.local.json` corrupt → server returns base config from `GET /api/admin/config`, `/api/admin/health` exposes `localFileError`.

## Test matrix

| Component | Test | Pass criterion |
|---|---|---|
| `ConfigWriter` | atomic write | crash mid-write leaves either old file or new file, never partial |
| `ConfigWriter` | concurrent saves | second save waits for first; both succeed serially |
| `ConfigSchemaProvider` | output matches frozen schema | round-trip parse equals expected JSON Schema fixture |
| `RestartCoordinator` | drain + exit | with mocked clock, 60 s deadline triggers exit even if requests remain |
| `RestartCoordinator` | wrapper detection | env var `C2F_WRAPPER=1` → restart proceeds; unset → 409 |
| `RequestCapturePipeline` | event ordering | events for a given id arrive in defined order |
| `RequestCapturePipeline` | backpressure | slow subscriber sees `foundry.chunk` gaps but never drops `*.complete`/`error` |
| `JsonlWriter` | daily rotation | clock advance crosses midnight → new file |
| `JsonlWriter` | size rotation | bytes > cap → `-2.jsonl` opened |
| `JsonlWriter` | retention | files older than N days deleted on startup |
| `FullBodyCache` | hybrid eviction | 101st insert evicts oldest |
| `FullBodyCache` | full mode | bypassed; nothing cached |
| `CorsAndCsrfGuard` | preflight reject | OPTIONS with foreign origin → 403 |
| `CorsAndCsrfGuard` | mutation guard | POST without header → 400 |
| `CorsAndCsrfGuard` | GET allowed | GET without header → handler invoked |
| Drain semantics | hot-reload mid-stream | stream completes on old config |
| Validation | bad `BackendUrl` | returns 400 with `issues[*].path = "Proxy.BackendUrl"` |
| Validation | bad `LogMaxBytes` | 400 with path |

## Risks / open Qs

- **`IOptionsMonitor` snapshot semantics.** Passing `IOptionsSnapshot<ProxyConfig>` down through translators changes existing translator signatures. If this triggers wide test churn, alternate path: keep current signatures, capture a `ProxyConfigSnapshot` POCO at request entry and pass that instead.
- **`Environment.Exit(75)` mid-response.** The restart response writes `{ "draining": true }` then exits. The client may see a partial response. The Console handles this by treating a dropped connection on `/api/admin/restart` as success after a `draining: true` response is received.
- **JSON Schema for discriminated unions.** `Tokenizers.<name>.Source ∈ {TiktokenCl100k, TiktokenO200k, HuggingFace}` with `Path` required only for `HuggingFace`. If `NJsonSchema` doesn't render this cleanly, hand-augment the output post-generation.
