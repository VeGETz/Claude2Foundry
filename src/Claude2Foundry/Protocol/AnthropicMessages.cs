using System.Text.Json;
using System.Text.Json.Serialization;

namespace Claude2Foundry.Protocol.Anthropic;

public sealed class AnthropicMessagesRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("messages")]
    public required List<AnthropicMessage> Messages { get; init; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; init; }

    // string | List<AnthropicContentBlock>
    [JsonPropertyName("system")]
    public JsonElement? System { get; init; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; init; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; init; }

    [JsonPropertyName("top_k")]
    public int? TopK { get; init; }

    [JsonPropertyName("stop_sequences")]
    public List<string>? StopSequences { get; init; }

    [JsonPropertyName("tools")]
    public List<AnthropicTool>? Tools { get; init; }

    [JsonPropertyName("tool_choice")]
    public AnthropicToolChoice? ToolChoice { get; init; }

    [JsonPropertyName("metadata")]
    public AnthropicMetadata? Metadata { get; init; }

    [JsonPropertyName("service_tier")]
    public string? ServiceTier { get; init; }

    [JsonPropertyName("thinking")]
    public AnthropicThinkingConfig? Thinking { get; init; }

    [JsonPropertyName("disable_parallel_tool_use")]
    public bool? DisableParallelToolUse { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class AnthropicCountTokensRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("messages")]
    public required List<AnthropicMessage> Messages { get; init; }

    [JsonPropertyName("system")]
    public JsonElement? System { get; init; }

    [JsonPropertyName("tools")]
    public List<AnthropicTool>? Tools { get; init; }

    [JsonPropertyName("tool_choice")]
    public AnthropicToolChoice? ToolChoice { get; init; }
}

public sealed class AnthropicMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    // string | List<AnthropicContentBlock>
    [JsonPropertyName("content")]
    public required JsonElement Content { get; init; }
}

public sealed class AnthropicContentBlock
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    // text
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    // image
    [JsonPropertyName("source")]
    public AnthropicImageSource? Source { get; init; }

    // tool_use
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("input")]
    public JsonElement? Input { get; init; }

    // tool_result: content is string | List<AnthropicContentBlock>
    [JsonPropertyName("tool_use_id")]
    public string? ToolUseId { get; init; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Content { get; init; }

    [JsonPropertyName("is_error")]
    public bool? IsError { get; init; }

    // thinking
    [JsonPropertyName("thinking")]
    public string? Thinking { get; init; }

    [JsonPropertyName("signature")]
    public string? Signature { get; init; }

    // redacted_thinking
    [JsonPropertyName("data")]
    public string? Data { get; init; }
}

public sealed class AnthropicImageSource
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("media_type")]
    public string? MediaType { get; init; }

    [JsonPropertyName("data")]
    public string? Data { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

public sealed class AnthropicTool
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("input_schema")]
    public JsonElement InputSchema { get; init; }
}

public sealed class AnthropicToolChoice
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("disable_parallel_tool_use")]
    public bool? DisableParallelToolUse { get; init; }
}

public sealed class AnthropicThinkingConfig
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("budget_tokens")]
    public int? BudgetTokens { get; init; }
}

public sealed class AnthropicMetadata
{
    [JsonPropertyName("user_id")]
    public string? UserId { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

// Response types

public sealed class AnthropicMessagesResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "message";

    [JsonPropertyName("role")]
    public string Role { get; init; } = "assistant";

    [JsonPropertyName("content")]
    public required List<AnthropicContentBlock> Content { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }

    [JsonPropertyName("stop_sequence")]
    public string? StopSequence { get; init; }

    [JsonPropertyName("usage")]
    public required AnthropicUsage Usage { get; init; }
}

public sealed class AnthropicUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }

    [JsonPropertyName("cache_creation_input_tokens")]
    public int CacheCreationInputTokens { get; init; }

    [JsonPropertyName("cache_read_input_tokens")]
    public int CacheReadInputTokens { get; init; }
}

public sealed class AnthropicCountTokensResponse
{
    [JsonPropertyName("input_tokens")]
    public required int InputTokens { get; init; }
}

public sealed class AnthropicErrorResponse
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "error";

    [JsonPropertyName("error")]
    public required AnthropicErrorDetail Error { get; init; }
}

public sealed class AnthropicErrorDetail
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

// Streaming event types

public sealed class AnthropicStreamEvent
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    // message_start
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnthropicMessagesResponse? Message { get; init; }

    // content_block_start / content_block_stop
    [JsonPropertyName("index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Index { get; init; }

    [JsonPropertyName("content_block")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnthropicContentBlock? ContentBlock { get; init; }

    // content_block_delta
    [JsonPropertyName("delta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnthropicDelta? Delta { get; init; }

    // message_delta: also has delta (AnthropicMessageDelta) and usage
    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnthropicUsage? Usage { get; init; }

    // error
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnthropicErrorDetail? Error { get; init; }
}

public sealed class AnthropicDelta
{
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    // text_delta
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    // thinking_delta
    [JsonPropertyName("thinking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Thinking { get; init; }

    // signature_delta
    [JsonPropertyName("signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Signature { get; init; }

    // input_json_delta
    [JsonPropertyName("partial_json")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PartialJson { get; init; }

    // message_delta
    [JsonPropertyName("stop_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StopReason { get; init; }

    [JsonPropertyName("stop_sequence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StopSequence { get; init; }
}
