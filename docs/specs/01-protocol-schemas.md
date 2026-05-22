# 01 — Protocol schemas

## Source

Port from `vllm/vllm/entrypoints/anthropic/protocol.py`. Both Anthropic and OpenAI Pydantic models live there. Target: C# `record` types in `src/Claude2Foundry/Protocol/` using `System.Text.Json` with source generation.

## Anthropic side (port verbatim)

C# namespace: `Claude2Foundry.Protocol.Anthropic`. File: `AnthropicMessages.cs`.

### Request types

- `AnthropicMessagesRequest` — `model`, `messages`, `max_tokens`, `system` (string or list of blocks), `stream`, `temperature`, `top_p`, `top_k`, `stop_sequences`, `tools`, `tool_choice`, `metadata`, `service_tier`, `thinking`, `disable_parallel_tool_use`. Plus passthrough holders for unknown fields (use `[JsonExtensionData]`).
- `AnthropicMessage` — `role` ("user" | "assistant"), `content` (string or `List<AnthropicContentBlock>`).
- `AnthropicContentBlock` — discriminated by `type`: `text`, `image`, `tool_use`, `tool_result`, `tool_reference`, `thinking`, `redacted_thinking`. Each variant carries its own fields (`text`; `source` with `type`/`media_type`/`data`/`url`; `id`/`name`/`input`; `tool_use_id`/`content`/`is_error`; `thinking`/`signature`).
- `AnthropicTool` — `name`, `description`, `input_schema` (JSON schema object).
- `AnthropicToolChoice` — `type` ("auto" | "any" | "tool" | "none"), `name` (required when `type` is "tool"), `disable_parallel_tool_use`.
- `AnthropicThinkingConfig` — `type` ("enabled" | "disabled"), `budget_tokens`.
- `AnthropicCountTokensRequest` — same shape minus inference knobs.

### Response types

- `AnthropicMessagesResponse` — `id`, `type` ("message"), `role` ("assistant"), `content` (`List<AnthropicContentBlock>`), `model`, `stop_reason` ("end_turn" | "max_tokens" | "stop_sequence" | "tool_use"), `stop_sequence`, `usage`.
- `AnthropicUsage` — `input_tokens`, `output_tokens`, `cache_creation_input_tokens`, `cache_read_input_tokens`. The cache fields are emitted as 0; Foundry has no equivalent.
- `AnthropicCountTokensResponse` — `input_tokens`.
- `AnthropicErrorResponse` — `type` ("error"), `error` (with `type` and `message`).

### Streaming event types

- `AnthropicStreamEvent` discriminated by `type`: `message_start`, `message_delta`, `message_stop`, `content_block_start`, `content_block_delta`, `content_block_stop`, `ping`, `error`.
- `AnthropicDelta` discriminated by `type`: `text_delta`, `input_json_delta`, `thinking_delta`, `signature_delta`. Plus the outer `message_delta` shape carrying `stop_reason`, `stop_sequence`, `usage`.

Implementations of polymorphic union types use `JsonPolymorphic`/`JsonDerivedType` attributes with the `type` discriminator. Source-gen the serializer context for AOT-friendliness.

## OpenAI side (minimum subset needed)

C# namespace: `Claude2Foundry.Protocol.OpenAI`. File: `OpenAIChat.cs`.

Only the request and response shapes Foundry's `/v1/chat/completions` actually uses are needed. Do not port the full OpenAI Pydantic surface.

### Request

- `ChatCompletionRequest` — `model`, `messages`, `temperature`, `top_p`, `max_tokens`, `max_completion_tokens`, `stop`, `stream`, `stream_options` (`include_usage`), `tools`, `tool_choice`, `parallel_tool_calls`, `user`, `reasoning_effort`.
- `ChatMessage` discriminated by `role`: `system`, `user`, `assistant`, `tool`. Content can be a string or a list of content parts (`{ "type": "text" | "image_url", ... }`). Assistant messages may carry `tool_calls`. Tool messages carry `tool_call_id`.
- `ChatTool` — `type: "function"`, `function: { name, description, parameters }`.
- `ChatToolChoice` — string ("auto" | "required" | "none") or object (`{ "type": "function", "function": { "name": ... } }`).

### Response (non-streaming)

- `ChatCompletionResponse` — `id`, `model`, `choices: List<Choice>`, `usage: Usage`.
- `Choice` — `index`, `finish_reason` ("stop" | "length" | "tool_calls" | "content_filter"), `message: AssistantMessage`.
- `AssistantMessage` — `content` (string or null), `reasoning_content` (string or null — Foundry-specific, present on DeepSeek-R1 and o-series), `tool_calls: List<ToolCall>?`.
- `ToolCall` — `id`, `type: "function"`, `function: { name, arguments (JSON-encoded string) }`.
- `Usage` — `prompt_tokens`, `completion_tokens`, `total_tokens`.

### Response (streaming chunks)

- `ChatCompletionChunk` — `id`, `model`, `choices: List<ChunkChoice>`, `usage: Usage?` (only on the last chunk when `stream_options.include_usage = true`).
- `ChunkChoice` — `index`, `delta: ChunkDelta`, `finish_reason?`.
- `ChunkDelta` — `role?`, `content?`, `reasoning_content?`, `tool_calls?: List<ToolCallChunk>`. Tool call chunks carry `index`, `id?`, `function?: { name?, arguments? }`. Arguments are streamed as partial JSON strings.

## Serialization rules

- JSON snake_case across the wire on both sides. Use `[JsonPropertyName]` per record field or a snake_case naming policy.
- Numeric fields: ints, not strings.
- `null` vs absent: write absent when the source field is null. Anthropic and OpenAI both reject some explicit-null fields.
- Use `JsonSerializerContext` source generation. The serializer registered with ASP.NET Core must use the source-gen context to support AOT.

## What NOT to port

- Anything labeled `kv_transfer_params`, `chat_template_kwargs` in the vLLM types — these are vLLM engine extensions, not part of the public OpenAI or Anthropic surface. Drop them.
- The `tool_reference` block type appears in newer Anthropic specs but is rarely emitted by Claude Code; keep the type for completeness but vLLM's translator already handles it as a passthrough inside `tool_result`.
