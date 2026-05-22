# 08 — Token counting

## Endpoint

```
POST /v1/messages/count_tokens
Content-Type: application/json
```

Request body matches `AnthropicCountTokensRequest`: `model`, `messages`, `system`, `tools`, `tool_choice`.

Response:

```json
{ "input_tokens": <int> }
```

HTTP 200 on success. Errors per [05](./05-error-mapping.md), but only the adapter-validation rules apply (no upstream call).

## Implementation outline

```csharp
async Task<int> Count(AnthropicCountTokensRequest req, ProxyConfig cfg) {
  var target = cfg.ModelAliases.GetValueOrDefault(req.Model, cfg.DefaultModel);
  var tokenizerCfg = cfg.Tokenizers.GetValueOrDefault(target, new TokenizerConfig("TiktokenCl100k", null));
  var tokenizer = TokenizerRegistry.Get(tokenizerCfg);  // cached
  var (textCorpus, imageCount) = Serialize(req);        // see below
  return tokenizer.CountTokens(textCorpus) + (imageCount * 1500);
}
```

## Serialization for counting

Build a single string from the request, in this order, separated by `\n`:

1. `req.System` if present:
   - string → use as-is.
   - list of blocks → join `text` blocks with `\n`, skip non-text.
2. Each `tool` in `req.Tools`: `"<tool.Name>\n<tool.Description>\n<JSON.Serialize(tool.InputSchema)>"`.
3. Each `req.Messages[i]`:
   - String content → use directly, prefixed by role: `"<role>: <text>"`.
   - List of blocks:
     - `text` block → its `text`.
     - `image` block → emit nothing here; increment `imageCount`.
     - `tool_use` block → `"tool_use:<name>(<JSON of input>)"`.
     - `tool_result` block → if `content` is string, use it; if list, recursively expand text/image as above.
     - `thinking` block → its `thinking`.
     - `redacted_thinking` → skip.
     - `tool_reference` → its `name`.

This is a heuristic. It does not exactly match what the upstream model sees — chat template formatting (which the adapter doesn't apply) shifts counts by a handful. Documented as approximate.

## Tokenizer sources

### `TiktokenCl100k`

`Microsoft.ML.Tokenizers.TiktokenTokenizer.CreateForEncoding("cl100k_base")` (or the `CreateForModel("gpt-4")` shortcut). No file needed.

### `TiktokenO200k`

`TiktokenTokenizer.CreateForEncoding("o200k_base")` (or `CreateForModel("gpt-4o")`). No file needed.

### `HuggingFace`

`Microsoft.ML.Tokenizers.BpeTokenizer.Create` / `LlamaTokenizer.Create` / the generic `Tokenizer.CreateAsync` loader — verify the exact API at implementation time. Pass the file path from config. Cache the loaded tokenizer (single instance per path).

## Image cost

Fixed `1500` per image content block, anywhere in the request (including inside `tool_result`). This matches Anthropic's published heuristic for the Claude tokenizer. We apply it regardless of upstream model — operators understand this is approximate.

## Caching

Build a single `Dictionary<string, Tokenizer>` keyed by target name, populated lazily on first request per target. Tokenizer instances are thread-safe per `Microsoft.ML.Tokenizers` docs.

## Test fixtures

- Empty request → 0 tokens.
- Request with system + 3 messages all text → finite count, deterministic.
- Request with image-only user message → 1500.
- Request with 5 tools → tool definitions counted in the corpus.
- Request with `tool_result` containing nested image → 1500 + text count.

## Not implemented

- No upstream `/tokenize` proxying. Even when the target is a vLLM-hosted Foundry model that exposes such an endpoint, the latency of a network round-trip per `count_tokens` is worse than a slightly-wrong local count. Local-only.
- No per-message overhead constant. Some chat templates inject `<|im_start|>` etc. — adapter ignores.
