# 10 — Phase A: Backend config + auth abstraction

**Mode:** Serial. One agent. Blocker for Phase B and Phase C.

Add config fields, rename Foundry-specific class names, abstract auth. No translation logic changes. No new behavior on the hot path beyond honoring `BackendAuthScheme`.

## Owned files (write)

- `src/Claude2Foundry/Config/ProxyConfig.cs` — add three fields (see below). Add nested `PrefixCacheConfig` and `ResponsesConfig` records (used in Phase B/D, defined now so JSON schema is final).
- `src/Claude2Foundry/Config/ConfigValidation.cs` — relax `BackendUrl` validator (trailing slash only). Add validators for new fields.
- `src/Claude2Foundry/Admin/ConfigSchemaProvider.cs` — JSON Schema mirrors new fields, enum constraints on `BackendKind` and `BackendAuthScheme`.
- `src/Claude2Foundry/Backend/FoundryClient.cs` → rename file + class to `UpstreamClient.cs` / `UpstreamClient`. Header injection switches on `BackendAuthScheme`.
- `src/Claude2Foundry/Backend/FoundryHealthProbe.cs` → `UpstreamHealthProbe.cs` / `UpstreamHealthProbe`. Probe target path stays `/chat/completions` for now; Phase C makes it conditional on `BackendKind`.
- `src/Claude2Foundry/Backend/FoundryLoggingHandler.cs` → `UpstreamLoggingHandler.cs` / `UpstreamLoggingHandler`. Console prefixes `[C2F → Foundry]` / `[C2F ← Foundry]` → `[C2F → Upstream]` / `[C2F ← Upstream]`.
- `src/Claude2Foundry/Protocol/OpenAIChat.cs` — rename `FoundryError`/`FoundryErrorResponse` → `OpenAIError`/`OpenAIErrorResponse`. `FoundryHttpException` lives here; rename to `UpstreamHttpException` and move to `Backend/`.
- `src/Claude2Foundry/Errors/ErrorMapping.cs` — `FoundryError` method → `UpstreamError`. `MapFoundryStatus` → `MapUpstreamStatus`. Message prefix `[Foundry]` → `[Upstream]`.
- `src/Claude2Foundry/Program.cs` — DI registrations + named HttpClient builder switches header per `BackendAuthScheme`.
- `tests/**/*.cs` — all `Foundry*` references updated. `FakeFoundryHandler` → `FakeUpstreamHandler`. `FoundryError_HasFoundryPrefix` → `UpstreamError_HasUpstreamPrefix`. Assertion strings updated.
- `ui/src/api/contracts.ts` — `FoundryHealth` → `UpstreamHealth`.
- `ui/src/pages/Health.tsx` (or equivalent) — label "Foundry" → "Upstream" + show BackendUrl as subtitle.
- `docs/specs/admin-ui/contracts/sse-events.md` — rename `foundry.*` events to `upstream.*`. JSONL kind names follow: `response.received` already exists from 50-monitor-rewrite — no SSE event rename needed if Monitor was already JSONL-only. **Audit before renaming.**
- `CONTEXT.md` — glossary update.
- `appsettings.json` (the repo sample) — add new fields with defaults.

## Config schema additions

```jsonc
{
  "Proxy": {
    "BackendUrl": "https://…/openai/v1/",
    "ApiKeyEnv": "FOUNDRY_API_KEY",

    // NEW:
    "BackendKind": "ChatCompletions",          // or "Responses"
    "BackendAuthScheme": "ApiKey",             // or "Bearer"
    "BackendApiVersion": null,                 // optional "?api-version=…"

    "Responses": {                             // used by Phase B only; ignored when Kind==ChatCompletions
      "StorePolicy": "WhenChaining"            // Always | Never | WhenChaining
    },

    "PrefixCache": {                           // used by Phase D only
      "Enabled": true,
      "Capacity": 256,
      "TtlMinutes": 30
    },

    // existing:
    "DefaultModel": "…",
    "ModelAliases": { … },
    "ReasoningPolicies": { … },
    "Tokenizers": { … },
    "Timeouts": { … },
    "Monitor": { … }
  }
}
```

## Tasks

1. Add fields to `ProxyConfig.cs` with `[JsonPropertyName]`, defaults.
2. Update validators: enum check on `BackendKind`, `BackendAuthScheme`, `Responses.StorePolicy`. Range check on `PrefixCache.Capacity` (>0, <10000) and `TtlMinutes` (>0, <1440).
3. Soften `BackendUrl`: require trailing `/`. Warn (don't error) if `BackendKind == ChatCompletions` and URL ends `/responses` (mismatched). Warn (don't error) if URL doesn't contain `/v1`.
4. Update JSON Schema in `ConfigSchemaProvider.cs` with `enum` constraints + descriptions.
5. Rename Foundry→Upstream as listed above. **No new behavior** beyond auth-header switch.
6. Auth-header switch in `UpstreamClient`:
   ```
   if (cfg.BackendAuthScheme == "Bearer")
     req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
   else
     req.Headers.Add("api-key", apiKey);
   ```
7. `BackendApiVersion` → append `?api-version={value}` to the upstream URL when non-null. Existing `BackendUrl` may already contain a query string in user configs — handle correctly (append `&` not `?`).
8. Update Health probe to use whichever scheme is configured.
9. Update all tests for rename + auth scheme. Add 2 new tests:
   - `Bearer scheme sets Authorization header, not api-key`
   - `ApiKeyEnv resolution still works under both schemes`
10. Update `appsettings.json` sample + `CONTEXT.md` glossary.

## Acceptance criteria

1. `dotnet build` warns 0.
2. All existing tests pass after rename (no behavior change with default config).
3. `BackendKind: "Responses"` is accepted by validator (does nothing yet — Phase B wires it).
4. With `BackendAuthScheme: "Bearer"`, requests to a fake OpenAI-shaped endpoint carry `Authorization: Bearer …` and no `api-key` header.
5. With `BackendApiVersion: "preview"`, requests to fake endpoint hit `…/chat/completions?api-version=preview`.
6. UI Config page renders the three new fields with descriptions from the JSON Schema.
7. UI Health page reads `upstream.…` keys; "Foundry" label gone.
8. JSONL Monitor records still load and render (no field renames touch Monitor schema).

## Out of scope

- Routing to `/responses` endpoint (Phase B).
- Prefix cache logic (Phase D).
- Stream translator changes (Phase B).

## Engineer prompt

```
Implement /mnt/c/@Projects/Claude2Foundry/docs/specs/responses-api/10-phase-a-backend-config.md exactly. Scope is config + rename + auth header switch — no translation behavior changes. All 8 acceptance criteria must pass before PR. Smoke test via curl against the existing Foundry endpoint with default config to confirm no regression.
```
