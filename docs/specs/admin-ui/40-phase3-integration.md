# 40 — Phase 3: Integration + polish (serial)

## Mode

Serial. One coding agent. Runs after **both** phase 1 (backend) and phase 2 (frontend) are merged to a shared integration branch.

This phase wires the two halves together, ships the wrapper scripts, updates packaging and docs, and adds end-to-end smoke tests.

## Owned files (write)

- `src/Claude2Foundry/Claude2Foundry.csproj` — add the MSBuild target that invokes `pnpm install` + `pnpm run build` before `Build`; verify `<EmbeddedResource Include="wwwroot\_ui\dist\**\*" />`. Add `<DefineConstants>UI_EMBEDDED</DefineConstants>` for release builds.
- `src/Claude2Foundry/Program.cs` — replace the phase 0 `_ui` 404 placeholder with `MapStaticAssets()` / `ManifestEmbeddedFileProvider` wiring at `/_ui/`. Serve `index.html` for any unmatched `/_ui/{**path}` request (SPA fallback).
- `scripts/c2f.sh` (new) — POSIX wrapper script (exit-75 loop). Lives in `scripts/`, **not** `publish/`, because `publish/` is gitignored and the wrapper is a shipped artifact (see ADR-0005).
- `scripts/c2f.cmd` (new) — Windows batch wrapper script (exit-75 loop).
- `scripts/README.md` (new) — explains the wrapper, exit code, env var. Release packaging copies these three files into the `publish/` archive alongside the binary.
- `README.md` (root) — add a "Adapter Console" section, screenshot or ASCII diagram, link to spec README.
- `tests/Claude2Foundry.Tests/Integration/AdminConsoleE2E.cs` (new) — xUnit integration tests using `WebApplicationFactory<Program>`:
  - Smoke: every admin route returns 200 against a default config.
  - Save round-trip: POST config → IOptionsMonitor fires → next request uses new alias.
  - SSE: in-process subscribe + simulated request → events arrive in order.
  - Restart: with `C2F_WRAPPER=1` mock, restart triggers exit(75) (caught via DI seam — production exit replaced with a recorded sentinel in test).
- `tests/Claude2Foundry.Tests/Integration/EmbeddedResourcesTest.cs` (new) — assert that `Microsoft.Extensions.FileProviders.ManifestEmbeddedFileProvider` for the running assembly contains `_ui/dist/index.html` and at least one `_ui/dist/assets/*.js`.
- `.github/workflows/*.yml` (if CI exists) — add Node 20 + corepack setup; add `pnpm install` + `pnpm run build` step before the existing `dotnet build`. If no workflow exists yet, this phase does not create one — it's documented in `publish/README.md` as a "if you add CI later, here's what to do" note.
- `docs/specs/admin-ui/40-phase3-integration.md` — this file.
- `docs/specs/admin-ui/README.md` — update the "Reading order" if any spec links rotted during phases 1/2 (none expected).

## Consumed contracts (read-only)

- All phase 0/1/2 outputs.
- The published `ui/dist/` directory from phase 2.

## Tasks

### 1. Embed UI into the binary

1.1 In `Claude2Foundry.csproj`:

```xml
<Target Name="BuildUI" BeforeTargets="BeforeBuild">
  <Exec Command="corepack enable pnpm" WorkingDirectory="../../ui" />
  <Exec Command="pnpm install --frozen-lockfile" WorkingDirectory="../../ui" />
  <Exec Command="pnpm run build" WorkingDirectory="../../ui" />
</Target>
<ItemGroup>
  <EmbeddedResource Include="wwwroot\_ui\dist\**\*" />
</ItemGroup>
```

Verify the resource manifest contains the expected files via `EmbeddedResourcesTest`.

1.2 In `Program.cs`:

```csharp
var uiProvider = new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot/_ui/dist");
app.MapGet("/_ui", () => Results.Redirect("/_ui/"));
app.MapGet("/_ui/{**path}", async (HttpContext ctx, string? path) =>
{
    var requested = string.IsNullOrEmpty(path) ? "index.html" : path;
    var file = uiProvider.GetFileInfo(requested);
    if (!file.Exists) file = uiProvider.GetFileInfo("index.html"); // SPA fallback
    ctx.Response.ContentType = MimeMap.Resolve(file.Name);
    await using var stream = file.CreateReadStream();
    await stream.CopyToAsync(ctx.Response.Body);
});
```

(Pseudocode — the actual implementation may differ; the contract is: any GET under `/_ui/` returns the embedded asset, with SPA fallback to `index.html`.)

1.3 Verify the single-file publish output (`dotnet publish -c Release -r linux-x64 --self-contained true /p:PublishSingleFile=true`) runs from any directory and serves `/_ui/` correctly.

### 2. Wrapper scripts

2.1 `scripts/c2f.sh`:

```sh
#!/usr/bin/env sh
export C2F_WRAPPER=1
BIN="${C2F_BIN:-./Claude2Foundry}"
while :; do
  "$BIN" "$@"
  ec=$?
  if [ "$ec" -ne 75 ]; then
    exit "$ec"
  fi
  echo "[c2f] adapter exited 75 (restart requested); relaunching..." >&2
done
```

2.2 `scripts/c2f.cmd`:

```cmd
@echo off
set C2F_WRAPPER=1
set BIN=%C2F_BIN%
if "%BIN%"=="" set BIN=Claude2Foundry.exe
:loop
"%BIN%" %*
if errorlevel 75 if not errorlevel 76 (
  echo [c2f] adapter exited 75 ^(restart requested^); relaunching...
  goto loop
)
exit /b %ERRORLEVEL%
```

2.3 `scripts/README.md` explains: when to use the wrapper (if you want one-click restart from the Console UI), when you don't need it (you'll restart by hand), and how to point it at a binary in another path (`C2F_BIN=/opt/c2f ./c2f.sh`).

2.4 Release packaging (`dotnet publish` post-step or release CI) copies `scripts/c2f.sh`, `scripts/c2f.cmd`, `scripts/README.md` into the publish output dir alongside the binary so downstream users get the wrapper without having to find it in the source tree.

### 3. E2E tests

3.1 `AdminConsoleE2E` uses `WebApplicationFactory<Program>` with a fake Foundry HTTP handler (already used by existing tests via `FoundryClient`'s injectable handler).

3.2 Test list:
- **Routes wired**: every admin route returns a non-500 status against a baseline config.
- **Save round-trip**: POST a new alias map, then call `/v1/messages` with a Claude model id that now resolves to a new Foundry name; assert the fake Foundry handler saw the new name.
- **SSE replay**: subscribe a test SSE client to `/api/admin/events`, fire a synthetic request through the pipeline, assert events arrive in order.
- **Hot-reload drain**: simulate a 5-second streaming request; mid-stream, save a config change; assert the stream finishes on the old config and the next request uses the new one.
- **CSRF**: POST without header → 400; with → 200.
- **Restart sentinel**: `C2F_WRAPPER=1` env in the test process; `IExitSink` (DI-injected, replaces `Environment.Exit` in tests) records exit code 75 was requested.
- **Embedded assets**: assert `ManifestEmbeddedFileProvider` returns a non-empty `index.html`.

### 4. Docs

4.1 Root `README.md` gets a new section after "Quick start":

```
## Adapter Console

A local web UI for monitoring and config lives at http://127.0.0.1:8787/_ui/.
- Monitor every request transiting the adapter in real time.
- Edit config without touching JSON files; saves to `<datadir>/appsettings.local.json`.
- Optionally use the `c2f.sh` / `c2f.cmd` wrapper for one-click restart from the UI.

See [docs/specs/admin-ui/](docs/specs/admin-ui/) for the full spec.
```

4.2 Update `docs/specs/README.md` to add the admin-ui spec tree link if not already linked.

4.3 If a CHANGELOG exists, add an entry. (Repo currently has none; skip if absent.)

### 5. Polish + hygiene

5.1 Verify `pnpm typecheck` passes in CI.

5.2 Verify the published binary works on a fresh machine:
- `mkdir /tmp/test-c2f && cp publish/Claude2Foundry /tmp/test-c2f/ && cd /tmp/test-c2f && FOUNDRY_API_KEY=fake ./Claude2Foundry` starts and serves `/_ui/`.

5.3 Sanity check: every link in `docs/specs/admin-ui/*.md` resolves to a real file.

5.4 Sanity check: every glossary term referenced in the spec docs is defined in `/CONTEXT.md`.

## Acceptance criteria

1. `dotnet publish -c Release -r linux-x64 --self-contained true /p:PublishSingleFile=true` produces a single binary. Running it serves `/_ui/` correctly and the UI loads without any external file.
2. All e2e tests pass.
3. The wrapper script restarts the adapter end-to-end: launch via `./c2f.sh`, change `BackendUrl` in UI, click Restart, adapter relaunches within 60 s, UI reconnects automatically.
4. README mentions the Console with a one-line summary + link to specs.
5. No regression in existing `/v1/*` tests.

## Test matrix

| Test | Pass criterion |
|---|---|
| `pnpm run build` | exit 0; `ui/dist/index.html` exists; total bundle < 200 KB gzipped |
| `dotnet build` | exit 0; warns 0 |
| `dotnet test` | all green including new e2e |
| Single-file publish | binary runs from arbitrary cwd, serves `/_ui/` |
| Wrapper restart loop | exit code 75 → relaunch; any other exit → loop ends |
| Wrapper env propagation | `C2F_WRAPPER=1` visible inside the adapter |
| CI (if present) | new pnpm step succeeds before dotnet step |

## Risks / open Qs

- **`ManifestEmbeddedFileProvider` + single-file publish.** Known to work in .NET 6+; verify in .NET 10. If broken, fall back to embedding raw `byte[]` resources and a small static-file handler.
- **MIME type table.** Need a small lookup (html, js, css, svg, woff2, ico, json). Implementation may use `Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider`.
- **Windows console for wrapper.** `exit /b` semantics with `errorlevel` are correct in the script above, but Windows interactive `cmd` users may see the relaunch text scroll. Acceptable; document.
- **Future CI.** This phase does not create a CI workflow. If one is added later, the Node + pnpm install step is the only addition.
