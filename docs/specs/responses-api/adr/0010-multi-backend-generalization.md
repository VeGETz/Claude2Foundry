# ADR-0010 — Generalize backend beyond Azure Foundry

**Status:** Accepted, 2026-05-24.

## Context

Adapter currently assumes Azure Foundry as the only upstream:
- `Proxy.BackendUrl` must end with `/openai/v1/` (Foundry shape).
- Auth uses `api-key` header (Azure convention).
- Class names: `FoundryClient`, `FoundryHealthProbe`, `FoundryLoggingHandler`, `FoundryHttpException`, `FoundryError`.
- ADR-0002 frames the project as Foundry-only.

Any OpenAI-compatible upstream that ships `/v1/chat/completions` or `/v1/responses` works at the wire level. Differences are limited to:

- **Base URL shape.** Foundry: `…/openai/v1/`. OpenAI: `https://api.openai.com/v1/`. OpenRouter: `https://openrouter.ai/api/v1/`. vLLM/llama.cpp: `http://localhost:8000/v1/`.
- **Auth header.** Azure: `api-key: …`. Everyone else: `Authorization: Bearer …`.
- **API version query.** Azure preview endpoints: `?api-version=…`. v1 stable Foundry + others: not needed.

## Decision

Generalize:

1. `Proxy.BackendUrl` validator: require trailing `/` only. Drop Foundry-specific `/openai/v1/` check. Add advisory warning (not error) if the URL doesn't look OpenAI-shaped.
2. Add `Proxy.BackendAuthScheme` ∈ `{ApiKey, Bearer}`. Default `ApiKey`. Header name and prefix derived from this.
3. Add `Proxy.BackendApiVersion` ∈ optional `string`. When set, appended as `?api-version=…` to the upstream URL. Default unset (matches v1 stable Foundry and OpenAI direct).
4. Rename `FoundryClient` → `UpstreamClient`, `FoundryHealthProbe` → `UpstreamHealthProbe`, `FoundryLoggingHandler` → `UpstreamLoggingHandler`, `FoundryHttpException` → `UpstreamHttpException`. Keep `FoundryError` DTO name in `Protocol/OpenAIChat.cs` since it's a wire DTO (OpenAI error envelope) — but rename to `OpenAIError` for accuracy.
5. Keep project/folder/binary name `Claude2Foundry`. Brand identity. Rename is class-level only.
6. Error message prefixes `[Foundry]` become `[Upstream]`. Console + JSONL + Anthropic error envelope.
7. UI Health page label: "Foundry" → "Upstream"; show resolved BackendUrl as the title.
8. SSE event names in admin Monitor: `foundry.chunk` → `upstream.chunk`, etc.

## Compatibility

Old `appsettings.json` keeps working. `BackendUrl` stays. `BackendKind` and `BackendAuthScheme` default sensibly. No env-var rename: `FOUNDRY_API_KEY` is referenced by user-defined `ApiKeyEnv` — they can already point at any name.

## Consequences

**Gain:**
- One adapter, any OpenAI-compatible endpoint.
- Project name + brand stay, internals shed assumption.

**Lose:**
- One-PR rename touches many files (cosmetic). Tests + docs + SSE contracts churn.
- Glossary update across `CONTEXT.md`, README, spec set.

## Rejected alternatives

- **Keep names, only add config.** Wire works but every error message + every Health page still says "Foundry" when pointed at OpenAI direct. Confusing.
- **Full rename to `Claude2OpenAI` or similar.** Bigger blast radius. Brand churn. No real win — project name is fine as-is.
