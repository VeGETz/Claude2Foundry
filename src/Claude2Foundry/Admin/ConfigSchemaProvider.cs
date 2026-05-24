using System.Text.Json.Nodes;

namespace Claude2Foundry.Admin;

public static class ConfigSchemaProvider
{
    public static readonly JsonNode Schema = BuildSchema();

    private static JsonNode BuildSchema() => JsonNode.Parse("""
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
              "additionalProperties": { "type": "string", "minLength": 1 },
              "default": {}
            },
            "ReasoningPolicies": {
              "type": "object",
              "description": "Map of Foundry deployment name → reasoning policy. Unknown targets default to 'none'.",
              "additionalProperties": { "type": "string", "enum": ["none", "passthrough", "effort"] },
              "default": {}
            },
            "Tokenizers": {
              "type": "object",
              "description": "Map of Foundry deployment name → tokenizer config. Unknown targets default to TiktokenCl100k.",
              "additionalProperties": { "$ref": "#/$defs/Tokenizer" },
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
              "description": "Adapter Console request capture settings.",
              "properties": {
                "Enabled": {
                  "type": "boolean",
                  "default": true,
                  "description": "Enable JSONL request capture. When false, no file is created and no writes occur."
                },
                "MaxBodyBytes": {
                  "type": "integer",
                  "minimum": 1,
                  "default": 10485760,
                  "description": "Per-body size cap in bytes. Bodies exceeding this are replaced with a truncation marker."
                }
              },
              "default": { "Enabled": true, "MaxBodyBytes": 10485760 }
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
                    "Source": { "type": "string", "enum": ["TiktokenCl100k", "TiktokenO200k"] }
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
        """)!;
}
