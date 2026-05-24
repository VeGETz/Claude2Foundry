using System.Text.Json;
using System.Text.Json.Nodes;
using Claude2Foundry.Config;
using Claude2Foundry.Protocol.Anthropic;

namespace Claude2Foundry.Tokens;

public sealed class TokenCounter(ProxyConfig config)
{
    public int Count(AnthropicCountTokensRequest req)
    {
        var target = config.ModelAliases.GetValueOrDefault(req.Model, config.DefaultModel);
        var tokCfg = config.Tokenizers.GetValueOrDefault(target, new TokenizerConfig { Source = "TiktokenCl100k" });
        var tokenizer = TokenizerRegistry.Get(tokCfg);

        var (corpus, imageCount) = BuildCorpus(req);
        return tokenizer.CountTokens(corpus) + imageCount * 1500;
    }

    private static (string Corpus, int ImageCount) BuildCorpus(AnthropicCountTokensRequest req)
    {
        var parts = new List<string>();
        var imageCount = 0;

        if (req.System is not null)
        {
            var sys = req.System;
            if (sys is JsonValue sysVal && sysVal.TryGetValue<string>(out var sysStr))
                parts.Add(sysStr);
            else if (sys is JsonArray sysArr)
                foreach (var bNode in sysArr)
                    if (bNode is JsonObject b
                        && b["type"]?.GetValue<string>() == "text"
                        && b["text"]?.GetValue<string>() is { } tx)
                        parts.Add(tx);
        }

        foreach (var tool in req.Tools ?? [])
        {
            parts.Add($"{tool.Name}\n{tool.Description ?? ""}\n{tool.InputSchema?.ToJsonString() ?? "{}"}");
        }

        foreach (var msg in req.Messages)
        {
            if (msg.Content is JsonValue msgVal && msgVal.TryGetValue<string>(out var msgStr))
            {
                parts.Add($"{msg.Role}: {msgStr}");
                continue;
            }

            foreach (var blockNode in msg.Content.AsArray())
            {
                if (blockNode is not JsonObject block) continue;
                var type = block["type"]?.GetValue<string>();
                switch (type)
                {
                    case "text":
                        parts.Add(block["text"]?.GetValue<string>() ?? "");
                        break;
                    case "image":
                        imageCount++;
                        break;
                    case "tool_use":
                        var name = block["name"]?.GetValue<string>() ?? "";
                        var inp = block["input"]?.ToJsonString() ?? "{}";
                        parts.Add($"tool_use:{name}({inp})");
                        break;
                    case "tool_result":
                        (var text, var imgs) = ExtractToolResult(block);
                        parts.Add(text);
                        imageCount += imgs;
                        break;
                    case "thinking":
                        parts.Add(block["thinking"]?.GetValue<string>() ?? "");
                        break;
                    case "tool_reference":
                        parts.Add(block["name"]?.GetValue<string>() ?? "");
                        break;
                }
            }
        }

        return (string.Join("\n", parts), imageCount);
    }

    private static (string Text, int Images) ExtractToolResult(JsonObject block)
    {
        var contentNode = block["content"];
        if (contentNode is null)
            return ("", 0);

        if (contentNode is JsonValue contentVal && contentVal.TryGetValue<string>(out var contentStr))
            return (contentStr, 0);

        var texts = new List<string>();
        var imgs = 0;
        foreach (var itemNode in contentNode.AsArray())
        {
            if (itemNode is not JsonObject item) continue;
            var t = item["type"]?.GetValue<string>();
            if (t == "text") texts.Add(item["text"]?.GetValue<string>() ?? "");
            else if (t == "image") imgs++;
        }
        return (string.Join("", texts), imgs);
    }
}
