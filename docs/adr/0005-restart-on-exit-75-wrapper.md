# 5. Hot-reload operational fields; restart-on-exit-75 wrapper for bootstrap fields

Date: 2026-05-23

## Status

Accepted

## Context

The Adapter Console allows editing every `Proxy.*` field. Some fields are safe to change while the process runs (operational fields: `ModelAliases`, `ReasoningPolicies`, `Tokenizers`, `Timeouts`, `DefaultModel`). Two are not: `Proxy.BackendUrl` (drives `HttpClient` URL + connection pool) and `Proxy.ApiKeyEnv` (read at startup; changing the name without restart leaves the old env value live). These are the **bootstrap fields**.

In-process rebind of bootstrap fields was considered and rejected: rebuilding `HttpClient` while requests are in flight invites pool churn and stale-reference bugs. Re-reading env on `ApiKeyEnv` rename is fine on its own but offers no in-process value without UI affordance to also rewrite the env var (which is out of scope).

So the bootstrap fields require a process restart. The adapter is a foreground process — no daemon, no systemd, no Docker `--restart`. We need a restart mechanism that works cross-platform from inside the running process.

`Process.Start(Environment.ProcessPath)` + `Environment.Exit(0)` was tried mentally and rejected: on Windows console hosts, the spawned child either inherits the parent's console (race condition) or detaches into a new window (visible UX break). On WSL/Linux/macOS terminals it mostly works but is fragile under specific shells (e.g. interactive zsh with auto-restart).

## Decision

**Operational fields hot-reload.** `IOptionsMonitor<ProxyConfig>` watches `appsettings.local.json` (and base) with `reloadOnChange: true`. Every request handler captures an `IOptionsSnapshot<ProxyConfig>` at request entry and threads that snapshot down the call stack to `RequestTranslator` / `ResponseTranslator` / `StreamTranslator`. In-flight requests therefore **drain** on the config they entered with; new requests use the new config. The handler stack never re-resolves config mid-request.

**Bootstrap fields trigger an exit-75 restart.** When the Console saves a config that changes `BackendUrl` or `ApiKeyEnv`, the response body includes `{ "restartRequired": true, "fields": ["Proxy.BackendUrl"] }`. The Console shows a banner with a "Restart adapter" button. Clicking it `POST`s `/api/admin/restart`, which logs the intent, gracefully cancels in-flight requests with a 60-second grace window, then calls `Environment.Exit(75)`.

The adapter is shipped with two thin wrapper scripts in `publish/`:

- `c2f.sh` — POSIX shell: `while c2f-bin "$@"; do :; done; ec=$?; [ $ec -eq 75 ] && exec "$0" "$@"; exit $ec`
- `c2f.cmd` — Windows batch equivalent

Both scripts export `C2F_WRAPPER=1` before exec. The adapter reads this env var at startup. If set, the Console exposes the Restart button. If unset, the Console hides it and shows a toast: `"Restart required. Re-run the adapter manually, or use the c2f wrapper script."`

## Consequences

Positive:
- The hot-reload path covers the 95% case (alias edits, policy tweaks, timeout changes) with zero downtime and zero in-flight disruption.
- The restart path is portable: a 10-line wrapper script works on every supported OS. No platform-specific spawn logic in C#.
- The wrapper is opt-in: users who run the binary directly get the existing behavior unchanged, with an honest toast explaining the situation.
- Exit code 75 is a documented `EX_TEMPFAIL` convention (sysexits.h); reuse signals intent to anyone reading process exit codes.

Negative:
- The "single-file binary" claim is now "single binary + tiny wrapper if you want one-click restart". The repo ships both; release archives bundle both. README and packaging docs need to explain when the wrapper is needed.
- Wrapper script absence + frequent bootstrap-field edits = friction. Operators who never touch `BackendUrl` or `ApiKeyEnv` won't notice; operators who switch Foundry resources often should use the wrapper.
- The 60-second drain window during restart is a hard cap; long-running streams in flight at restart time will be cut. Acceptable: an explicit restart is a user action.

## Alternatives considered

**In-process rebind (hot-swap `HttpClient`).** Rejected. `IHttpClientFactory` + `SocketsHttpHandler` lifetime mechanics make safe mid-flight handler swaps non-trivial; bugs in this area are hard to reproduce. We chose the simpler boundary.

**`Process.Start(Environment.ProcessPath) + Exit(0)`.** Rejected. Cross-platform console-inheritance behavior is inconsistent. On Windows the child either races on the console or detaches; the latter breaks the "foreground process in the same terminal" promise.

**Systemd / Docker restart policy.** Rejected for v1. The adapter is a developer tool launched from a terminal; daemon/service hosting is explicitly out of scope per the existing CONTEXT.md.

**No restart button at all (Q14 option D).** Held as the documented fallback when the wrapper is absent. Not the default UX.
