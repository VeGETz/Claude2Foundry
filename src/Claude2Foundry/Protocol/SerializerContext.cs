using System.Text.Json.Serialization;
using Claude2Foundry.Protocol.Anthropic;
using Claude2Foundry.Protocol.OpenAI;

namespace Claude2Foundry.Protocol;

[JsonSerializable(typeof(AnthropicMessagesRequest))]
[JsonSerializable(typeof(AnthropicCountTokensRequest))]
[JsonSerializable(typeof(AnthropicMessage))]
[JsonSerializable(typeof(AnthropicContentBlock))]
[JsonSerializable(typeof(AnthropicImageSource))]
[JsonSerializable(typeof(AnthropicTool))]
[JsonSerializable(typeof(AnthropicToolChoice))]
[JsonSerializable(typeof(AnthropicThinkingConfig))]
[JsonSerializable(typeof(AnthropicMetadata))]
[JsonSerializable(typeof(AnthropicMessagesResponse))]
[JsonSerializable(typeof(AnthropicUsage))]
[JsonSerializable(typeof(AnthropicCountTokensResponse))]
[JsonSerializable(typeof(AnthropicErrorResponse))]
[JsonSerializable(typeof(AnthropicErrorDetail))]
[JsonSerializable(typeof(AnthropicStreamEvent))]
[JsonSerializable(typeof(AnthropicDelta))]
[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(ChatContentPart))]
[JsonSerializable(typeof(ChatImageUrl))]
[JsonSerializable(typeof(ChatTool))]
[JsonSerializable(typeof(ChatFunction))]
[JsonSerializable(typeof(StreamOptions))]
[JsonSerializable(typeof(ToolCall))]
[JsonSerializable(typeof(ToolCallFunction))]
[JsonSerializable(typeof(ChatCompletionResponse))]
[JsonSerializable(typeof(ChatChoice))]
[JsonSerializable(typeof(AssistantMessage))]
[JsonSerializable(typeof(ChatUsage))]
[JsonSerializable(typeof(ChatCompletionChunk))]
[JsonSerializable(typeof(ChunkChoice))]
[JsonSerializable(typeof(ChunkDelta))]
[JsonSerializable(typeof(ToolCallChunk))]
[JsonSerializable(typeof(ToolCallChunkFunction))]
[JsonSerializable(typeof(FoundryErrorResponse))]
[JsonSerializable(typeof(FoundryError))]
[JsonSerializable(typeof(List<AnthropicContentBlock>))]
[JsonSerializable(typeof(List<AnthropicMessage>))]
[JsonSerializable(typeof(List<ChatMessage>))]
[JsonSerializable(typeof(List<ChatContentPart>))]
[JsonSerializable(typeof(List<ToolCall>))]
[JsonSerializable(typeof(List<ToolCallChunk>))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class AppJsonSerializerContext : JsonSerializerContext
{
}
