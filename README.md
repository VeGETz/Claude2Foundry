# Claude2Foundry

Anthropic Messages API → Azure AI Foundry (OpenAI Chat Completions) proxy. Claude Code talks to this proxy, this proxy talks to Foundry — Claude Code sees "Opus 4.7" but the actual model is whatever Foundry serves.

## How it works

```
Claude Code                     Claude2Foundry                  Azure Foundry
Anthropic Messages API  ──►  :8787/v1/messages  ──►  OpenAI Chat Completions
     SSE events          ◄──  (translate)          ◄──  (translate)
```

- Accepts Anthropic Messages API requests on `http://127.0.0.1:8787`
- Translates to OpenAI Chat Completions format for Azure Foundry backend
- Streams SSE responses back in Anthropic format
- Maps model names (e.g. `claude-opus-4-7` → `o4-mini`)

## Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/v1/messages` | POST | Chat (streaming & non-streaming) |
| `/v1/messages/count_tokens` | POST | Token counting |
| `/health` | GET | Health check (probes Foundry `/models`) |

## Quick start

```bash
# 1. Set your Foundry API key
export FOUNDRY_API_KEY="your-foundry-api-key"

# 2. Build & run
dotnet run --project src/Claude2Foundry

# 3. Point Claude Code at it
export ANTHROPIC_BASE_URL="http://127.0.0.1:8787"
export ANTHROPIC_API_KEY="anything"  # not used, just has to be set
```

## Configuration

All config lives in `src/Claude2Foundry/appsettings.json` under the `Proxy` section:

```json
{
  "Proxy": {
    "BackendUrl": "https://my-resource.openai.azure.com/openai/v1/",
    "ApiKeyEnv": "FOUNDRY_API_KEY",
    "DefaultModel": "DeepSeek-V3",
    "ModelAliases": {
      "claude-opus-4-7": "o4-mini"
    },
    "ReasoningPolicies": {
      "DeepSeek-V3": "none",
      "o4-mini": "effort"
    },
    "Tokenizers": {
      "DeepSeek-V3": { "Source": "TiktokenCl100k" },
      "o4-mini": { "Source": "TiktokenO200k" }
    },
    "Timeouts": {
      "OutboundTotalSeconds": 600,
      "StreamIdleSeconds": 60
    }
  }
}
```

| Key | Description |
|-----|-------------|
| `BackendUrl` | Azure Foundry OpenAI endpoint (include `/openai/v1/`) |
| `ApiKeyEnv` | Environment variable to read the API key from |
| `DefaultModel` | Fallback model when alias not found |
| `ModelAliases` | Claude model → Foundry model mapping |
| `ReasoningPolicies` | How to handle thinking/reasoning: `none`, `passthrough`, `effort` |
| `Tokenizers` | Which tiktoken registry to use per model |
| `Timeouts` | `OutboundTotalSeconds` (600s) and `StreamIdleSeconds` (60s) |

## Reasoning policies

| Policy | Behavior |
|--------|----------|
| `none` | Strip all thinking blocks (use for models without reasoning) |
| `passthrough` | Forward thinking blocks as-is (use for DeepSeek-R1, etc.) |
| `effort` | Map Anthropic thinking budget to OpenAI `reasoning_effort` |

## Supported features

- Streaming (SSE) and non-streaming chat
- Tool use (function calling)
- Image inputs (base64 and URL)
- Thinking/reasoning passthrough
- Token counting via tiktoken
- Model alias resolution
- Stop reason mapping
- Raw API call logging to stdout (`[C2F → Foundry]` / `[C2F ← Foundry]`)

## Requirements

- .NET 10 SDK
- Azure AI Foundry resource with model deployments
- `FOUNDRY_API_KEY` environment variable set
