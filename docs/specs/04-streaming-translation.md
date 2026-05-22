# 04 — Streaming translation

## Source

Port `message_stream_converter` and `_ActiveBlockState` from `vllm/vllm/entrypoints/anthropic/serving.py` (around lines 511–750). Target: `src/Claude2Foundry/Translation/StreamTranslator.cs`.

## Shape

```
TranslateStream(
  IAsyncEnumerable<ChatCompletionChunk> upstream,
  AnthropicMessagesRequest original,
  string resolvedTarget,
  ProxyConfig cfg,
  CancellationToken ct
) -> IAsyncEnumerable<string>      // SSE-formatted Anthropic events ready to write to wire
```

Each yielded string is a complete SSE frame: `event: <type>\ndata: <json>\n\n`. The HTTP handler writes them as they arrive with `Content-Type: text/event-stream`, `Cache-Control: no-cache`, no buffering.

## Anthropic SSE event sequence

For one assistant turn the wire MUST emit, in order:

```
message_start
[ for each content block: content_block_start, one-or-more content_block_delta, content_block_stop ]
message_delta
message_stop
```

Optional `ping` events may be interleaved between blocks (keep-alive — emit one every 15s if no other event has been sent).

`error` events may interrupt anywhere and end the stream.

## State machine

Maintain across the lifetime of the stream:

```csharp
class StreamState {
  string MessageId;                       // from upstream.Id, set on first chunk
  AnthropicUsage CurrentUsage;            // tracks running totals
  int NextBlockIndex;                     // 0, 1, 2, ... per Anthropic spec
  ActiveBlock? Active;                    // null between blocks
  Dictionary<int, string> ToolIndexToId;  // OpenAI tool_call.index -> Anthropic block id
  string? FinishReason;                   // set when a chunk arrives with finish_reason
}

class ActiveBlock {
  int Index;
  string Type;          // "text" | "thinking" | "tool_use"
  string? ToolUseId;    // when Type == "tool_use"
  string? Signature;    // when Type == "thinking" (UUID hex)
}
```

## First chunk

Emit `message_start`:

```json
{
  "type": "message_start",
  "message": {
    "id": "<upstream.Id>",
    "type": "message",
    "role": "assistant",
    "content": [],
    "model": "<original.Model>",
    "stop_reason": null,
    "stop_sequence": null,
    "usage": { "input_tokens": 0, "output_tokens": 0, "cache_creation_input_tokens": 0, "cache_read_input_tokens": 0 }
  }
}
```

The input_tokens here is `0` initially; OpenAI doesn't expose prompt tokens until the final chunk with `stream_options.include_usage`. vLLM emits `0` then updates via `message_delta` at the end — preserve that.

## Per-chunk handling

For each `ChatCompletionChunk`:

1. If `chunk.Usage` is present, accumulate into `state.CurrentUsage`.
2. For `chunk.Choices[0]`:
   - If `delta.FinishReason` present, remember it (do not emit immediately).
   - If `delta.ReasoningContent` is non-zero-length:
     - Resolved target policy must be `passthrough` or `effort` to emit; if `none`, drop.
     - If `state.Active?.Type != "thinking"`: close current block (if any), start a new `thinking` block.
     - Emit `content_block_delta` with `delta: { type: "thinking_delta", thinking: <delta.ReasoningContent> }`.
   - If `delta.Content` is non-zero-length:
     - If `state.Active?.Type != "text"`: close current block, start a new `text` block.
     - Emit `content_block_delta` with `delta: { type: "text_delta", text: <delta.Content> }`.
   - If `delta.ToolCalls` is non-empty:
     - For each `toolCallChunk` (each carries `index`, optionally `id`, optionally `function.name`, optionally `function.arguments`):
       - If `toolCallChunk.Id` is set, record `state.ToolIndexToId[index] = id`.
       - If a *new* tool index appears (not seen before) OR a tool index is appearing for the first time and we are not currently in its block:
         - Close current block, start a new `tool_use` block with `Id = state.ToolIndexToId[index]`, `Name = toolCallChunk.Function.Name`.
       - If `toolCallChunk.Function?.Arguments` is non-empty:
         - Emit `content_block_delta` with `delta: { type: "input_json_delta", partial_json: <arguments> }`.

## Closing a block

Helper `StopActiveBlock(state)`:

- If `state.Active` is null, no-op.
- If `state.Active.Type == "thinking"`: emit `content_block_delta` with `delta: { type: "signature_delta", signature: <state.Active.Signature> }` first.
- Emit `content_block_stop` with `{ "type": "content_block_stop", "index": state.Active.Index }`.
- Set `state.Active = null`.

## Starting a block

Helper `StartBlock(state, type, toolCallChunk?)`:

- Increment `state.NextBlockIndex` (use the prior value as `Active.Index`).
- `state.Active = new ActiveBlock { Index = state.NextBlockIndex - 1, Type = type, ToolUseId = ..., Signature = (type == "thinking" ? Guid.NewGuid().ToString("N") : null) }`.
- Emit `content_block_start` with the appropriate initial block payload:
  - `text` → `{ "type": "text", "text": "" }`.
  - `thinking` → `{ "type": "thinking", "thinking": "", "signature": "" }` (signature filled at close).
  - `tool_use` → `{ "type": "tool_use", "id": <id>, "name": <name>, "input": {} }`.

## Final chunks

When upstream signals end (enumerator completes) OR a chunk's `finish_reason` was set:

1. `StopActiveBlock(state)`.
2. Emit `message_delta`:
   ```json
   {
     "type": "message_delta",
     "delta": { "stop_reason": "<mapped>", "stop_sequence": null },
     "usage": { "input_tokens": <state.CurrentUsage.InputTokens>, "output_tokens": <state.CurrentUsage.OutputTokens>, "cache_creation_input_tokens": 0, "cache_read_input_tokens": 0 }
   }
   ```
   `stop_reason` mapping is the same table as [03](./03-response-translation.md).
3. Emit `message_stop`: `{ "type": "message_stop" }`.

## Errors mid-stream

If the upstream enumerator throws or yields an HTTP error indicator (e.g. a non-2xx response detected before any chunks, OR a connection drop mid-stream):

- If no `message_start` has been emitted yet: emit no SSE at all; return the error via [05 error mapping](./05-error-mapping.md) as a regular HTTP response. This requires the handler to buffer the first chunk before committing to SSE — see [07](./07-endpoints-and-hosting.md).
- If `message_start` was already emitted: emit `error` event with the mapped Anthropic error payload, then close the stream. Do not attempt `message_stop`.

## Idle-chunk timeout

If `60s` elapses with no chunk from upstream (`9b` in CONTEXT), throw `TimeoutException` from the enumerator. The handler treats it as a mid-stream error per the rule above.

## Keep-alive ping

Optional. If you implement: every 15s of stream wall-time with no other event, emit `event: ping\ndata: {"type": "ping"}\n\n`. Claude Code tolerates absence.

## Test fixtures

- One chunk only (whole response in `choices[0].delta.content`).
- Multi-chunk text streaming.
- Single tool call streamed across many chunks (`name` only in first, `arguments` partial JSON in following).
- Parallel tool calls — two tool indices interleaved.
- `reasoning_content` followed by `content` followed by `tool_calls` (DeepSeek-R1 with tool use).
- Mid-stream upstream disconnect.
