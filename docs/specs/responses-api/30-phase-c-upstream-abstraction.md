# 30 — Phase C: Upstream client routing + health/probe per backend kind

**Mode:** Parallel with Phase B. Depends on Phase A. Worktree-isolated.

Make `UpstreamClient` and `UpstreamHealthProbe` aware of `BackendKind`. Route to the right endpoint path. Probe with the right shape.

## Owned files (write)

- `src/Claude2Foundry/Backend/UpstreamClient.cs` — branch on `BackendKind` for path + serializer DTO + return type.
- `src/Claude2Foundry/Backend/UpstreamHealthProbe.cs` — when `Responses`, probe with a minimal `/responses` POST (or `/models` if cheap; see below). When `ChatCompletions`, current `/chat/completions` minimal POST.
- `src/Claude2Foundry/Backend/UpstreamLoggingHandler.cs` — no logic change; only confirm console messages still useful with both paths.
- `src/Claude2Foundry/Admin/TestRequestRunner.cs` — uses whichever client path matches `BackendKind`.
- `tests/Claude2Foundry.Tests/Integration/UpstreamRoutingTests.cs` (new) — `BackendKind` switch routes to correct path; `BackendAuthScheme` switch uses correct header; `BackendApiVersion` appended correctly.

## Tasks

### 1. `UpstreamClient` dual-path

Current shape (Phase A):
```
Task<ChatResponse> ChatAsync(ChatRequest, ct)
IAsyncEnumerable<string> ChatStreamAsync(ChatRequest, ct)
```

Expand to:
```
Task<UpstreamResponse> InvokeAsync(UpstreamRequest, ct)
IAsyncEnumerable<string> InvokeStreamAsync(UpstreamRequest, ct)
```

Where `UpstreamRequest` is a discriminated wrapper:
```
public abstract record UpstreamRequest;
public sealed record ChatCompletionsRequest(ChatRequest Body) : UpstreamRequest;
public sealed record ResponsesRequest(CreateResponseRequest Body) : UpstreamRequest;
```

Path resolution:
- `ChatCompletionsRequest` → `{BackendUrl}chat/completions{?api-version=…}`
- `ResponsesRequest` → `{BackendUrl}responses{?api-version=…}`

(`BackendUrl` already ends with `/`.)

The caller in `Program.cs` builds the right variant based on `BackendKind` after translation. Keep the two translator outputs separate; don't unify into a single DTO.

### 2. Health probe

Current probe POSTs a tiny request to `chat/completions`. For Responses, do the same against `responses`:

```json
{ "model": "<DefaultModel>", "input": "ping", "max_output_tokens": 1, "store": false }
```

Probe interpretation:
- 200 → reachable.
- 4xx → reachable (auth/model issue surfaced in message).
- 5xx / connect failure → unreachable.

Latency = wall-clock around the HTTP send. `modelCount` field in current Health endpoint is Foundry-specific — drop or set to `null` for non-Foundry endpoints.

### 3. Error mapping

Both paths share the OpenAI error envelope shape:
```json
{ "error": { "message": "…", "type": "…", "param": null, "code": "…" } }
```
Existing `MapUpstreamStatus` already handles this. Verify Responses errors fit the same shape (Foundry confirms it does). Add 1 test per path.

### 4. Timeout/retry behavior

No change in Phase C. Both paths use existing `TimeoutsConfig`. SSE idle-timeout middleware already operates on raw event stream — works for both.

## Acceptance criteria

1. `BackendKind: "ChatCompletions"` routes to `…/chat/completions` exactly as before. All existing tests pass.
2. `BackendKind: "Responses"` routes to `…/responses`. Stub upstream returning a canned response shape passes through.
3. `BackendAuthScheme: "Bearer"` flips header for both backend kinds (test both combinations).
4. `BackendApiVersion: "preview"` is appended correctly to either path (`…/responses?api-version=preview`).
5. Health probe uses the right path per `BackendKind`. Probe failure surfaces in `/api/admin/health` `upstream.reachable: false`.
6. `TestRequestRunner` (Test page) honors `BackendKind` and returns whichever-shape response in its payload.
7. New `UpstreamRoutingTests` cover the 4-cell matrix (Chat × ApiKey, Chat × Bearer, Responses × ApiKey, Responses × Bearer).

## Out of scope

- Translation logic (Phase B).
- Prefix cache (Phase D).
- Streaming SSE event-name translation (Phase B).

## Risks

- **Health probe cost.** `responses` with `store:false` is fine. Avoid `store:true` here — would create a stray cached response on Foundry every Health refresh.
- **`/models` listing.** Optional in current Health; if you want a quick "is this endpoint OpenAI-shaped" sniff, try `GET /models` first. But not all compatible endpoints implement it (vLLM does, llama.cpp partially). Treat 404 as "no model listing available," not "endpoint down."

## Engineer prompt

```
Implement /mnt/c/@Projects/Claude2Foundry/docs/specs/responses-api/30-phase-c-upstream-abstraction.md exactly. Scope is upstream HTTP routing only — translation is Phase B's job. Requires Phase A merged. Coordinates with Phase B via the shared DTO names in Protocol/OpenAIResponses.cs (Phase B owns that file; you import). If Phase B not done at integration time, stub the DTOs locally and the integration phase resolves the merge. All 7 acceptance criteria pass before PR.
```
