# 05 — Error mapping

## Goal

Every failure surfaces to Claude Code as an Anthropic-shaped error response Claude Code can parse, render in its UI, and (where appropriate) retry on.

## Wire shape

```json
{
  "type": "error",
  "error": {
    "type": "<anthropic_error_type>",
    "message": "<prefix> <detail>"
  }
}
```

HTTP status on the response matches the Anthropic convention listed below.

## Mapping table

Driven by the upstream HTTP status received from Foundry. Network/connection failures map as if status 503.

| Foundry HTTP | Anthropic `error.type` | Anthropic HTTP status |
|---|---|---|
| 400 | `invalid_request_error` | 400 |
| 401 | `authentication_error` | 401 |
| 403 | `permission_error` | 403 |
| 404 | `not_found_error` | 404 |
| 408 | `api_error` | 500 |
| 413 | `request_too_large` | 413 |
| 422 | `invalid_request_error` | 400 |
| 429 | `rate_limit_error` | 429 |
| 500 | `api_error` | 500 |
| 502 | `api_error` | 500 |
| 503 | `api_error` | 500 |
| 504 | `api_error` | 500 |
| Network/connect/DNS failure | `api_error` | 500 |
| Adapter timeout (outbound) | `api_error` | 500 |

## Message prefix

- Errors originating from Foundry (any HTTP response or connection failure to it): prefix `[Foundry] `. Concatenate Foundry's own `error.message` (or HTTP reason phrase if no body) after the prefix.
- Errors originating inside the adapter (validation, config, translator panic, our own timeout): prefix `[Adapter] `. Provide a short human-readable cause.

Examples:

```
[Foundry] DeploymentNotFound: The API deployment for this resource does not exist.
[Adapter] thinking.budget_tokens must be > 0
[Adapter] Foundry timeout after 600s
```

## Adapter validation errors

Trigger conditions and their `[Adapter]` messages:

- Anthropic request fails JSON deserialization → 400 `invalid_request_error`, message `[Adapter] Malformed request: <System.Text.Json detail>`.
- `messages` is empty or missing → 400 `invalid_request_error`, message `[Adapter] At least one message is required`.
- `max_tokens` missing → 400 `invalid_request_error`, message `[Adapter] max_tokens is required`.
- `tool_choice` references a `name` not in `tools` → 400 `invalid_request_error`, message `[Adapter] tool_choice.name not found in tools`.
- `Foundry returned no choices` (empty `choices` array) → 500 `api_error`.
- Unhandled exception in translator → 500 `api_error`, message `[Adapter] Internal error (<correlation_id>)`. Log the stack trace separately at `Error` level. Never leak stack traces over the wire.

## Streaming errors

See [04 streaming translation](./04-streaming-translation.md). Two modes:

1. Error detected *before* the first SSE event is written: emit a regular HTTP error response per the table above. The response's `Content-Type` is `application/json`, not `text/event-stream`.
2. Error detected *after* `message_start` was written: emit an Anthropic `error` event on the open stream and close. The SSE frame is:
   ```
   event: error
   data: {"type":"error","error":{"type":"<mapped>","message":"<prefix> <detail>"}}

   ```
   Do not emit `message_stop` after an `error` event.

## Idempotency

Errors carry the same `x-c2f-request-id` response header used on successful responses, so operators can correlate the wire failure with the server log.

## Don't do

- Don't include Foundry's raw response body verbatim if it is HTML (some Azure layers return HTML error pages). Detect by `Content-Type` and substitute `[Foundry] <status code> <reason phrase>` instead.
- Don't include the upstream URL, request id, or any header from Foundry in the message — those leak deployment names. Log them at `Error` level instead, with the correlation id.
- Don't retry inside the adapter. Claude Code handles retries on `rate_limit_error` and transient `api_error`. Surface and let the client decide.
