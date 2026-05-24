# Claude2Foundry

Anthropic Messages API → Azure AI Foundry (OpenAI Chat Completions) proxy. Claude Code talks to this proxy, this proxy talks to Foundry — Claude Code sees "Opus 4.7" but the actual model is whatever Foundry serves.

## How it works

```
Claude Code (thinks it's Opus 4.7)
    │  Anthropic Messages API
    ▼
Claude2Foundry (:8787)   ← translates Anthropic ↔ OpenAI
    │  OpenAI Chat Completions
    ▼
Azure Foundry (DeepSeek-V4-Pro, GPT-5.4-mini, etc.)
```

- Accepts Anthropic Messages API on `http://127.0.0.1:8787`
- Translates to OpenAI Chat Completions for Azure Foundry backend
- Streams SSE responses back in Anthropic format
- Maps model names (`claude-opus-4-7` → `DeepSeek-V4-Pro`, etc.)

## Proof it works

This README was written by **DeepSeek-V4-Pro** running behind the proxy, exposed to Claude Code as `claude-opus-4-7`. The proxy is doing its job — Claude Code requested Opus, got DeepSeek, never knew the difference.

The rest of the codebase (99%+ of all code) was written by **Claude Opus 4.7 and Sonnet 4.6**, also running through the proxy.

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

## Adapter Console

A local web UI lives at `http://127.0.0.1:8787/_ui/`.

- **Monitor** — every request captured to JSONL, replayed on connect, live-tailed via SSE; click any row to inspect request/response bodies
- **Config** — edit all proxy settings without touching JSON; changes saved to `<datadir>/appsettings.local.json`
- **Health** — Foundry reachability, uptime, log file path
- **Test** — send ad-hoc requests from the browser

Use `c2f.sh` / `c2f.cmd` wrapper for one-click restart from the UI.

## Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/v1/messages` | POST | Chat (streaming & non-streaming) |
| `/v1/messages/count_tokens` | POST | Token counting |
| `/api/admin/health` | GET | Health, config, log file info |
| `/api/admin/monitor/list` | GET | Recent request list |
| `/api/admin/monitor/{id}` | GET | Full request/response bodies |
| `/api/admin/monitor/events` | GET | SSE live tail |

## Configuration

All config in `src/Claude2Foundry/appsettings.json` under `Proxy`. Example:

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
    },
    "Monitor": {
      "Enabled": true,
      "MaxBodyBytes": 10485760
    }
  }
}
```

### `BackendUrl`

Azure Foundry OpenAI endpoint. Include full path to `/openai/v1/` (trailing slash). The proxy appends `chat/completions` for every request.

### `ApiKeyEnv`

Name of the env var holding your Foundry API key. Set before starting:

```bash
export FOUNDRY_API_KEY="abc123..."
```

### `DefaultModel`

Foundry deployment to use when no alias matches. Fallback for any `model` the proxy doesn't have an alias for.

### `ModelAliases`

Maps Anthropic model names (what Claude Code sends) to Foundry deployment names.

```json
"ModelAliases": {
  "claude-opus-4-7": "GPT-5.4-mini",
  "claude-sonnet-4-6": "DeepSeek-V4-Pro"
}
```

### `ReasoningPolicies`

Controls thinking/reasoning block handling per deployment.

| Policy | Behavior |
|--------|----------|
| `none` | Strip all thinking blocks. Use for standard chat models. |
| `passthrough` | Forward thinking blocks as-is (e.g. DeepSeek-R1 inline reasoning). |
| `effort` | Map `thinking.budget_tokens` → `reasoning_effort` (`low`/`medium`/`high`). Use for OpenAI o-series. |

### `Tokenizers`

Used for `/v1/messages/count_tokens`.

| Source | Use for |
|--------|---------|
| `TiktokenCl100k` | DeepSeek variants, GPT-4, GPT-3.5 |
| `TiktokenO200k` | GPT-4o, o-series, GPT-4.1 |
| `HuggingFace` | Custom/local models (set `Path` to `.model` file) |

### `Timeouts`

| Key | Default | Description |
|-----|---------|-------------|
| `OutboundTotalSeconds` | 600 | Hard timeout for entire Foundry HTTP call |
| `StreamIdleSeconds` | 60 | Per-chunk idle timeout during SSE streaming |

### `Monitor`

| Key | Default | Description |
|-----|---------|-------------|
| `Enabled` | `true` | Capture requests to JSONL. Disable to stop all capture. |
| `MaxBodyBytes` | `10485760` | Max body size to capture per request (10 MB). Larger bodies are replaced with a truncation marker. |

When enabled, all requests are written to a JSONL file in `<datadir>/logs/requests-<bootId>.jsonl`. The file is purged and recreated on each restart. The admin console monitor page reads from this file.

## Data directory

Default locations:

| OS | Path |
|----|------|
| Windows | `%LOCALAPPDATA%\Claude2Foundry` |
| macOS | `~/Library/Application Support/Claude2Foundry` |
| Linux | `~/.local/share/claude2foundry` (or `$XDG_DATA_HOME/claude2foundry`) |

Override with `C2F_DATA_DIR` env var.

## Supported features

- Streaming (SSE) and non-streaming chat
- Tool use / function calling
- Image inputs (base64 and URL)
- Thinking/reasoning — strip, passthrough, or `reasoning_effort` mapping
- Token counting via tiktoken or HuggingFace tokenizers
- Model alias resolution
- Request monitoring with JSONL capture and live SSE tail
- Admin console (monitor, config editor, health, test page)
- Restart via wrapper script

## Requirements

- .NET 10 SDK
- Azure AI Foundry resource with model deployments
- `FOUNDRY_API_KEY` environment variable
