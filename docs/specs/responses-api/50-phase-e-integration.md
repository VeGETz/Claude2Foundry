# 50 — Phase E: Integration, UI, docs

**Mode:** Serial after Phase D merged.

UI wires the new fields. README + CONTEXT updated. End-to-end test matrix.

## Owned files (write)

- `ui/src/pages/Config.tsx` — render `BackendKind`, `BackendAuthScheme`, `BackendApiVersion`, `Responses.StorePolicy`, `PrefixCache.*` fields. Use JSON Schema from `/api/admin/config/schema`. Restart-required fields surface the standard "Restart needed" banner.
- `ui/src/pages/Monitor.tsx`, `ui/src/components/MonitorRow.tsx`, `ui/src/components/MonitorDetail.tsx` — chain badge + saved-bytes display + chain detail panel.
- `ui/src/pages/Health.tsx` — show `BackendKind` next to `BackendUrl`.
- `README.md` — Backend Kinds section (Chat vs Responses tradeoffs, supported endpoints), prefix-cache explanation, privacy note about `store:true` server-side retention.
- `CONTEXT.md` — glossary entries finalized.
- `docs/specs/responses-api/README.md` — link from `docs/specs/README.md`.
- `tests/Claude2Foundry.Tests/Integration/BackendMatrixE2E.cs` (new) — runs the smoke matrix below.

## UI

### Config page — new section "Backend"

```
Backend
├── Backend URL          [https://….openai.azure.com/openai/v1/         ] [Test connection]
├── API key env var      [FOUNDRY_API_KEY                                ]
├── Backend kind         (○ Chat Completions  ● Responses)               [Restart required]
├── Auth scheme          (● Api Key            ○ Bearer    )             [Restart required]
├── API version          [                                               ] (optional)
└── Responses
    ├── Store policy     (○ Always  ● When chaining  ○ Never)
    └── Prefix cache
        ├── Enabled       [✓]
        ├── Capacity      [256                                           ]
        └── TTL (minutes) [30                                            ]
```

Tooltips:
- **Backend kind:** "Chat Completions = legacy OpenAI protocol, full history every turn. Responses = newer protocol, supports stateful chains via prefix cache."
- **Store policy:** "When chaining: stores responses only when the prefix cache is enabled. Foundry/OpenAI retains stored responses up to 30 days server-side."
- **Prefix cache:** "Reduces request payload on multi-turn sessions by sending only the new turn after the first."

### Monitor row badge

- Hit: `🔗 18.2 KB saved`
- Miss: `—`
- Fallback: `⚠ chain stale`
- Disabled (Chat path or cache off): no badge

## Smoke matrix

Eight cells. Run all in `BackendMatrixE2E`:

| Backend | Auth | Stream | Tools | Pass criterion |
|---|---|---|---|---|
| Chat | ApiKey | no | no | response.content text non-empty |
| Chat | ApiKey | yes | yes | tool_use block emitted in stream, tool_result returns final text |
| Chat | Bearer | no | no | same as row 1 against OpenAI-shaped stub |
| Responses | ApiKey | no | no | response.content text non-empty |
| Responses | ApiKey | yes | yes | tool_use block emitted in stream, tool_result returns final text |
| Responses | ApiKey | yes | yes (5-turn chain) | chainStatus transitions correctly across turns |
| Responses | Bearer | no | no | OpenAI-shaped stub, Bearer auth, Responses path |
| Responses | ApiKey | no | yes (reasoning model stub) | `thinking` block precedes `text` block in response |

## Docs additions to root README

```markdown
## Backend kinds

The adapter supports two upstream protocols, switchable via `Proxy.BackendKind`:

- **`ChatCompletions`** (default): targets `/v1/chat/completions`. Works with Azure Foundry, OpenAI direct, OpenRouter, vLLM, llama.cpp, and any OpenAI-compatible endpoint.
- **`Responses`**: targets `/v1/responses`. Newer OpenAI protocol with stateful multi-turn via `previous_response_id`. Required for `o3` and `gpt-5.x` reasoning visibility.

Auth header is controlled by `Proxy.BackendAuthScheme`:
- `ApiKey` (default): `api-key: <key>` (Azure/Foundry convention).
- `Bearer`: `Authorization: Bearer <key>` (OpenAI/OpenRouter/most others).

## Prefix cache (Responses only)

When `BackendKind: Responses` and `PrefixCache.Enabled: true`, the adapter remembers the response id from each turn and sends only the new user message on the next, chained via `previous_response_id`. Multi-turn sessions in Claude Code see reduced request size after the first turn.

Privacy note: this requires `store: true` on the upstream, which means OpenAI/Foundry retains your prompts up to 30 days server-side. Disable the cache (`PrefixCache.Enabled: false`) or set `Responses.StorePolicy: Never` if this is unacceptable.
```

## Acceptance criteria

1. UI Config page renders all new fields with correct types and validation.
2. Restart banner appears when bootstrap fields change.
3. Monitor row badge renders for all four chain states.
4. Monitor detail panel shows chain hash + previous_response_id when present.
5. Health page label updated.
6. All 8 smoke matrix cells pass.
7. README + CONTEXT changes committed.
8. `dotnet test` green, `pnpm test` green, `pnpm typecheck` green.

## Engineer prompt

```
Implement /mnt/c/@Projects/Claude2Foundry/docs/specs/responses-api/50-phase-e-integration.md exactly. Requires Phase D merged. All 8 acceptance criteria pass. Capture screenshot of Config page with new section for the PR description.
```
