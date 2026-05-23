# 4. Layered config with `appsettings.local.json` full-snapshot writes

Date: 2026-05-23

## Status

Accepted

## Context

Before the Adapter Console, all configuration lived in `src/Claude2Foundry/appsettings.json`, edited by hand. The Console is a hard requirement to remove hand-editing: every `Proxy.*` field is editable via the UI. The Console needs a place to write user-owned config that doesn't pollute the repo-shipped `appsettings.json` (which contains documented examples like `claude-opus-4-7 → GPT-5.4-mini`) and doesn't risk accidental commit of operator-specific deployment values.

We considered: writing back to the same `appsettings.json` (single source of truth), writing to a sidecar `appsettings.local.json` (layered), and changing format (rejected on sight).

## Decision

The adapter binds `IConfiguration` from two layered JSON files, in order:

1. `appsettings.json` — repo-shipped defaults and examples. Loaded from the binary directory (matches existing behavior).
2. `appsettings.local.json` — user-owned, UI-managed, optional. Loaded from the **data dir** (`C2F_DATA_DIR` env var, else OS user data dir; see CONTEXT.md glossary). `reloadOnChange: true`.

The local file overrides the base file key-by-key. The Console writes a **full snapshot of the entire `Proxy` section** to `appsettings.local.json` on every save — not a diff against the base. The base file ships with `appsettings.local.json` listed in `.gitignore`.

On first run with no `appsettings.local.json`, the Console hydrates its form from the merged effective config (which equals the base file). On first save, the local file is created with the user's full `Proxy` section.

If `appsettings.local.json` exists but is malformed (invalid JSON, missing required fields), the adapter falls back to the base configuration and surfaces a banner in the Console: `"local config invalid: <error>, running on defaults"`. The corrupt file is not auto-deleted.

## Consequences

Positive:
- Repo `appsettings.json` stays stable across releases: it's the canonical example documentation. Users do not need to merge upstream changes by hand.
- User state lives in the standard per-user data dir, never in the repo tree. Multi-user dev boxes get independent state.
- Full-snapshot writes are predictable: the active `Proxy` section is exactly what's on disk. No "what overrides what" reasoning required during debugging.
- Hand-editing still works in either file.

Negative:
- A user who customizes one field gets a full `Proxy` snapshot on disk including all defaults. If a future release ships a new default for a field they never customized, the user keeps the stale value until they re-save or hand-edit. We accept this; the Console can grow a "reset to defaults" button later if it bites.
- Two files = two places to look. We mitigate by surfacing the resolved effective config in the Console's Health page.

## Alternatives considered

**Single file (write back to `appsettings.json`).** Rejected. Pollutes the repo file with operator-specific values like `BackendUrl`; high risk of accidental commit of someone's actual Foundry resource URL. Also fights the existing examples in `appsettings.json` that document the supported feature surface.

**Diff-only writes to `appsettings.local.json`.** Rejected. Saves disk bytes but creates an "implicit inheritance" trap: a future release that changes a base default silently changes effective config for every user whose local file did not pin that field. Full snapshot eliminates the trap at the cost of a few hundred bytes.

**New format (YAML / TOML / custom).** Rejected without serious discussion. JSON binding works. The cost of a format change is high (existing users, existing tests, existing tooling) for zero benefit.
