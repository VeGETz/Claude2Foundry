# 03 — Response translation (non-streaming)

## Source

Port from `vllm/vllm/entrypoints/anthropic/serving.py` non-streaming converter (around lines 459–508). Target: `src/Claude2Foundry/Translation/ResponseTranslator.cs`.

## Entry

```
Translate(ChatCompletionResponse upstream, AnthropicMessagesRequest original, string resolvedTarget, ProxyConfig cfg) -> AnthropicMessagesResponse
```

`original` is needed because Anthropic's response echoes the *requested* model identifier (`original.Model`), not the resolved target. Claude Code displays this to the user.

## Construction

```csharp
new AnthropicMessagesResponse {
  Id = upstream.Id,
  Type = "message",
  Role = "assistant",
  Model = original.Model,                  // echo the alias the client sent
  Content = BuildContentBlocks(...),       // see below
  StopReason = MapStopReason(choice.FinishReason),
  StopSequence = null,                     // OpenAI doesn't surface which stop matched
  Usage = new AnthropicUsage {
    InputTokens  = upstream.Usage.PromptTokens,
    OutputTokens = upstream.Usage.CompletionTokens,
    CacheCreationInputTokens = 0,
    CacheReadInputTokens = 0,
  },
}
```

Use `upstream.Choices[0]`. If `upstream.Choices` is empty, raise an adapter error (`[Adapter] Foundry returned no choices`).

## Stop reason map

| OpenAI `finish_reason` | Anthropic `stop_reason` |
|---|---|
| `"stop"` | `"end_turn"` |
| `"length"` | `"max_tokens"` |
| `"tool_calls"` | `"tool_use"` |
| `"content_filter"` | `"end_turn"` (no Anthropic equivalent; closest semantic) |
| null / unknown | `"end_turn"` |

## Content blocks

Inspect `choice.Message`. Emit blocks in this order:

1. If `Message.ReasoningContent` is non-empty **and** the resolved target's reasoning policy is `passthrough` or `effort`:
   - Emit `AnthropicContentBlock { Type = "thinking", Thinking = Message.ReasoningContent, Signature = Guid.NewGuid().ToString("N") }`.
   - The `signature` is opaque to Claude Code; vLLM uses a UUID hex — match that.
   - If policy is `none`, drop `reasoning_content` even if present.
2. If `Message.Content` is non-empty:
   - Emit `AnthropicContentBlock { Type = "text", Text = Message.Content }`.
3. If `Message.ToolCalls` is non-empty:
   - For each tool call in order, emit `AnthropicContentBlock { Type = "tool_use", Id = call.Id, Name = call.Function.Name, Input = JsonSerializer.Deserialize<JsonElement>(call.Function.Arguments) }`.
   - If `Function.Arguments` fails to parse as JSON, emit `Input = {}` and log `Warning` with the raw string. Don't fail the response.

If after all steps the content list is empty (rare — empty assistant turn), emit a single `text` block with empty string to satisfy Claude Code's parser.

## Pure function

No I/O. The handler that calls this also writes the HTTP response.
