# `ProxyConfig` JSON Schema — frozen contract

Schema returned by `GET /api/admin/config/schema`. Draft 2020-12. Drives client-side form rendering. Frozen in phase 0; phase 1 may generate this from C# types as long as the on-the-wire shape matches.

The schema below is the **expected shape**. Field documentation is in the `description` field per JSON Schema convention.

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://claude2foundry.local/schema/proxy-config.json",
  "title": "ProxyConfig",
  "type": "object",
  "required": ["BackendUrl", "ApiKeyEnv", "DefaultModel"],
  "properties": {
    "BackendUrl": {
      "type": "string",
      "pattern": "^https?://.+/openai/v1/?$",
      "description": "Azure Foundry OpenAI endpoint URL. Must end with /openai/v1/ (trailing slash recommended). Bootstrap field — change requires restart."
    },
    "ApiKeyEnv": {
      "type": "string",
      "minLength": 1,
      "description": "Name of the environment variable holding the Foundry API key. Bootstrap field — change requires restart. The value itself is read from the env var at startup, never stored in this config."
    },
    "DefaultModel": {
      "type": "string",
      "minLength": 1,
      "description": "Foundry deployment name used when no entry in ModelAliases matches the incoming Claude model id."
    },
    "ModelAliases": {
      "type": "object",
      "description": "Map of Anthropic model id → Foundry deployment name.",
      "additionalProperties": {
        "type": "string",
        "minLength": 1
      },
      "default": {}
    },
    "ReasoningPolicies": {
      "type": "object",
      "description": "Map of Foundry deployment name → reasoning policy. Unknown targets default to 'none'.",
      "additionalProperties": {
        "type": "string",
        "enum": ["none", "passthrough", "effort"]
      },
      "default": {}
    },
    "Tokenizers": {
      "type": "object",
      "description": "Map of Foundry deployment name → tokenizer config. Unknown targets default to TiktokenCl100k.",
      "additionalProperties": {
        "$ref": "#/$defs/Tokenizer"
      },
      "default": {}
    },
    "Timeouts": {
      "type": "object",
      "properties": {
        "OutboundTotalSeconds": {
          "type": "integer",
          "minimum": 1,
          "default": 600,
          "description": "Hard timeout for the entire non-streaming HTTP call to Foundry, in seconds."
        },
        "StreamIdleSeconds": {
          "type": "integer",
          "minimum": 1,
          "default": 60,
          "description": "Per-chunk idle timeout during SSE streaming, in seconds."
        }
      },
      "required": ["OutboundTotalSeconds", "StreamIdleSeconds"],
      "default": { "OutboundTotalSeconds": 600, "StreamIdleSeconds": 60 }
    },
    "Monitor": {
      "type": "object",
      "description": "Adapter Console capture and retention settings.",
      "properties": {
        "CaptureMode": {
          "type": "string",
          "enum": ["hybrid", "full"],
          "default": "hybrid",
          "description": "hybrid: store summaries + 8 KB previews in the ring, full bodies in a short-TTL cache. full: store everything in the ring and JSONL."
        },
        "LogMaxBytes": {
          "type": "integer",
          "minimum": 1048576,
          "default": 104857600,
          "description": "Per-file size cap for rolling JSONL request-capture files, in bytes."
        },
        "LogRetentionDays": {
          "type": "integer",
          "minimum": 0,
          "default": 7,
          "description": "Number of days to retain rolling JSONL files. 0 disables persistence (in-memory ring only)."
        }
      },
      "default": {
        "CaptureMode": "hybrid",
        "LogMaxBytes": 104857600,
        "LogRetentionDays": 7
      }
    }
  },
  "$defs": {
    "Tokenizer": {
      "type": "object",
      "required": ["Source"],
      "oneOf": [
        {
          "title": "Built-in tiktoken",
          "properties": {
            "Source": {
              "type": "string",
              "enum": ["TiktokenCl100k", "TiktokenO200k"]
            }
          },
          "required": ["Source"],
          "not": { "required": ["Path"] }
        },
        {
          "title": "HuggingFace tokenizer.json",
          "properties": {
            "Source": { "type": "string", "const": "HuggingFace" },
            "Path": { "type": "string", "minLength": 1 }
          },
          "required": ["Source", "Path"]
        }
      ]
    }
  }
}
```

## Notes for phase 1

- The schema must be served at `GET /api/admin/config/schema` with exactly this shape (additive fields are allowed; renames, removals, and shape changes are not).
- If `NJsonSchema` (or any other generator) produces output that differs cosmetically (key order, optional `$schema` declarations), that is fine. The semantic shape — types, enums, requireds, the `oneOf` discriminator on `Tokenizer` — must match.
- The schema-driven UI in phase 2 supports the subset listed in phase 2 task 7.1. Anything outside that subset must not appear in this schema.

## Notes for phase 2

- The `default` fields are used by the UI to populate placeholders on first paint when no value is present.
- The `description` fields are surfaced as inline help (e.g. as `title` tooltips on form labels).
- The `additionalProperties` schemas drive the "add row" affordance in map editors.
- The `oneOf` on `Tokenizer` drives the union renderer (radio for `Source`, conditional `Path` field).
