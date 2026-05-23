# 00 — Adapter Console: Overview

## What this is

A local web UI hosted by the Claude2Foundry adapter on the same Kestrel listener that serves the existing Anthropic surface. The UI runs at `http://127.0.0.1:8787/_ui/` and is consumed exclusively by a browser on the same machine. It exists to replace hand-editing of `appsettings.json` and to give the developer a real-time view of every request transiting the adapter.

The UI is a single-page application built with Vite + Preact + TypeScript, embedded in the .NET 10 single-file binary via `ManifestEmbeddedFileProvider`. It speaks to the adapter through a JSON HTTP surface at `/api/admin/*` and consumes a live SSE feed at `/api/admin/events`. None of the existing `/v1/*` endpoints change.

## Why

- Operators no longer want to hand-edit JSON to swap model aliases or tune reasoning policies.
- The adapter is a translation pipeline whose correctness is hard to inspect through stdout logs alone — a structured per-request view (with expandable bodies) accelerates debugging.
- The same UI doubles as a "control panel" feel that makes a foreground process easier to live with.

## Scope (in)

- Single-page app at `/_ui/` with four views: **Config**, **Monitor**, **Health**, **Test**.
- Full edit of every `Proxy.*` config field via UI, persisted to `appsettings.local.json` in the data dir.
- Hot-reload of operational fields (everything except `BackendUrl` and `ApiKeyEnv`); restart-required banner + button for bootstrap fields, backed by exit-75 + optional wrapper script.
- Live SSE stream of every request transiting the adapter, with phase-by-phase events and click-to-expand full bodies.
- Rolling JSONL request-capture file at `<datadir>/logs/requests-*.jsonl` (separate from `ILogger`).
- `GET /api/admin/config/schema` returning JSON Schema for `ProxyConfig` to drive client-side form rendering.
- CSRF guard via `X-C2F-Admin: 1` required on mutating admin requests.
- "Test connection" button against Foundry on the Config page; "Test request" form on the Test page.

## Scope (out)

- No bearer-token auth, no per-user accounts, no TLS. Loopback trust is the entire security model (see [ADR-0007](../../adr/0007-loopback-trust-csrf-header.md)).
- No multi-instance / cluster monitoring. One adapter, one Console.
- No editable Console of historical JSONL files in v1 — they are read-only from the UI; users can `jq` them externally. The UI hydrates recent history into the live view via `?since=<timestamp>` on connect.
- No charts / metrics dashboard in v1. The Health view shows scalars (uptime, in-flight count, etc.) but no time-series charts. Defer to v2.
- No diff viewer (side-by-side Anthropic vs OpenAI bodies) as a dedicated page in v1. The Monitor expand drawer shows both bodies, which covers the use case.
- No cost-estimator / budget cap. Test page shows estimated input tokens before send as a soft warning; no hard cap (see Q16 grilling).
- No editing of `appsettings.json` (the repo-shipped base file). Only `appsettings.local.json` is UI-mutable.

## Success criteria

1. Starting the adapter with no `appsettings.local.json` present: opening `http://127.0.0.1:8787/_ui/` shows the Config view hydrated from `appsettings.json`. Editing `ModelAliases` and saving creates `appsettings.local.json` in the data dir; the next inbound request from Claude Code uses the new alias without restart.
2. While a streaming `/v1/messages` request is in flight, the Monitor view shows the request row immediately with phase `streaming` and ticks token counts as chunks arrive. Clicking the row opens a drawer with the original Anthropic body, the translated OpenAI body, and the assembled response as the stream completes.
3. Editing `BackendUrl` and saving: the Console returns `restartRequired: true`; the page shows a banner with a "Restart adapter" button. With the wrapper script, clicking the button restarts the adapter in the same terminal within 60 seconds and reloads the UI on the new process.
4. Killing the adapter (`Ctrl-C` or wrapper restart) and starting it again: the Monitor view, on reconnect, replays the last N entries from the JSONL file so recent history is not lost.
5. A malicious local webpage attempting a cross-origin `POST` to `/api/admin/config` without `X-C2F-Admin: 1` is rejected with `400`. The Console's own `POST`s succeed because the Console attaches the header.
6. `dotnet publish -c Release` for a single-file target produces a binary that, when run from any directory, serves the embedded UI correctly without external files (the Console + JS bundle live inside the binary; only `appsettings.local.json` and JSONL files are read/written from the data dir).

## Out-of-scope risks called out

- **JSONL disk growth.** Documented in README. Users can reduce `Monitor.LogRetentionDays` or set it to `0` to disable persistence.
- **Test page billing.** Documented in README. The Test page shows estimated input tokens before send; no hard cap.
- **Wrapper script absence.** Handled: Console hides the Restart button and shows a manual-restart toast when `C2F_WRAPPER` env var is not set. Bootstrap field saves still write to disk; only the in-process restart action is unavailable.
