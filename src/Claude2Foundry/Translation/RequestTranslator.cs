using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claude2Foundry.Config;
using Claude2Foundry.Protocol.Anthropic;
using Claude2Foundry.Protocol.OpenAI;
using Microsoft.Extensions.Logging;

namespace Claude2Foundry.Translation;

public sealed class RequestTranslator(ProxyConfig config, ILogger<RequestTranslator> logger)
{
    private static readonly ConcurrentDictionary<string, byte> _warnedFields = new();

    public (ChatCompletionRequest Request, string ResolvedTarget) Translate(AnthropicMessagesRequest req, ProxyConfig? snapshot = null)
    {
        var effectiveConfig = snapshot ?? config;
        var resolved = effectiveConfig.ModelAliases.GetValueOrDefault(req.Model, effectiveConfig.DefaultModel);
        logger.LogInformation("model={Original}->{Resolved}", req.Model, resolved);

        var policy = effectiveConfig.ReasoningPolicies.GetValueOrDefault(resolved, "none");
        var messages = new List<ChatMessage>();

        BuildSystemMessage(req.System, messages);
        BuildMessages(req.Messages, messages);
        FillMissingToolResults(messages);

        var openaiReq = BuildBaseRequest(req, resolved, messages, policy);
        return (openaiReq, resolved);
    }

    public ChatCompletionRequest TranslateCountTokens(AnthropicCountTokensRequest req, ProxyConfig? snapshot = null)
    {
        var effectiveConfig = snapshot ?? config;
        var resolved = effectiveConfig.ModelAliases.GetValueOrDefault(req.Model, effectiveConfig.DefaultModel);
        var messages = new List<ChatMessage>();
        BuildSystemMessage(req.System, messages);

        var adaptedMessages = new List<AnthropicMessage>();
        // For count_tokens, convert messages similarly
        foreach (var msg in req.Messages)
        {
            adaptedMessages.Add(msg);
        }
        BuildMessages(adaptedMessages, messages);
        FillMissingToolResults(messages);

        return new ChatCompletionRequest
        {
            Model = resolved,
            Messages = messages,
            Tools = ConvertTools(req.Tools),
            ToolChoice = ConvertToolChoice(req.ToolChoice, req.Tools),
        };
    }

    private void BuildSystemMessage(JsonNode? system, List<ChatMessage> messages)
    {
        if (system is null) return;

        string? content = null;
        if (system is JsonValue sysVal && sysVal.TryGetValue<string>(out var sysStr))
        {
            content = sysStr;
        }
        else if (system is JsonArray sysArr)
        {
            var parts = new List<string>();
            foreach (var blockNode in sysArr)
            {
                if (blockNode is not JsonObject block) continue;
                var type = block["type"]?.GetValue<string>();
                if (type != "text") continue;
                var text = block["text"]?.GetValue<string>();
                if (string.IsNullOrEmpty(text)) continue;
                if (text.StartsWith("x-anthropic-billing-header", StringComparison.OrdinalIgnoreCase)) continue;
                parts.Add(text);
                WarnStrip("cache_control", block);
            }
            content = string.Join("\n", parts);
        }

        if (!string.IsNullOrEmpty(content))
            messages.Add(MakeStringMessage("system", content));
    }

    private void BuildMessages(List<AnthropicMessage> anthropicMsgs, List<ChatMessage> messages)
    {
        foreach (var msg in anthropicMsgs)
        {
            if (msg.Content is JsonValue msgVal && msgVal.TryGetValue<string>(out var msgStr))
            {
                messages.Add(MakeStringMessage(msg.Role, msgStr));
                continue;
            }

            var contentParts = new List<ChatContentPart>();
            var toolCalls = new List<ToolCall>();
            var reasoningParts = new List<string>();
            var extraMessages = new List<ChatMessage>();

            foreach (var blockNode in msg.Content.AsArray())
            {
                if (blockNode is not JsonObject block) continue;
                var type = block["type"]?.GetValue<string>();

                switch (type)
                {
                    case "text":
                        var text = block["text"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(text))
                            contentParts.Add(new ChatContentPart { Type = "text", Text = text });
                        WarnStrip("cache_control", block);
                        break;

                    case "image":
                        if (block["source"] is JsonNode srcNode)
                            contentParts.Add(new ChatContentPart
                            {
                                Type = "image_url",
                                ImageUrl = new ChatImageUrl { Url = ConvertImageSource(srcNode) }
                            });
                        break;

                    case "tool_use" when msg.Role == "assistant":
                        var toolId = block["id"]?.GetValue<string>() ?? $"call_{Guid.NewGuid():N}";
                        var toolName = block["name"]?.GetValue<string>() ?? "";
                        var inputJson = block["input"] is JsonNode inp
                            ? inp.ToJsonString()
                            : "{}";
                        toolCalls.Add(new ToolCall
                        {
                            Id = toolId,
                            Function = new ToolCallFunction { Name = toolName, Arguments = inputJson }
                        });
                        break;

                    case "tool_result" when msg.Role == "user":
                        ConvertToolResult(block, extraMessages);
                        break;

                    case "thinking" when msg.Role == "assistant":
                        if (block["thinking"]?.GetValue<string>() is { } thinkText)
                            reasoningParts.Add(thinkText);
                        break;

                    case "redacted_thinking":
                        break; // drop silently

                    case "tool_reference":
                        break; // handled inside tool_result
                }
            }

            // Emit primary message
            if (msg.Role == "assistant")
            {
                var assistantMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = contentParts.Count > 0
                        ? JsonSerializer.SerializeToNode(contentParts, AppSerializerContext.Default.ListChatContentPart)
                        : null,
                    ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
                    ReasoningContent = reasoningParts.Count > 0 ? string.Join("\n", reasoningParts) : null,
                };
                messages.Add(assistantMsg);
            }
            else if (msg.Role == "user")
            {
                if (contentParts.Count > 0)
                {
                    messages.Add(contentParts.Count == 1 && contentParts[0].Type == "text"
                        ? MakeStringMessage("user", contentParts[0].Text!)
                        : new ChatMessage
                        {
                            Role = "user",
                            Content = JsonSerializer.SerializeToNode(contentParts, AppSerializerContext.Default.ListChatContentPart)
                        });
                }
                messages.AddRange(extraMessages);
            }
        }
    }

    private static void FillMissingToolResults(List<ChatMessage> messages)
    {
        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (msg.Role != "assistant" || msg.ToolCalls is not { Count: > 0 }) continue;

            var coveredIds = new HashSet<string>();
            for (int j = i + 1; j < messages.Count && messages[j].Role == "tool"; j++)
                if (messages[j].ToolCallId is not null)
                    coveredIds.Add(messages[j].ToolCallId!);

            int insertAt = i + 1;
            while (insertAt < messages.Count && messages[insertAt].Role == "tool")
                insertAt++;

            foreach (var tc in msg.ToolCalls)
            {
                if (!coveredIds.Contains(tc.Id))
                {
                    messages.Insert(insertAt, new ChatMessage
                    {
                        Role = "tool",
                        ToolCallId = tc.Id,
                        Content = JsonValue.Create("[No result available]"),
                    });
                    insertAt++;
                }
            }
        }
    }

    private static void ConvertToolResult(JsonObject block, List<ChatMessage> extraMessages)
    {
        var toolUseId = block["tool_use_id"]?.GetValue<string>() ?? "";

        var contentNode = block["content"];
        if (contentNode is null)
        {
            extraMessages.Add(new ChatMessage { Role = "tool", ToolCallId = toolUseId, Content = MakeStringNode("") });
            return;
        }

        if (contentNode is JsonValue contentVal && contentVal.TryGetValue<string>(out var contentStr))
        {
            extraMessages.Add(new ChatMessage { Role = "tool", ToolCallId = toolUseId, Content = MakeStringNode(contentStr) });
            return;
        }

        // list of sub-blocks
        var textParts = new List<string>();
        foreach (var itemNode in contentNode.AsArray())
        {
            if (itemNode is not JsonObject item) continue;
            var itemType = item["type"]?.GetValue<string>();
            switch (itemType)
            {
                case "text":
                    textParts.Add(item["text"]?.GetValue<string>() ?? "");
                    break;
                case "image":
                    if (item["source"] is JsonNode srcNode)
                    {
                        var url = ConvertImageSource(srcNode);
                        var imgPart = new ChatContentPart
                        {
                            Type = "image_url",
                            ImageUrl = new ChatImageUrl { Url = url }
                        };
                        extraMessages.Add(new ChatMessage
                        {
                            Role = "user",
                            Content = JsonSerializer.SerializeToNode(
                                new List<ChatContentPart> { imgPart },
                                AppSerializerContext.Default.ListChatContentPart)
                        });
                    }
                    break;
            }
        }

        var toolText = string.Join("", textParts);
        extraMessages.Insert(0, new ChatMessage { Role = "tool", ToolCallId = toolUseId, Content = MakeStringNode(toolText) });
    }

    private ChatCompletionRequest BuildBaseRequest(
        AnthropicMessagesRequest req,
        string resolved,
        List<ChatMessage> messages,
        string policy)
    {
        WarnTopK(req.TopK);
        WarnServiceTier(req.ServiceTier);

        var stopSeqs = req.StopSequences;
        if (stopSeqs?.Count > 4)
        {
            WarnOnce("stop_sequences_truncated", "stop_sequences has >4 entries; truncating to 4");
            stopSeqs = stopSeqs.Take(4).ToList();
        }

        string? reasoningEffort = null;
        if (policy == "effort")
        {
            reasoningEffort = req.Thinking?.Type == "enabled" && req.Thinking.BudgetTokens.HasValue
                ? req.Thinking.BudgetTokens.Value < 2000 ? "low"
                    : req.Thinking.BudgetTokens.Value <= 8000 ? "medium"
                    : "high"
                : "medium";
        }

        return new ChatCompletionRequest
        {
            Model = resolved,
            Messages = messages,
            Temperature = req.Temperature,
            TopP = req.TopP,
            MaxTokens = req.MaxTokens,
            MaxCompletionTokens = req.MaxTokens,
            Stop = stopSeqs,
            Stream = req.Stream,
            StreamOptions = req.Stream == true ? new StreamOptions { IncludeUsage = true } : null,
            Tools = ConvertTools(req.Tools),
            ToolChoice = ConvertToolChoice(req.ToolChoice, req.Tools),
            ParallelToolCalls = (req.DisableParallelToolUse == true || req.ToolChoice?.DisableParallelToolUse == true) ? false : null,
            User = req.Metadata?.UserId,
            ReasoningEffort = reasoningEffort,
        };
    }

    private List<ChatTool>? ConvertTools(List<AnthropicTool>? tools)
    {
        if (tools is null || tools.Count == 0) return null;
        return tools.Select(t => new ChatTool
        {
            Function = new ChatFunction
            {
                Name = t.Name,
                Description = string.IsNullOrEmpty(t.Description) ? t.Name : t.Description,
                Parameters = t.InputSchema ?? JsonNode.Parse("{}"),
            }
        }).ToList();
    }

    private JsonNode? ConvertToolChoice(AnthropicToolChoice? tc, List<AnthropicTool>? tools)
    {
        if (tc is null) return null;

        return tc.Type switch
        {
            "auto" => MakeStringNode("auto"),
            "any" => MakeStringNode("required"),
            "none" => MakeStringNode("none"),
            "tool" => JsonSerializer.SerializeToNode(
                new { type = "function", function = new { name = tc.Name } }),
            _ => null,
        };
    }

    private static string ConvertImageSource(JsonNode source)
    {
        var srcType = (source as JsonObject)?["type"]?.GetValue<string>();
        if (srcType == "url")
            return (source as JsonObject)?["url"]?.GetValue<string>() ?? "";

        var mediaType = (source as JsonObject)?["media_type"]?.GetValue<string>() ?? "image/jpeg";
        var data = (source as JsonObject)?["data"]?.GetValue<string>() ?? "";
        return $"data:{mediaType};base64,{data}";
    }

    private static ChatMessage MakeStringMessage(string role, string content) =>
        new() { Role = role, Content = MakeStringNode(content) };

    private static JsonNode MakeStringNode(string value) =>
        JsonValue.Create(value)!;

    private void WarnStrip(string field, JsonObject block)
    {
        if (block.ContainsKey("cache_control"))
            WarnOnce("strip:cache_control", "cache_control markers stripped (not supported by Foundry)");
    }

    private void WarnTopK(int? topK)
    {
        if (topK.HasValue)
            WarnOnce("strip:top_k", "top_k stripped (not supported by OpenAI Chat Completions)");
    }

    private void WarnServiceTier(string? st)
    {
        if (st is not null)
            WarnOnce("strip:service_tier", "service_tier stripped (not supported by Foundry)");
    }

    private void WarnOnce(string key, string message)
    {
        if (_warnedFields.TryAdd(key, 0))
            logger.LogWarning("{Message}", message);
    }
}

// Shim so ConvertToolResult static helper can use source-gen context
file static class AppSerializerContext
{
    public static Protocol.AppJsonSerializerContext Default => Protocol.AppJsonSerializerContext.Default;
}
