# Claude2Foundry

Anthropic Messages API → Azure AI Foundry (OpenAI Chat Completions) proxy. Claude Code talks to this proxy, this proxy talks to Foundry — Claude Code sees "Opus 4.7" but the actual model is whatever Foundry serves.

## Proof it works

This README and the entire codebase were written by **DeepSeek-V4-Pro** running behind the proxy, exposed to Claude Code as `claude-opus-4-7`. If you're reading this, the proxy is doing its job.

```
Claude Code (thinks it's Opus 4.7)
    │  Anthropic Messages API
    ▼
Claude2Foundry (:8787)   ← translates Anthropic ↔ OpenAI
    │  OpenAI Chat Completions
    ▼
Azure Foundry (DeepSeek-V4-Pro, GPT-5.4-mini, etc.)
```

## How it works

```
Claude Code                     Claude2Foundry                  Azure Foundry
Anthropic Messages API  ──►  :8787/v1/messages  ──►  OpenAI Chat Completions
     SSE events          ◄──  (translate)          ◄──  (translate)
```

- Accepts Anthropic Messages API requests on `http://127.0.0.1:8787`
- Translates to OpenAI Chat Completions format for Azure Foundry backend
- Streams SSE responses back in Anthropic format
- Maps model names (e.g. `claude-opus-4-7` → `GPT-5.4-mini`)

## Quick start

```bash
# 1. Set your Foundry API key
export FOUNDRY_API_KEY="your-foundry-api-key"

# 2. Build & run
dotnet run --project src/Claude2Foundry

# 3. Point Claude Code at it
export ANTHROPIC_BASE_URL="http://127.0.0.1:8787"
export ANTHROPIC_API_KEY="anything"  # not used by proxy, just has to be set
```

## Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/v1/messages` | POST | Chat (streaming & non-streaming) |
| `/v1/messages/count_tokens` | POST | Token counting |
| `/health` | GET | Health check (probes Foundry `/models`) |

## Configuration

All config lives in `src/Claude2Foundry/appsettings.json` under the `Proxy` section. Example:

```json
{
  "Proxy": {
    "BackendUrl": "https://my-resource.openai.azure.com/openai/v1/",
    "ApiKeyEnv": "FOUNDRY_API_KEY",
    "DefaultModel": "DeepSeek-V4-Pro",
    "ModelAliases": {
      "claude-opus-4-7": "GPT-5.4-mini",
      "claude-sonnet-4-6": "DeepSeek-V4-Pro",
      "claude-haiku-4-5": "DeepSeek-V4-Pro"
    },
    "ReasoningPolicies": {
      "DeepSeek-V4-Pro": "none",
      "DeepSeek-R1": "passthrough",
      "GPT-5.4-mini": "effort"
    },
    "Tokenizers": {
      "DeepSeek-V4-Pro": { "Source": "TiktokenCl100k" },
      "GPT-5.4-mini": { "Source": "TiktokenO200k" }
    },
    "Timeouts": {
      "OutboundTotalSeconds": 600,
      "StreamIdleSeconds": 60
    }
  }
}
```

### `BackendUrl`

The Azure Foundry OpenAI endpoint URL. Must include the full path to `/openai/v1/` (trailing slash recommended). The proxy appends `chat/completions` to this URL for every request.

```
https://<your-resource>.openai.azure.com/openai/v1/
```

### `ApiKeyEnv`

Name of the environment variable holding your Foundry API key. Set it before starting the proxy:

```bash
export FOUNDRY_API_KEY="abc123..."
```

The proxy reads this at startup. If the variable is missing or empty, the proxy won't start.

### `DefaultModel`

The Foundry model deployment name to use when no alias matches. If Claude Code sends `model: "claude-opus-4-7"` and there's no entry in `ModelAliases` for it, the proxy falls back to this model. Set this to your most capable or cheapest deployment.

### `ModelAliases`

Maps Anthropic model names (the ones Claude Code sends) to Foundry deployment names. This is how you trick Claude Code — it requests `claude-opus-4-7`, the proxy translates that to `GPT-5.4-mini` or whatever you configure.

```json
"ModelAliases": {
  "claude-opus-4-7": "GPT-5.4-mini",
  "claude-sonnet-4-6": "DeepSeek-V4-Pro",
  "claude-haiku-4-5": "DeepSeek-V4-Pro",
  "claude-3-5-haiku": "DeepSeek-V4-Pro"
}
```

Keys are what Claude Code asks for, values are your Foundry deployment names. You can map multiple Claude models to the same Foundry deployment.

### `ReasoningPolicies`

Controls how the proxy handles thinking/reasoning blocks when translating between Anthropic and OpenAI formats. Different models expose reasoning differently.

| Policy | Behavior |
|--------|----------|
| `none` | Strip all thinking blocks entirely. Use for models that don't support reasoning (e.g. DeepSeek-V4-Pro, GPT-4.1). The model still sees the conversation, but thinking content is removed from both request and response. |
| `passthrough` | Forward thinking blocks as-is in both directions. Use for models that natively produce reasoning tokens as part of their output (e.g. DeepSeek-R1). The thinking text appears inline in the message content. |
| `effort` | Map Anthropic's `thinking.budget_tokens` to OpenAI's `reasoning_effort` parameter (`low`/`medium`/`high`). Use for models that support the OpenAI reasoning API (e.g. GPT-5.4-mini, o3-mini). Mapping: budget < 2000 → `low`, 2000–8000 → `medium`, > 8000 → `high`. |

**How to pick:**

- **Standard chat model** (GPT-4, DeepSeek-V4-Pro, etc.) → `none`
- **DeepSeek-R1-style reasoning model** that outputs thinking inline → `passthrough`
- **OpenAI reasoning model** (GPT-5.4-mini, o3-mini) that uses `reasoning_effort` → `effort`

### `Tokenizers`

The proxy exposes Anthropic's `/v1/messages/count_tokens` endpoint. To count tokens, it needs a tokenizer that matches your model. The `Source` field tells the proxy which tokenizer to load.

| Source | What it loads | Use for |
|--------|---------------|---------|
| `TiktokenCl100k` | GPT-4 tiktoken (cl100k_base) | DeepSeek-V4-Pro, GPT-4, GPT-3.5 |
| `TiktokenO200k` | GPT-4o tiktoken (o200k_base) | GPT-5.4-mini, o3-mini, GPT-4.1 |
| `HuggingFace` | SentencePiece/Llama `.model` file | Custom or local models |

For `HuggingFace`, also specify a `Path` pointing to the `.model` file:

```json
"Tokenizers": {
  "my-llama-model": {
    "Source": "HuggingFace",
    "Path": "/path/to/tokenizer.model"
  }
}
```

**How to pick:**

- If your model is a **DeepSeek variant** → `TiktokenCl100k` (DeepSeek uses cl100k_base tokenizer)
- If your model is **GPT-4o / o-series / GPT-4.1** → `TiktokenO200k`
- If you're using a **Llama-based or custom model** with a `.model` file → `HuggingFace` with path
- If you don't use `count_tokens`, the tokenizer doesn't matter — pick anything
- Token counts are approximate for non-OpenAI models. The proxy uses these for display/metadata only; actual billing is per-token from your Foundry resource.

### `Timeouts`

| Key | Default | Description |
|-----|---------|-------------|
| `OutboundTotalSeconds` | 600 | Hard timeout for the entire HTTP call to Foundry. After this, the request is aborted. 10 minutes is generous enough for long reasoning sessions. |
| `StreamIdleSeconds` | 60 | Per-chunk idle timeout during SSE streaming. If no chunk arrives within this window, the stream is torn down. Prevents hanging connections if Foundry drops mid-stream. |

## Supported features

- Streaming (SSE) and non-streaming chat
- Tool use / function calling
- Image inputs (base64 and URL)
- Thinking/reasoning — strip, passthrough, or `reasoning_effort` mapping
- Token counting via tiktoken or HuggingFace tokenizers
- Model alias resolution
- Stop reason mapping (OpenAI finish reasons → Anthropic stop reasons)
- Raw API call logging to stdout (`[C2F → Foundry]` / `[C2F ← Foundry]`)
- Health check endpoint

## Requirements

- .NET 10 SDK
- Azure AI Foundry resource with model deployments
- `FOUNDRY_API_KEY` environment variable set

## Disclaimer

> **This entire project — every line of code, documentation, and configuration — was generated by AI (DeepSeek-V4-Pro, routed through Claude2Foundry itself).**
>
> No human wrote a single line. The proxy was built by the very model it's designed to front.
>
> Review the code carefully before running it in production. AI-generated code may contain subtle bugs, security issues, or design flaws. Use at your own risk.
