using System.Text.Json;
using System.Text.Json.Nodes;
using Claude2Foundry.Config;
using Claude2Foundry.Errors;
using Claude2Foundry.Protocol.Anthropic;
using Claude2Foundry.Protocol.OpenAI;
using Microsoft.Extensions.Logging;

namespace Claude2Foundry.Translation;

public sealed class ResponseTranslator(ProxyConfig config, ILogger<ResponseTranslator> logger)
{
    public AnthropicMessagesResponse Translate(
        ChatCompletionResponse upstream,
        AnthropicMessagesRequest original,
        string resolvedTarget,
        ProxyConfig? snapshot = null)
    {
        if (upstream.Choices.Count == 0)
            throw new AdapterException("Foundry returned no choices");

        var choice = upstream.Choices[0];
        var effectiveConfig = snapshot ?? config;
        var policy = effectiveConfig.ReasoningPolicies.GetValueOrDefault(resolvedTarget, "none");
        var content = BuildContentBlocks(choice.Message, policy);

        if (content.Count == 0)
            content.Add(new AnthropicContentBlock { Type = "text", Text = "" });

        return new AnthropicMessagesResponse
        {
            Id = upstream.Id,
            Model = original.Model,
            Content = content,
            StopReason = StopReasonMap.Map(choice.FinishReason),
            Usage = new AnthropicUsage
            {
                InputTokens = upstream.Usage.PromptTokens,
                OutputTokens = upstream.Usage.CompletionTokens,
            }
        };
    }

    private List<AnthropicContentBlock> BuildContentBlocks(AssistantMessage msg, string policy)
    {
        var content = new List<AnthropicContentBlock>();

        var reasoning = msg.ReasoningContent ?? msg.Reasoning;
        if (!string.IsNullOrEmpty(reasoning) && policy is "passthrough" or "effort")
        {
            content.Add(new AnthropicContentBlock
            {
                Type = "thinking",
                Thinking = reasoning,
                Signature = Guid.NewGuid().ToString("N"),
            });
        }

        if (!string.IsNullOrEmpty(msg.Content))
        {
            content.Add(new AnthropicContentBlock { Type = "text", Text = msg.Content });
        }

        foreach (var tc in msg.ToolCalls ?? [])
        {
            JsonNode? input;
            try
            {
                input = JsonNode.Parse(tc.Function.Arguments);
            }
            catch (JsonException ex)
            {
                logger.LogWarning("Failed to parse tool call arguments for '{Name}': {Args} — {Error}",
                    tc.Function.Name, tc.Function.Arguments, ex.Message);
                input = JsonNode.Parse("{}");
            }

            content.Add(new AnthropicContentBlock
            {
                Type = "tool_use",
                Id = tc.Id,
                Name = tc.Function.Name,
                Input = input,
            });
        }

        return content;
    }
}
