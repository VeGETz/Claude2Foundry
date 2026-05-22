# 09 — Logging

## Logger

`Microsoft.Extensions.Logging.ILogger`. ASP.NET Core's default console provider. No Serilog, no Seq, no file sink, no third-party logger.

Output goes to stdout (Information, Debug, Trace) and stderr (Warning, Error, Critical) per the default formatter. Operators redirect with shell redirection if they want files.

## Levels

| Level | What |
|---|---|
| `Trace` | Off by default. Reserved for future deep diagnostics; do not emit anything at this level in v1. |
| `Debug` | Off by default. Full Anthropic request body, full translated OpenAI body, full upstream response, full translated Anthropic response. One log entry per body. Mask the Foundry `api-key` header if it ever appears in a dump. |
| `Information` | One line per inbound request: `req=<correlation_id> model=<original>->{<resolved>} status=<http> elapsed=<ms>ms stream=<true|false>`. Plus startup banner (resolved config summary, listen URL, alias count). Plus client-disconnect events. |
| `Warning` | Sampled events for Anthropic-only fields stripped from requests. Sample policy: emit at most one log per distinct stripped field per process lifetime. Fields: `cache_control`, `top_k`, `service_tier`, oversize `stop_sequences` (>4 → truncated). Include a count of suppressed duplicates if convenient. |
| `Error` | Foundry upstream HTTP errors (with the correlation id, target, status code, and the unmasked upstream body, snipped to 2 KiB if longer). Adapter validation errors that returned 4xx are logged at `Information`, not `Error` — those are user errors, not adapter failures. Unhandled exceptions in the pipeline log at `Error` with stack trace. |
| `Critical` | Startup failures only — bad config, port bind failure, tokenizer file missing. Process exits non-zero after the log line. |

## Correlation id

Generated per inbound request at the very top of the pipeline. Format: 8-byte hex (16 chars). Cheap, low-collision for human pasting.

- Attached to every `ILogger.BeginScope` for that request — the default console formatter prints the scope on each line.
- Set on `HttpContext.TraceIdentifier`.
- Returned to the client in response header `x-c2f-request-id` on both success and error responses.

## What NOT to log

- Foundry `api-key` value, anywhere.
- Claude Code's `x-api-key` value (it's dropped by the adapter — don't log it either).
- Full URL of the Foundry resource at `Information` level (logs go to stdout, operators may share). The startup banner may include it; per-request lines do not.
- Tokenizer file contents.
- The raw `tool_result` payload at `Information` (potentially big; OK at `Debug`).

## Sampling helper

The sampled warnings need a tiny in-memory `ConcurrentDictionary<string, bool>` keyed by warning key (`"strip:cache_control"`, etc.). `TryAdd` returns true on first occurrence → log; false → silently increment a counter and move on. Counters can be reported in a final shutdown summary log line.

## Example startup banner

```
info: Claude2Foundry.Startup
      Listening on http://127.0.0.1:8787
      Backend: https://my-resource.openai.azure.com/openai/v1/ (api-key from env FOUNDRY_API_KEY: present)
      Default model: DeepSeek-V3
      Aliases: 4 entries
      Reasoning policies: 4 entries (none=2, passthrough=1, effort=1)
      Tokenizers: 4 entries (TiktokenCl100k=1, TiktokenO200k=2, HuggingFace=1)
      Timeouts: outbound=600s stream-idle=60s
```

## Example per-request line

```
info: Claude2Foundry.Pipeline[0]
      => req=a1b2c3d4e5f60718 POST /v1/messages model=claude-opus-4-7->{o4-mini} stream=true
info: Claude2Foundry.Pipeline[0]
      <= req=a1b2c3d4e5f60718 status=200 elapsed=4823ms in=1247 out=312
```
