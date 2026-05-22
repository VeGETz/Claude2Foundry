# Claude2Foundry — Context

## Purpose

Protocol adapter. Accepts Anthropic Messages API requests (from Claude Code CLI) and forwards to Microsoft Foundry, which exposes an OpenAI-compatible Chat Completions API. Foundry is the sole target backend — the adapter is not a generic multi-backend proxy.

## Operating model

The adapter is a foreground process the developer launches alongside Claude Code, listening by default on `127.0.0.1:8787` over plain HTTP. Distributed as a single-file self-contained .NET 10 binary per OS. The Foundry API key lives in an env var read by the adapter; Claude Code's own `ANTHROPIC_API_KEY` is required by Claude Code itself but ignored by the adapter. TLS, daemon/service hosting, and LAN-binding are out of scope for v1 — Kestrel's standard ASP.NET Core options remain available for operators who need them.

## Logging

Logs to stdout via `ILogger` with the default ASP.NET Core console formatter — no file sink, no third-party logger. Every inbound request gets a short correlation id, included in every log line for that request and returned in the response header `x-c2f-request-id`. Level policy: `Information` for one-line per-request summaries (`originalModel → resolvedModel`, status, elapsed), `Warning` for stripped Anthropic-only fields (sampled, not per-request), `Error` for upstream Foundry HTTP failures and adapter validation failures, `Debug` (off by default) for full request and response bodies.

## Tool use

The Anthropic→OpenAI tool translation is lifted from vLLM's reference implementation: `tool_use` ↔ `tool_calls`, `tool_result` ↔ `role: "tool"`, `tool_choice: {type: "any"}` → `tool_choice: "required"`, parallel tool calls via OpenAI's streaming `index`, `input_json_delta` for partial JSON arguments. Adapter-specific addition: `disable_parallel_tool_use: true` maps to `parallel_tool_calls: false`. Known limitations documented in the README: (1) some non-OpenAI Foundry targets reject `tool_choice: "required"` — the adapter does not pre-empt; the upstream error is surfaced with `[Foundry]` prefix and the operator picks a different target. (2) Anthropic allows images inside `tool_result.content`; OpenAI tool messages are text-only, so the adapter emits the tool message and follows with a separate user message carrying the image — message order changes, which some models tolerate better than others.

## Token counting

`POST /v1/messages/count_tokens` is implemented locally — never proxied. Tokenizer is selected per Foundry target via a `Tokenizers` config map. Built-in sources: `TiktokenCl100k`, `TiktokenO200k`. External source: `HuggingFace` with a `tokenizer.json` file path. Unknown targets fall back to `TiktokenCl100k`. Images in the input count as 1500 tokens each. Counts are approximate and documented as not billing-accurate.

## Timeouts

Outbound to Foundry: 10-minute total deadline for non-streaming requests. Streaming requests have no total deadline but enforce a 60-second per-chunk idle timeout — no SSE chunk for 60s kills the connection. Inbound Kestrel: `KeepAliveTimeout` 10 minutes, `RequestHeadersTimeout` 30 seconds. Outbound timeouts surface to the client as Anthropic `api_error` with `[Adapter]` prefix.

## Glossary

- **Adapter** — this program. Rewrites request/response shape between two API contracts. Not a proxy (which implies pass-through), not a translator (too generic).
- **Foundry** — Azure AI Foundry (cloud). Sole upstream target. Hosts models (DeepSeek, Kimi, Qwen, GPT, etc.) behind an OpenAI-compatible endpoint. Foundry Local is explicitly out of scope.
- **Claude Code** — Anthropic's official CLI. Downstream client. Speaks Anthropic Messages API.
- **Anthropic Messages API** — downstream protocol. `POST /v1/messages`, `POST /v1/messages/count_tokens`.
- **OpenAI Chat Completions API** — upstream protocol the adapter calls on Foundry. `POST /v1/chat/completions`. Chosen over the Responses API because it (a) is stateless and matches Anthropic Messages 1:1, and (b) is supported by the full Foundry model catalog including DeepSeek, Kimi, Qwen — Responses API on Foundry is GPT/o-series only.
- **Model alias** — mapping from a Claude model identifier (e.g. `claude-sonnet-4-5`) to a Foundry model identifier (e.g. `DeepSeek-V3`) sent in the OpenAI request body. Configured in `appsettings.json` as a flat string→string map. Unknown Claude model identifiers fall back to a configured `DefaultModel`. Every request logs `originalModel → resolvedModel` via `ILogger`.
- **Foundry endpoint** — single unified URL: `https://<resource>.openai.azure.com/openai/v1/chat/completions`. Authenticated with `api-key` header from env var. No `api-version` query param, no per-deployment URL path.
- **Reasoning policy** — declares how the adapter handles Anthropic `thinking` blocks for a given Foundry target. Three values: `none` (strip request, ignore response reasoning), `passthrough` (strip request, wrap response `reasoning_content` as Anthropic `thinking` blocks), `effort` (translate `thinking.budget_tokens` to OpenAI `reasoning_effort` low/medium/high, wrap response reasoning). Configured in a `ReasoningPolicies` map keyed by Foundry target name. Unknown target defaults to `none`.
- **Error origin prefix** — when surfacing errors to Claude Code as Anthropic-shaped error responses, the `error.message` field is prefixed with `[Foundry]` for upstream errors and `[Adapter]` for errors raised by the adapter itself. Lets a Claude Code user tell at a glance whether a failure came from Foundry, the adapter, or something in between.
