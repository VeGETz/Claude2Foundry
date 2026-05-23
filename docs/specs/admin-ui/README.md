# Adapter Console — Specifications

Specifications for the Adapter Console feature: a local web UI hosted by the Claude2Foundry adapter at `/_ui/` on `127.0.0.1:8787` that replaces hand-editing of `appsettings.json` and provides a live view of every request transiting the adapter.

These specs extend the parent specs in [`../`](../) — read [`../README.md`](../README.md) first if you are new to the project.

## Reading order

1. [`00-overview.md`](./00-overview.md) — Scope, non-goals, success criteria for the Console feature.
2. [`10-phase0-contracts.md`](./10-phase0-contracts.md) — Phase 0 (serial preamble). HTTP contracts, SSE event schema, JSON Schema, data dir resolution, CSRF guard, scaffolding tasks shared by all later phases.
3. [`20-phase1-backend.md`](./20-phase1-backend.md) — Phase 1 (parallel). Admin API handlers, ring buffer, JSONL writer, IOptionsMonitor layering, restart wiring, validation, request-capture pipeline.
4. [`30-phase2-frontend.md`](./30-phase2-frontend.md) — Phase 2 (parallel). Vite + Preact app, 4 pages (Config, Monitor, Health, Test), SSE consumer, schema-driven forms.
5. [`40-phase3-integration.md`](./40-phase3-integration.md) — Phase 3 (serial). Embedding pipeline (csproj + ManifestEmbeddedFileProvider), pnpm/dotnet wiring, wrapper scripts, e2e tests, README + packaging docs.

## Phased execution model

The Console is delivered in four phases designed to enable two coding agents working in separate git worktrees:

| Phase | Mode | Agent | Estimated work |
|---|---|---|---|
| 0 — Contracts + scaffolding | Serial (1 agent) | Either | ~30 k tokens |
| 1 — Backend | Parallel with phase 2 | Agent A (`worktree/backend`) | ~80 k tokens |
| 2 — Frontend | Parallel with phase 1 | Agent B (`worktree/frontend`) | ~80 k tokens |
| 3 — Integration + polish | Serial (1 agent) | Either | ~40 k tokens |

Phase 0 freezes the HTTP contracts and the on-disk JSON Schema endpoint so phases 1 and 2 can proceed without coordinating. Each phase doc explicitly lists **owned files** (write paths), **consumed contracts** (read-only inputs), and **produced contracts** (outputs the next phase relies on).

If a coding agent needs to touch a file owned by another phase, escalate to the Tech Lead before editing — that is a phase-collision risk that must be resolved at the spec level, not in code.

## Hard-to-reverse decisions

Decisions taken outside this spec tree but load-bearing for it:

- [`../../adr/0003-embedded-spa-vite-preact.md`](../../adr/0003-embedded-spa-vite-preact.md) — Stack: Vite + Preact + TypeScript, embedded in the binary.
- [`../../adr/0004-layered-config-full-snapshot.md`](../../adr/0004-layered-config-full-snapshot.md) — `appsettings.local.json` layering, full-snapshot writes.
- [`../../adr/0005-restart-on-exit-75-wrapper.md`](../../adr/0005-restart-on-exit-75-wrapper.md) — Hot-reload + exit-75 wrapper.
- [`../../adr/0006-request-capture-jsonl-not-ilogger.md`](../../adr/0006-request-capture-jsonl-not-ilogger.md) — Ring + JSONL, distinct from `ILogger`.
- [`../../adr/0007-loopback-trust-csrf-header.md`](../../adr/0007-loopback-trust-csrf-header.md) — Loopback trust, `X-C2F-Admin` guard.

## Glossary

All Adapter Console terms (Console, Admin API, bootstrap/operational fields, layered config, data dir, request capture, wrapper script) are defined in [`/CONTEXT.md`](../../../CONTEXT.md#glossary). Do not re-define here; link to the glossary.
