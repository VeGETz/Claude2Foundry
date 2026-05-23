# 3. Adapter Console as embedded Vite + Preact + TypeScript SPA

Date: 2026-05-23

## Status

Accepted

## Context

The Adapter Console (a local web UI for monitoring traffic and editing config) needs to ship inside the existing single-file self-contained .NET 10 binary, run on `127.0.0.1:8787` alongside the existing Anthropic surface, and not break the "no manual configuration" promise of the adapter. Several web stacks were considered: Blazor Server, Blazor WebAssembly, plain HTML + vanilla JS, HTMX with server-rendered partials, and a Vite-built JS SPA.

The Console has non-trivial dynamic forms (the `ModelAliases`, `ReasoningPolicies`, `Tokenizers` config sub-trees are all maps with per-entry shapes) and a high-rate live event stream from the Monitor. Forms benefit from a component model with client-side validation against typed schemas. The live stream benefits from a small, tree-shakeable runtime.

## Decision

The Adapter Console is built with Vite + Preact + TypeScript and embedded in the binary as `EmbeddedResource` files served by `ManifestEmbeddedFileProvider`. The Console mounts at `/_ui/` on the same Kestrel listener that serves `/v1/*` and `/api/admin/*`. The package manager for the UI is `pnpm` (via `corepack`); CI runs `pnpm install --frozen-lockfile && pnpm run build` before `dotnet publish`. MSBuild invokes this from `src/Claude2Foundry/Claude2Foundry.csproj` so `dotnet publish` remains the single shipping command.

UI source lives in `ui/` at the repo root. Build output lands in `src/Claude2Foundry/wwwroot/_ui/` and is referenced as `EmbeddedResource` in the csproj. The Console fetches the JSON Schema for `ProxyConfig` from `GET /api/admin/config/schema` at load and renders the config form dynamically from it.

## Consequences

Positive:
- Single `dotnet publish` produces a single binary, preserving the existing distribution model.
- Preact runtime is ~3 KB minified; total Console bundle target is < 200 KB gzipped. Fast first paint even on cold cache.
- TypeScript catches `ProxyConfig` schema drift at build time on the UI side; server-emitted JSON Schema catches it at request time.
- Vite dev server (`pnpm run dev` on `:5173`, proxying `/api/admin/*` and `/_ui/_stream` to `:8787`) gives sub-200 ms HMR for fast iteration; the adapter's `dotnet watch run` cycle is independent.
- `pnpm` content-addressed store + strict dep resolution catches accidental phantom deps; lockfile-frozen CI installs are fast.

Negative:
- Adds Node + pnpm to the dev toolchain. Pure-.NET contributors must run `corepack enable` once. CI image needs Node.
- Adds a JS dependency surface. We mitigate by pinning lockfile and reviewing dependency updates manually.
- Bundle size needs ongoing attention; adding a heavy chart lib later (e.g. Plotly) blows the budget. Future Metrics view should pick a lightweight charting library (e.g. uPlot, ~30 KB).

## Alternatives considered

**Blazor Server.** Rejected. SignalR is required for live updates, conflicting with the SSE choice already made for the monitor stream (would force two transports). Server holds UI state — restart loses scroll position, expanded rows. Adds significant binary size and runtime memory.

**Blazor WebAssembly.** Rejected. WASM runtime + framework adds ~5–10 MB to the binary; first paint is slow because the runtime downloads on each cold load. Overkill for "form + table + live log".

**Plain HTML + vanilla JS + CDN Tailwind.** Rejected. The config form is dynamic enough that hand-rolling DOM is more work than a component framework saves. Also breaks the "self-contained" promise if Tailwind comes from a CDN.

**HTMX + server-rendered partials.** Rejected. Strong fit for the live stream (HTMX has SSE primitives), weak fit for the multi-page config form with cross-field validation. Picking HTMX would force server-side templating into the adapter, which has been ASP.NET-Minimal-API JSON only so far — that's a bigger architectural shift than adding a JS bundle.

**React (full).** Rejected in favor of Preact. Same JSX, ~15× larger runtime. No SSR, no concurrent rendering needs that would justify React over Preact.
