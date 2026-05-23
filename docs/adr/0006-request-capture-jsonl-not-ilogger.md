# 6. Request capture via in-memory ring + rolling JSONL; separate from `ILogger`

Date: 2026-05-23

## Status

Accepted

## Context

The Adapter Console Monitor view must show every request transiting the adapter in real time, allow expanding any row to see the full bodies (Anthropic in, OpenAI translated, Foundry response), and survive an adapter restart (so a developer post-mortem isn't lost on a `Ctrl-C`).

The existing CONTEXT.md `Logging` section is explicit: `ILogger` writes to stdout only; no file sink, no third-party logger. This rule covers operational logging (per-request summaries, warnings, errors). It does not contemplate structured per-request capture for a UI.

We considered: in-memory ring buffer only (lost on restart), in-memory ring + rolling JSONL file (survives restart, greppable), and embedded SQLite (queryable but heavier).

## Decision

The adapter maintains a **bounded in-memory ring buffer** of `RequestRecord` entries (default 500) for live SSE replay to Console clients. The ring is the source of truth for "what's new" — Console clients receive a snapshot of the ring on connect, then live events thereafter via SSE.

In parallel, every `RequestRecord` is appended to a **rolling JSONL file** at `<datadir>/logs/requests-YYYY-MM-DD.jsonl`. Files rotate at calendar-day boundaries and at 100 MB per file (configurable: `Monitor.LogMaxBytes`). Files older than 7 days are deleted on startup and once per hour (configurable: `Monitor.LogRetentionDays`).

`RequestRecord` capture mode is `hybrid` by default and `full` when opted into:

- **hybrid** — record carries: correlation id, timestamps per phase, original + resolved model, status, elapsed, token usage, error if any, headers minus secrets, and a body preview (first 8 KB of each side). Full bodies live in a separate short-TTL in-memory cache (last 100 fully-captured requests), evicted FIFO; older expansions return a `body-expired` stub.
- **full** — record carries everything `hybrid` does plus the complete body payloads. The Console can be put into `full` for the current session (button) or persistently (`Monitor.CaptureMode = "full"` config field; UI button takes precedence for the current session).

Header redaction: the outbound `api-key` / `Authorization` headers on Foundry calls are replaced with `***`. User content (chat messages, system prompts, tool results) is **not** redacted — it's the user's own data and is the point of the Monitor view.

This capture is independent of `ILogger`. `ILogger` continues to write only to stdout per existing CONTEXT.md. The JSONL file is "request capture", not "logging".

## Consequences

Positive:
- Live Monitor view is fast: the ring is in memory, no disk I/O on the read path.
- Post-mortem grep works: `jq` over `requests-*.jsonl` reproduces any historical request without restarting the adapter.
- Restart survives: the Console reloads recent history from the JSONL files via `GET /api/admin/events?since=<timestamp>` on reconnect.
- Hybrid mode caps memory: a chat with 50 long turns no longer pins ~5 MB of bodies in the ring per entry.
- The `ILogger` "no file sink" rule stays intact — request capture is a separate, declared file artifact.

Negative:
- Disk usage grows with traffic. 100 MB × 7 days = 700 MB worst case for a heavy user. Documented in README. `Monitor.LogRetentionDays = 0` disables persistence (in-memory ring only).
- "Full" mode under sustained streaming traffic with large prompts can produce multi-MB JSON lines. JSONL still parses, but `jq` over very long lines is slow. Hybrid stays the default.
- The two surfaces (`ILogger` stdout, JSONL request capture) both reflect adapter activity but in different shapes. README must explain that the JSONL is the Monitor's data store, not "the log".

## Alternatives considered

**In-memory only (no JSONL).** Rejected. Loses history on restart, including the user's own restart from the Console's Restart button — exactly when they're most likely to want history.

**Embedded SQLite.** Rejected for v1. SQLite adds a dependency, requires migrations on schema changes, and gains us nothing the JSONL doesn't (no filter/search UI is planned for v1). If a future Metrics or Search view needs indexed queries, revisit.

**File sink on `ILogger` itself.** Rejected. Changes the existing `ILogger` contract (stdout-only) and conflates operational logs with structured per-request capture. Two consumers (operators reading stdout, Console reading JSONL) want different shapes; one combined file would compromise both.

**Redact user chat content.** Rejected. The Monitor exists so the developer can see what they're sending; redacting it makes the feature pointless. The threat model is a local dev box (loopback bind, no LAN), so the content is already only visible to the running user.
