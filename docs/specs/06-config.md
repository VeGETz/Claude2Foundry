# 06 — Configuration

## File

`appsettings.json` next to the binary. Optional `appsettings.Development.json` overrides at dev time. ASP.NET Core's standard `IConfiguration` binding.

Standard env-var override applies: `Proxy__BackendUrl` overrides `Proxy:BackendUrl`, etc.

## Schema

```json
{
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://127.0.0.1:8787" }
    }
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "Proxy": {
    "BackendUrl": "https://my-resource.openai.azure.com/openai/v1/",
    "ApiKeyEnv": "FOUNDRY_API_KEY",
    "DefaultModel": "DeepSeek-V3",

    "ModelAliases": {
      "claude-opus-4-7":   "o4-mini",
      "claude-sonnet-4-6": "DeepSeek-V3",
      "claude-haiku-4-5":  "DeepSeek-V3",
      "claude-3-5-haiku":  "DeepSeek-V3"
    },

    "ReasoningPolicies": {
      "DeepSeek-V3": "none",
      "DeepSeek-R1": "passthrough",
      "o4-mini":     "effort",
      "gpt-4.1":     "none"
    },

    "Tokenizers": {
      "DeepSeek-V3": { "Source": "TiktokenCl100k" },
      "DeepSeek-R1": { "Source": "HuggingFace", "Path": "./tokenizers/deepseek-r1.json" },
      "o4-mini":     { "Source": "TiktokenO200k" },
      "gpt-4.1":     { "Source": "TiktokenO200k" }
    },

    "Timeouts": {
      "OutboundTotalSeconds": 600,
      "StreamIdleSeconds": 60
    }
  }
}
```

## Bind to records

```csharp
public sealed record ProxyConfig(
  string BackendUrl,
  string ApiKeyEnv,
  string DefaultModel,
  Dictionary<string, string> ModelAliases,
  Dictionary<string, string> ReasoningPolicies,
  Dictionary<string, TokenizerConfig> Tokenizers,
  TimeoutsConfig Timeouts
);

public sealed record TokenizerConfig(string Source, string? Path);

public sealed record TimeoutsConfig(int OutboundTotalSeconds, int StreamIdleSeconds);
```

Bind under section `"Proxy"`.

## Validation at startup

Fail fast (`throw`, exit non-zero, log clear message) if any of:

- `BackendUrl` missing or not a well-formed `https://*.openai.azure.com/openai/v1/` URL (trailing `/v1/` is required).
- Env var named by `ApiKeyEnv` is unset or empty.
- `DefaultModel` is empty.
- Any `ReasoningPolicies` value is not in `{ "none", "passthrough", "effort" }`.
- Any `Tokenizers[*].Source` is not in `{ "TiktokenCl100k", "TiktokenO200k", "HuggingFace" }`.
- For `HuggingFace` source, `Path` is missing or the file does not exist on disk.
- `Timeouts.OutboundTotalSeconds < 1` or `StreamIdleSeconds < 1`.

Warn but don't fail if:

- A target referenced in `ModelAliases` has no entry in `ReasoningPolicies` (defaults to `none`).
- A target has no entry in `Tokenizers` (defaults to `TiktokenCl100k`).

## What goes in env vars vs file

- API key: env var, name configurable. **Never** in `appsettings.json`. Validation refuses an empty value.
- Listen URL: `appsettings.json` (under `Kestrel`).
- Everything else: file.

Operators who want a single-binary deploy can override anything via env var with the `Proxy__*` prefix.

## No hot reload

Config is read once at startup. Restart to change. Document in README.
