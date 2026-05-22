# Claude2Foundry — Specifications

Implementation specs for the Claude2Foundry adapter. Read in order.

## Reading order

1. [00-overview.md](./00-overview.md) — What the adapter is, scope, non-goals, success criteria.
2. [01-protocol-schemas.md](./01-protocol-schemas.md) — Anthropic Messages and OpenAI Chat Completions data models. C# record definitions to port from vLLM Pydantic models.
3. [02-request-translation.md](./02-request-translation.md) — Anthropic request → OpenAI Chat Completions request. Field-by-field mapping rules.
4. [03-response-translation.md](./03-response-translation.md) — Non-streaming OpenAI response → Anthropic response. Stop reason map, content block construction.
5. [04-streaming-translation.md](./04-streaming-translation.md) — OpenAI SSE chunks → Anthropic SSE event stream. State machine, block lifecycle, tool call deltas.
6. [05-error-mapping.md](./05-error-mapping.md) — HTTP status and exception → Anthropic-shaped error response. Prefix rules.
7. [06-config.md](./06-config.md) — `appsettings.json` schema, env vars, defaults, validation.
8. [07-endpoints-and-hosting.md](./07-endpoints-and-hosting.md) — REST routes, Kestrel settings, listen address, packaging.
9. [08-token-counting.md](./08-token-counting.md) — `count_tokens` endpoint implementation, tokenizer selection, image cost.
10. [09-logging.md](./09-logging.md) — Logger configuration, levels, correlation id, sampled warnings.
11. [10-build-and-deploy.md](./10-build-and-deploy.md) — Solution layout, NuGet refs, publish profile, README contents for end users.

## Authoritative sources

- Glossary and decision context: [`/CONTEXT.md`](../../CONTEXT.md)
- Hard-to-reverse decisions: [`/docs/adr/`](../adr/)
- Reference implementation to port from: `/vllm/vllm/entrypoints/anthropic/` in this repo (cloned vLLM source — engine code is out of scope).

## What is NOT in scope

The adapter does not host models, does not stream from a local engine, does not implement any inference. It only translates request and response shapes between two HTTP APIs. Anything in `vllm/vllm/entrypoints/anthropic/serving.py` that calls `self.create_chat_completion` or `self.render_chat_request` is engine glue and must be replaced with an HTTP call to Foundry — see [02](./02-request-translation.md), [03](./03-response-translation.md), [04](./04-streaming-translation.md).
