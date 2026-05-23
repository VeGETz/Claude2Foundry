# 7. Loopback-only trust for the Adapter Console; CSRF header guard; no auth

Date: 2026-05-23

## Status

Accepted

## Context

The Adapter Console exposes an HTTP surface (`/api/admin/*`) that can mutate config and read full request bodies (which may include user-sensitive chat content). The existing adapter binds `127.0.0.1` and explicitly lists TLS, daemon hosting, and LAN binding as v1 non-goals. The threat model is a single-user developer workstation.

Three auth models were considered:

1. No auth, rely on loopback bind.
2. Bearer token, generated on first start, stored in a file the user reads once.
3. Bearer token gated on `/v1/*` too (would conflict with Claude Code's expected `ANTHROPIC_API_KEY` semantics, which the adapter currently documents as "not used by proxy, just has to be set").

We also considered the browser-CSRF risk: any webpage the user visits could attempt a cross-origin POST to `http://127.0.0.1:8787/api/admin/config`, potentially altering the adapter's settings.

## Decision

The Adapter Console operates under **loopback trust**. There is no bearer token, no per-user auth, no session. The adapter binds `127.0.0.1` only (already enforced; out of scope to change in v1). The Console is reachable from any process running as the same user — same trust boundary as `appsettings.local.json` file access.

To block cross-origin browser attacks, every **mutating** request to `/api/admin/*` (`POST`, `PUT`, `DELETE`, `PATCH`) must carry the request header `X-C2F-Admin: 1`. The Console attaches this header to every admin fetch. Requests missing the header are rejected with `400 missing X-C2F-Admin`. The header is a custom header, so a malicious webpage cannot set it on a simple cross-origin request without triggering a CORS preflight; the adapter rejects all CORS preflights from origins other than `null` and `http://127.0.0.1:5173` (the Vite dev server during development) and `http://127.0.0.1:8787` (the embedded Console origin).

Read-only `GET /api/admin/*` requests are not gated by the header (browsers send simple GETs cross-origin without preflight, but they cannot read the response without CORS). The adapter sets `Access-Control-Allow-Origin: null` (i.e. denies cross-origin reads) on every `/api/admin/*` response.

The `/v1/*` Anthropic surface is **not** gated by the header. Claude Code does not know about it and does not need to. Claude Code's `ANTHROPIC_API_KEY` remains ignored by the adapter, as today.

## Consequences

Positive:
- Zero auth state to manage: no token file, no token rotation, no "I lost my token" recovery flow, no per-user provisioning.
- Console is reachable instantly after `c2f-bin` starts — open the URL, no setup step.
- The CSRF header guard adds two lines to the admin handler and one line to every Console fetch wrapper; it costs nothing while closing the only realistic remote attack surface.
- Aligns with the existing CONTEXT.md non-goals: TLS, LAN bind, daemon hosting all out of scope for v1.

Negative:
- Any process running as the same user can read full request bodies via `/api/admin/events`. Acceptable: a hostile local process can already read `appsettings.local.json` and the JSONL files directly. The Console adds no new exfiltration path that the file system doesn't already grant.
- A multi-user dev box (rare, but real for shared lab machines) gives every logged-in user access to the running user's Console. We document this and recommend such operators run separate adapters per user (different ports, different data dirs via `C2F_DATA_DIR`).
- If a future release wants real auth (e.g. for a hosted scenario), it has to be added without breaking existing loopback users. Path: gate `/api/admin/*` behind a new optional `Admin.Token` config field, default unset = current behavior.

## Alternatives considered

**Bearer token generated on first start.** Rejected for v1. The threat model doesn't justify the complexity: token file management, cookie/CSRF for the Console itself, recovery when the file is deleted. Costs more than it buys on a dev workstation.

**Token on `/v1/*` as well.** Rejected. Breaks the existing contract that Claude Code's `ANTHROPIC_API_KEY` is unused. Would require Claude Code users to manually align two unrelated keys.

**No CSRF guard.** Rejected. Without it, any open browser tab on the user's machine can POST to `127.0.0.1:8787/api/admin/config` and alter the adapter. The custom-header guard is cheap and standard.
