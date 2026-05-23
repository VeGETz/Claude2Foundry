using System.Collections.Concurrent;
using System.Text.Json;
using Claude2Foundry.Config;
using Claude2Foundry.Protocol.Anthropic;
using Claude2Foundry.Protocol.OpenAI;
using Microsoft.Extensions.Logging;

namespace Claude2Foundry.Translation;

public sealed class RequestTranslator(ProxyConfig config, ILogger<RequestTranslator> logger)
{
    private static readonly ConcurrentDictionary<string, byte> _warnedFields = new();

    public (ChatCompletionRequest Request, string ResolvedTarget) Translate(AnthropicMessagesRequest req)
    {
        var resolved = config.ModelAliases.GetValueOrDefault(req.Model, config.DefaultModel);
        logger.LogInformation("model={Original}->{Resolved}", req.Model, resolved);

        var policy = config.ReasoningPolicies.GetValueOrDefault(resolved, "none");
        var messages = new List<ChatMessage>();

        BuildSystemMessage(req.System, messages);
        BuildMessages(req.Messages, messages);
        FillMissingToolResults(messages);

        var openaiReq = BuildBaseRequest(req, resolved, messages, policy);
        return (openaiReq, resolved);
    }

    public ChatCompletionRequest TranslateCountTokens(AnthropicCountTokensRequest req)
    {
        var resolved = config.ModelAliases.GetValueOrDefault(req.Model, config.DefaultModel);
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

    private void BuildSystemMessage(JsonElement? system, List<ChatMessage> messages)
    {
        if (system is null) return;

        string? content = null;
        if (system.Value.ValueKind == JsonValueKind.String)
        {
            content = system.Value.GetString();
        }
        else if (system.Value.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var block in system.Value.EnumerateArray())
            {
                var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type != "text") continue;
                var text = block.TryGetProperty("text", out var tx) ? tx.GetString() : null;
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
            if (msg.Content.ValueKind == JsonValueKind.String)
            {
                messages.Add(MakeStringMessage(msg.Role, msg.Content.GetString()!));
                continue;
            }

            var contentParts = new List<ChatContentPart>();
            var toolCalls = new List<ToolCall>();
            var reasoningParts = new List<string>();
            var extraMessages = new List<ChatMessage>();

            foreach (var block in msg.Content.EnumerateArray())
            {
                var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;

                switch (type)
                {
                    case "text":
                        var text = block.TryGetProperty("text", out var tx) ? tx.GetString() : null;
                        if (!string.IsNullOrEmpty(text))
                            contentParts.Add(new ChatContentPart { Type = "text", Text = text });
                        WarnStrip("cache_control", block);
                        break;

                    case "image":
                        if (block.TryGetProperty("source", out var src))
                            contentParts.Add(new ChatContentPart
                            {
                                Type = "image_url",
                                ImageUrl = new ChatImageUrl { Url = ConvertImageSource(src) }
                            });
                        break;

                    case "tool_use" when msg.Role == "assistant":
                        var toolId = block.TryGetProperty("id", out var tid) ? tid.GetString() ?? $"call_{Guid.NewGuid():N}" : $"call_{Guid.NewGuid():N}";
                        var toolName = block.TryGetProperty("name", out var tn) ? tn.GetString() ?? "" : "";
                        var inputJson = block.TryGetProperty("input", out var inp)
                            ? JsonSerializer.Serialize(inp)
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
                        if (block.TryGetProperty("thinking", out var th) && th.GetString() is { } thinkText)
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
                        ? JsonSerializer.SerializeToElement(contentParts, AppSerializerContext.Default.ListChatContentPart)
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
                            Content = JsonSerializer.SerializeToElement(contentParts, AppSerializerContext.Default.ListChatContentPart)
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
                        Content = JsonDocument.Parse("\"[No result available]\"").RootElement,
                    });
                    insertAt++;
                }
            }
        }
    }

    private static void ConvertToolResult(JsonElement block, List<ChatMessage> extraMessages)
    {
        var toolUseId = block.TryGetProperty("tool_use_id", out var tuid) ? tuid.GetString() ?? "" : "";

        if (!block.TryGetProperty("content", out var content))
        {
            extraMessages.Add(new ChatMessage { Role = "tool", ToolCallId = toolUseId, Content = MakeStringElement("") });
            return;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            extraMessages.Add(new ChatMessage { Role = "tool", ToolCallId = toolUseId, Content = MakeStringElement(content.GetString()!) });
            return;
        }

        // list of sub-blocks
        var textParts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            var itemType = item.TryGetProperty("type", out var it) ? it.GetString() : null;
            switch (itemType)
            {
                case "text":
                    if (item.TryGetProperty("text", out var txt))
                        textParts.Add(txt.GetString() ?? "");
                    break;
                case "image":
                    if (item.TryGetProperty("source", out var src))
                    {
                        var url = ConvertImageSource(src);
                        var imgPart = new ChatContentPart
                        {
                            Type = "image_url",
                            ImageUrl = new ChatImageUrl { Url = url }
                        };
                        extraMessages.Add(new ChatMessage
                        {
                            Role = "user",
                            Content = JsonSerializer.SerializeToElement(
                                new List<ChatContentPart> { imgPart },
                                AppSerializerContext.Default.ListChatContentPart)
                        });
                    }
                    break;
            }
        }

        var toolText = string.Join("", textParts);
        extraMessages.Insert(0, new ChatMessage { Role = "tool", ToolCallId = toolUseId, Content = MakeStringElement(toolText) });
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
                Parameters = t.InputSchema.ValueKind == System.Text.Json.JsonValueKind.Undefined
                    ? JsonDocument.Parse("{}").RootElement
                    : t.InputSchema,
            }
        }).ToList();
    }

    private JsonElement? ConvertToolChoice(AnthropicToolChoice? tc, List<AnthropicTool>? tools)
    {
        if (tc is null) return null;

        return tc.Type switch
        {
            "auto" => MakeStringElement("auto"),
            "any" => MakeStringElement("required"),
            "none" => MakeStringElement("none"),
            "tool" => JsonSerializer.SerializeToElement(
                new { type = "function", function = new { name = tc.Name } }),
            _ => null,
        };
    }

    private static string ConvertImageSource(JsonElement source)
    {
        var srcType = source.TryGetProperty("type", out var st) ? st.GetString() : null;
        if (srcType == "url")
            return source.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";

        var mediaType = source.TryGetProperty("media_type", out var mt) ? mt.GetString() ?? "image/jpeg" : "image/jpeg";
        var data = source.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "";
        return $"data:{mediaType};base64,{data}";
    }

    private static ChatMessage MakeStringMessage(string role, string content) =>
        new() { Role = role, Content = MakeStringElement(content) };

    private static JsonElement MakeStringElement(string value) =>
        JsonDocument.Parse($"\"{JsonEncodedText.Encode(value)}\"").RootElement;

    private void WarnStrip(string field, JsonElement block)
    {
        if (block.TryGetProperty("cache_control", out _))
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
