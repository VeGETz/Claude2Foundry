# 30 — Phase 2: Frontend (parallel with phase 1)

## Mode

Parallel with phase 1 (backend). One agent per worktree. **Do not edit files in phase 1's "Owned files" list.**

The phase 1 backend is stubbed at the start of this phase: routes return `501 NotImplementedException` or empty payloads. Phase 2 develops against the **contracts**, not the implementation. Phase 3 integration covers the e2e wiring.

## Owned files (write)

Entire `ui/` directory minus the placeholder files created in phase 0 (those are extended, not replaced):

- `ui/index.html` — final layout (header, nav, content slot).
- `ui/src/main.tsx` — router setup, root component.
- `ui/src/App.tsx` — shell with nav bar (Config / Monitor / Health / Test), route outlet, global toast container, restart banner host.
- `ui/src/api/client.ts` — production fetch wrapper (CSRF header, JSON, error normalization). Extends phase 0 stub.
- `ui/src/api/contracts.ts` — extend with full TypeScript types matching `docs/specs/admin-ui/contracts/*`. Hand-written; **never reduce shape** vs what backend emits.
- `ui/src/api/events.ts` — SSE client with `EventSource`, auto-reconnect, `Last-Event-ID` resume.
- `ui/src/pages/Config.tsx` — schema-driven form, save flow, restart banner integration, "Test connection" button.
- `ui/src/pages/Monitor.tsx` — live table, expand drawer, filter (status, model, error), pause/resume, clear, capture-mode toggle (session + persistent).
- `ui/src/pages/Health.tsx` — periodic poll of `/api/admin/health`, render scalars.
- `ui/src/pages/Test.tsx` — form (model dropdown sourced from ModelAliases keys + DefaultModel, message editor, thinking toggle, stream toggle), pre-send token estimate, post-send trace view (Anthropic out, OpenAI request, OpenAI response).
- `ui/src/components/SchemaForm/` — generic form renderer driven by a JSON Schema (Draft 2020-12 subset: object, array, string, number, boolean, enum, oneOf for discriminated unions).
  - `SchemaForm.tsx`
  - `FieldString.tsx`, `FieldNumber.tsx`, `FieldBoolean.tsx`, `FieldEnum.tsx`
  - `FieldMap.tsx` (for `additionalProperties: <schema>` — used by `ModelAliases`, `ReasoningPolicies`, `Tokenizers`)
  - `FieldUnion.tsx` (for `Tokenizer.Source` switching)
- `ui/src/components/RequestRow.tsx` + `ui/src/components/RequestDrawer.tsx` — Monitor table row + expanded panel.
- `ui/src/components/RestartBanner.tsx` — sticky banner shown when `restartRequired` flag is set; "Restart adapter" button calls `/api/admin/restart`; "I'll restart manually" dismisses.
- `ui/src/lib/tokenEstimate.ts` — lightweight estimator (calls the existing `/v1/messages/count_tokens` endpoint of the adapter for the Test page pre-send estimate; not a re-implementation).
- `ui/src/lib/redaction.ts` — extra defense-in-depth header masking in the UI (the backend masks, but if a future bug leaks a header, the UI masks too).
- `ui/src/styles/` — minimal CSS (UnoCSS or Pico CSS — pick one; bundle target < 30 KB CSS).
- `ui/vite.config.ts` — production config: build into `../src/Claude2Foundry/wwwroot/_ui/dist/`.
- `ui/package.json` — pinned dependency versions; scripts: `dev`, `build`, `typecheck`.
- Optional `ui/src/__tests__/` — Vitest unit tests for SchemaForm rendering and SSE client reconnect logic.

## Consumed contracts (read-only)

- `docs/specs/admin-ui/contracts/http-routes.md` — endpoint shapes.
- `docs/specs/admin-ui/contracts/sse-events.md` — SSE schema.
- `docs/specs/admin-ui/contracts/json-schema.md` — `ProxyConfig` schema reference (the live one is fetched at runtime; this doc is the spec).
- `docs/specs/admin-ui/10-phase0-contracts.md` — overall freeze.

## Must NOT touch

- `src/**` (entire backend tree owned by phase 1 + phase 0 scaffolding).
- `tests/**`.
- `docs/specs/admin-ui/contracts/*.md` — frozen.

The exception: if phase 1 introduces a contract change mid-development (e.g. new SSE event), phase 2 receives the updated `contracts/*.md` from the Tech Lead and extends `ui/src/api/contracts.ts` accordingly. No direct cross-worktree file edits.

## Tasks

### 1. App shell

1.1 Router with four routes: `/`, `/monitor`, `/health`, `/test`. `/` is Config.

1.2 Nav bar with the four links. Active link highlighted.

1.3 Toast system (transient notifications: save success, save failure, connection lost).

1.4 Restart banner host slot, shown when a global "restart pending" flag is set (set by Config save response).

### 2. Config page

2.1 On mount: `GET /api/admin/config/schema` + `GET /api/admin/config`. Render `SchemaForm` against the schema, hydrate values from the config.

2.2 Dirty-state tracking: edits mark fields dirty. Save button disabled until dirty.

2.3 Save flow:
- Validate against schema client-side; show inline errors. Save disabled if invalid.
- `POST /api/admin/config` with full proposed `proxy` payload.
- On `400 invalid_config`, render `issues[]` as inline errors at the indicated paths.
- On `200 { restartRequired: true }`, set the global restart-pending flag → banner appears.
- On `200 { restartRequired: false }`, toast "config saved" + refresh form values from response or refetch.

2.4 "Test connection" button next to `BackendUrl`:
- `POST /api/admin/config/test-connection` with the current (possibly unsaved) form values.
- Show inline result: ✓ latency + model count, or ✗ error message.
- Does not gate save.

2.5 Capture-mode toggle (also reachable from Monitor): bound to `Monitor.CaptureMode`. Saving via Config uses persistent scope.

### 3. Monitor page

3.1 On mount: open SSE `GET /api/admin/events?since=<browser-stored-last-id>`. First frame is `replay.snapshot`; render rows for each record. Subsequent live events update or append.

3.2 Table columns: time, correlation id (short), original→resolved model, status (badge: streaming/ok/error), elapsed, input tokens, output tokens, error if any.

3.3 Row click → drawer (right side panel):
- Header: full correlation id, copy button, timestamps per phase.
- Tabs: **Anthropic request** | **OpenAI request** | **Foundry response** | **Anthropic response** | **Raw events**.
- Each tab renders pretty-printed JSON or text.
- For an in-flight request, tabs say "streaming…" and update as `foundry.chunk` arrives.
- "Load full bodies" button calls `GET /api/admin/events/full/{id}` if hybrid mode dropped them. Shows "body expired" message if `expired: true`.

3.4 Filter row: status (any/ok/error/streaming), model (multi-select), error-only toggle.

3.5 Buttons: **Pause** (stops adding new rows; events still arrive in background and buffer up to 100, then drop-oldest), **Resume**, **Clear** (clears local table but does not affect server ring), **Capture: hybrid/full** (session toggle, with "Make persistent" link that calls `POST /api/admin/capture-mode` with `scope: "persistent"`).

3.6 Connection lost handling: SSE `onerror` triggers a "Disconnected, reconnecting…" toast. `EventSource` reconnects automatically; on reconnect, request includes `Last-Event-ID`; server may replay missed events.

### 4. Health page

4.1 Poll `GET /api/admin/health` every 5 s while visible. Stop polling when tab hidden (`document.visibilityState`).

4.2 Render sections:
- **Foundry**: reachable y/n, last latency, last probe time, last failure (if any).
- **Adapter**: uptime, version, .NET version, data dir path, wrapper present y/n.
- **Traffic**: in-flight count, ring buffer occupancy bar, capture mode (session / persistent).
- **Logs**: current JSONL file path + size; link to "Open data dir" (best-effort — opens with `os.openurl`).
- **Config**: collapsible JSON view of the effective config with api-key value masked.

### 5. Test page

5.1 Form:
- Model: dropdown sourced from current config's `ModelAliases` keys + an "other (type custom Claude model id)" option that falls through to `DefaultModel`.
- Messages: simple editor — turn role (user/assistant) + content text. Initially one user turn; add-turn button.
- Thinking: checkbox + budget slider (1000 / 4000 / 16000 — three preset points to demo low/med/high effort).
- Stream: checkbox.

5.2 Pre-send estimate: `POST /v1/messages/count_tokens` against the adapter's local count endpoint, show estimated input tokens + "Estimated cost: 0 (counts are not billing-accurate)" disclaimer.

5.3 Submit: `POST /api/admin/test-request`. Response opens an inline trace panel identical in shape to the Monitor drawer.

5.4 Session tally widget: tokens sent / received this page session. Resets on page reload. No persistence.

### 6. SSE client

6.1 `ui/src/api/events.ts` wraps `EventSource`. Handles:
- Connection lifecycle (open / message / error).
- Per-event-type dispatch (each `event:` name routes to a typed callback).
- Last-Event-ID resume across reconnects.
- Bounded internal queue for paused state (max 100 events; drop oldest).

### 7. Schema-driven form

7.1 `SchemaForm` walks a JSON Schema and renders fields by type. Supports:
- `string` (text input; `pattern` → regex validation).
- `number` / `integer` (numeric input; `minimum`/`maximum` enforced).
- `boolean` (checkbox).
- `enum` (dropdown).
- `object` with `properties` (nested fieldset).
- `object` with `additionalProperties` (key-value map editor: add/remove rows, each value follows the sub-schema).
- `oneOf` with a `discriminator` (radio + conditional sub-fields). Used for `Tokenizers.*` (`Source` switches between Tiktoken vs HuggingFace).

7.2 Validation: synchronous against schema before save-enable. Async errors (from server) merged into the same error-message store.

## Acceptance criteria

1. `pnpm run build` succeeds. Output bundle is < 200 KB gzipped total (verify in build report).
2. `pnpm run dev` starts; navigating to `http://localhost:5173/` shows the Console with proxy to `:8787` working.
3. Config page renders dynamically from `/api/admin/config/schema` — adding a new field to the schema (server-side) makes a new form field appear without UI code change.
4. Monitor page connects to SSE, shows replay records + live updates, allows expanding for full bodies.
5. Saving a `BackendUrl` change triggers the restart banner. Clicking "Restart" calls `/api/admin/restart`; UI shows "Restarting…" and waits 15 s before retrying connection.
6. Test page submits and shows a trace.
7. Without `X-C2F-Admin: 1` header (test by hand-removing it in DevTools), every save fails with 400 surfaced as a toast — confirms client wrapper is enforcing it.
8. TypeScript strict mode passes; no `any` in the contracts module.

## Test matrix

| Component | Test | Pass criterion |
|---|---|---|
| `SchemaForm` | renders map field | `additionalProperties` schema produces add/remove UI |
| `SchemaForm` | renders union | `oneOf` with discriminator switches sub-fields |
| `SchemaForm` | client validation | pattern mismatch disables save with inline error |
| SSE client | reconnect | `EventSource.onerror` reconnects with `Last-Event-ID` |
| SSE client | paused queue | 200 events while paused → 100 in buffer (oldest dropped) |
| Config save | restart flag | response with `restartRequired: true` sets global banner |
| Monitor drawer | expired body | `expired: true` response shows "body expired" message |
| Test page | model dropdown | dropdown options equal current `ModelAliases` keys |
| CSRF | client always sends header | Vitest mocks fetch; verifies every non-GET adds `X-C2F-Admin: 1` |

## Risks / open Qs

- **Bundle size budget.** Preact + preact-iso ≈ 8 KB. Adding `monaco-editor` for code editing in the Test page would blow the budget; use a simple textarea + syntax-aware highlighting only if a tiny lib (< 10 KB) is found, else plain textarea.
- **JSON Schema 2020-12 coverage.** The `SchemaForm` component supports only the subset listed in task 7.1. If the schema endpoint emits constructs outside that subset (e.g. `if`/`then`/`else`), the form falls back to a raw JSON editor for that subtree. Phase 1's schema is constrained to the supported subset by construction.
- **Mobile viewport.** Not a target. Min viewport 1024 × 720 — declared in `<meta>` viewport. No mobile QA in v1.
