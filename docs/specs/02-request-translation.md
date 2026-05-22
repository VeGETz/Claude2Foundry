# 02 — Request translation (Anthropic → OpenAI)

## Source

Port the `_convert_*` methods on `AnthropicServingMessages` in `vllm/vllm/entrypoints/anthropic/serving.py` (lines 121–458). Drop the engine inheritance. All conversion is pure.

Target C# location: `src/Claude2Foundry/Translation/RequestTranslator.cs` — a stateless class with `static` methods or `public` methods on an instance carrying configuration (alias map, reasoning policies).

## Top-level entry

```
Translate(AnthropicMessagesRequest req, ProxyConfig cfg) -> ChatCompletionRequest
```

Order of operations:

1. Resolve target model: `cfg.ModelAliases[req.Model] ?? cfg.DefaultModel`. Log `originalModel → resolvedModel`.
2. Look up reasoning policy for the resolved target: `cfg.ReasoningPolicies[resolvedTarget] ?? "none"`.
3. Build the OpenAI `messages` list (steps 4–7).
4. Convert `req.System` to a system message and prepend.
5. Convert each `req.Messages[i]` to one or more OpenAI messages.
6. Apply tool translation (`req.Tools`, `req.ToolChoice`, `req.DisableParallelToolUse`).
7. Apply scalar field mapping and reasoning translation.

## System message

- `req.System` is null → no system message.
- `req.System` is a string → `{ "role": "system", "content": <string> }`.
- `req.System` is a list of `AnthropicContentBlock`:
  - Concatenate the `text` of every `type: "text"` block, joined by `\n`.
  - Skip any block whose `text` starts with `x-anthropic-billing-header` (vLLM strips these — preserve that behavior).
  - Drop `cache_control` markers if present on any block. **Log once per session at `Warning` level** that markers were stripped.
  - Result: `{ "role": "system", "content": <concatenated> }`.

## Per-message conversion

Loop over `req.Messages`. For each `AnthropicMessage`:

- `content` is a string → `{ "role": msg.Role, "content": msg.Content }`. Done.
- `content` is a list of blocks → process block-by-block, accumulating into:
  - `contentParts: List<ChatContentPart>` (text and images),
  - `toolCalls: List<ToolCall>` (assistant role only),
  - `reasoningText: string` (assistant role only, joined `\n`),
  - `extraMessages: List<ChatMessage>` (for tool_result fan-out — see below).

After the block loop, emit:

- If role is `assistant`: a single `assistant` message with `content = contentParts` (or string if all-text), `tool_calls = toolCalls` (omit if empty), and `reasoning_content = reasoningText` (omit if empty — Foundry accepts it on input for context carryover).
- If role is `user`: see "tool_result fan-out" below; otherwise a single `user` message.

Append `extraMessages` after the main message.

### Block conversion table

| Anthropic block | OpenAI output |
|---|---|
| `text` | `{ "type": "text", "text": <text> }` content part. |
| `image` with `source.type = "url"` | `{ "type": "image_url", "image_url": { "url": <url> } }` content part. |
| `image` with `source.type = "base64"` | `{ "type": "image_url", "image_url": { "url": "data:<media_type>;base64,<data>" } }` content part. |
| `tool_use` (assistant only) | Append to `toolCalls`: `{ "id": block.Id, "type": "function", "function": { "name": block.Name, "arguments": JsonSerializer.Serialize(block.Input) } }`. Do not add to `contentParts`. |
| `tool_result` (user only) | See fan-out below. |
| `thinking` (assistant only) | Append `block.Thinking` to `reasoningText`. Do not add to `contentParts`. |
| `redacted_thinking` | Drop silently — opaque safety content. |
| `tool_reference` | Drop here; surfaced only inside `tool_result` content. |

### tool_result fan-out (user role only)

Anthropic's `tool_result` block is a user-message-attached result; OpenAI requires a separate `role: "tool"` message keyed by `tool_call_id`. Translation:

- For each `tool_result` block in the user message:
  - If `block.Content` is a string:
    - Emit `{ "role": "tool", "tool_call_id": block.ToolUseId, "content": block.Content }`.
  - If `block.Content` is a list of sub-blocks:
    - Accumulate `text` sub-blocks into one combined string. Emit a `role: "tool"` message with that combined text as content (or empty string if no text sub-blocks).
    - For each `image` sub-block, emit a separate `role: "user"` message containing only that image as `content` (`[{ "type": "image_url", ... }]`). This is the documented limitation: OpenAI tool messages cannot carry images, and surfacing the image as a follow-up user message preserves the data at the cost of message-order purity.
    - For each `tool_reference` sub-block, emit `{ "role": "tool", "tool_call_id": block.ToolUseId, "content": [ { "type": "tool_reference", "name": <ref.name> } ] }`. Some Foundry models reject this content shape; consider it best-effort.
- Any non-`tool_result` blocks in the same user message merge into a single `user` message that comes *first* (text/image parts of the user message itself), followed by the fan-out tool/user messages.

## Tool definitions

- `req.Tools` (list of `AnthropicTool`) → OpenAI `tools` array.
- Per tool: `{ "type": "function", "function": { "name": tool.Name, "description": tool.Description, "parameters": tool.InputSchema } }`.
- vLLM's translator also writes a `defer_loading: false` flag. Omit — that's a vLLM extension.

## tool_choice

| Anthropic `tool_choice.type` | OpenAI `tool_choice` |
|---|---|
| `"auto"` (default) | `"auto"` |
| `"any"` | `"required"` |
| `"none"` | `"none"` |
| `"tool"` with `name` | `{ "type": "function", "function": { "name": <name> } }` |

If `req.ToolChoice` is null and `req.Tools` is non-empty, omit `tool_choice` (let Foundry default to `"auto"`).

## disable_parallel_tool_use

- `req.DisableParallelToolUse == true` → set `req.parallel_tool_calls = false` on the OpenAI request.
- `req.ToolChoice?.DisableParallelToolUse == true` carries the same meaning — apply identically.
- Otherwise omit. (Foundry defaults vary by model.)

## Scalar field mapping

| Anthropic | OpenAI |
|---|---|
| `model` | resolved via `ModelAliases` (see top of file). |
| `max_tokens` | set both `max_tokens` *and* `max_completion_tokens` (o-series uses the latter; others accept the former). |
| `temperature` | passthrough. |
| `top_p` | passthrough. |
| `stop_sequences` | `stop` (array, max 4 entries — truncate if longer and log `Warning`). |
| `stream` | passthrough. When `true`, also set `stream_options.include_usage = true` to get the final usage chunk. |
| `metadata.user_id` | `user`. Drop the rest of `metadata`. |
| `service_tier` | strip silently. |
| `top_k` | strip silently. |
| `cache_control` (on any block) | strip with per-session-once `Warning` log. |

## Thinking translation

Decided by reasoning policy of the resolved target:

- Policy `none`:
  - Strip `req.Thinking` entirely. Do not set `reasoning_effort`.
- Policy `passthrough`:
  - Strip `req.Thinking` entirely. Do not set `reasoning_effort`. The response translator will pull `reasoning_content` back. (DeepSeek-R1 reasons unconditionally.)
- Policy `effort`:
  - If `req.Thinking?.Type == "enabled"`:
    - `req.Thinking.BudgetTokens < 2000` → `reasoning_effort = "low"`.
    - `2000 <= req.Thinking.BudgetTokens <= 8000` → `reasoning_effort = "medium"`.
    - `req.Thinking.BudgetTokens > 8000` → `reasoning_effort = "high"`.
  - If `req.Thinking` is null or `Type == "disabled"`:
    - `reasoning_effort = "medium"` (default for `effort`-policy targets — o-series requires the field).
  - Strip the original `thinking` block from the outbound request.

## Pure translation, no upstream calls

`RequestTranslator` does no I/O. The output `ChatCompletionRequest` is then handed to the backend client (`OpenAIBackendClient`) which performs the HTTP call. This separation lets the translator be unit-tested without a Foundry account.

## Test fixtures to write

- System as string vs. list.
- Single user text message.
- User message with `image` + `text` + a follow-up `tool_result`.
- Assistant message with `text` + `thinking` + `tool_use` block.
- `tool_choice` in each of its four forms.
- All three reasoning policies, with and without a `thinking` block on the request.
- `top_k`, `service_tier`, `cache_control` stripping.
