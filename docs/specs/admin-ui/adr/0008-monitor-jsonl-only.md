# ADR-0008 — Monitor uses JSONL on disk as the only capture store

**Status:** Accepted, 2026-05-24.
**Supersedes:** ADR-0004 (hybrid/full capture modes), the FullBodyCache portion of ADR-0006.

## Context

Phase 1 shipped a three-tier capture pipeline (in-mem ring + FullBodyCache LRU + JSONL writer) gated by a hybrid/full mode toggle. Across phase 1–3 and post-merge fixes it produced one runtime regression that broke `/v1/messages` itself, plus four follow-up patches for JsonElement lifecycle, snapshot detach, emission ordering, and contract drift.

Adapter core is the product. Monitor is a debug aid. Investment ratio is inverted.

## Decision

Capture pipeline is reduced to one mechanism:

- One JSONL file at `<datadir>/logs/requests-<bootId>.jsonl`.
- Purged on every startup.
- All admin Monitor endpoints read from disk.
- No in-memory ring. No FullBodyCache. No hybrid/full mode. No capture-mode toggle.

## Consequences

**Gain:**
- One storage tier, one code path. Fewer states to reason about.
- Capture failures cannot break `/v1/messages` — writes are synchronous serialize + async append, with explicit catch.
- Restart guarantees a clean slate. No leaked state across boots.
- Removes ~6 files of pipeline plumbing.

**Lose:**
- No cross-boot history. Acceptable: dev tool, restart cheap.
- No per-frame streaming visibility. Acceptable: console already buffers stream-then-log.
- Disk IO on every request. Bounded by `Monitor.MaxBodyBytes` (default 10 MB).

## Rejected alternatives

- **Keep ring + JSONL, drop only the cache.** Still two stores, still synchronization between them, still mode toggle. Doesn't actually address the complexity.
- **In-memory only.** User explicitly chose disk-backed; survives across SPA reloads and gives a tail-able artifact.
- **Per-frame SSE capture.** Streaming bodies become hard to bound; live tail is nice-to-have, not core.
