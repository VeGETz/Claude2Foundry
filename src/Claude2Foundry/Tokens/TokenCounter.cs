using System.Text.Json;
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

        if (req.System.HasValue)
        {
            var sys = req.System.Value;
            if (sys.ValueKind == JsonValueKind.String)
                parts.Add(sys.GetString() ?? "");
            else if (sys.ValueKind == JsonValueKind.Array)
                foreach (var b in sys.EnumerateArray())
                    if (b.TryGetProperty("type", out var t) && t.GetString() == "text"
                        && b.TryGetProperty("text", out var tx))
                        parts.Add(tx.GetString() ?? "");
        }

        foreach (var tool in req.Tools ?? [])
        {
            parts.Add($"{tool.Name}\n{tool.Description ?? ""}\n{JsonSerializer.Serialize(tool.InputSchema)}");
        }

        foreach (var msg in req.Messages)
        {
            if (msg.Content.ValueKind == JsonValueKind.String)
            {
                parts.Add($"{msg.Role}: {msg.Content.GetString()}");
                continue;
            }

            foreach (var block in msg.Content.EnumerateArray())
            {
                var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (type)
                {
                    case "text":
                        if (block.TryGetProperty("text", out var tx)) parts.Add(tx.GetString() ?? "");
                        break;
                    case "image":
                        imageCount++;
                        break;
                    case "tool_use":
                        var name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var inp = block.TryGetProperty("input", out var i) ? JsonSerializer.Serialize(i) : "{}";
                        parts.Add($"tool_use:{name}({inp})");
                        break;
                    case "tool_result":
                        (var text, var imgs) = ExtractToolResult(block);
                        parts.Add(text);
                        imageCount += imgs;
                        break;
                    case "thinking":
                        if (block.TryGetProperty("thinking", out var th)) parts.Add(th.GetString() ?? "");
                        break;
                    case "tool_reference":
                        if (block.TryGetProperty("name", out var rn)) parts.Add(rn.GetString() ?? "");
                        break;
                }
            }
        }

        return (string.Join("\n", parts), imageCount);
    }

    private static (string Text, int Images) ExtractToolResult(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content))
            return ("", 0);

        if (content.ValueKind == JsonValueKind.String)
            return (content.GetString() ?? "", 0);

        var texts = new List<string>();
        var imgs = 0;
        foreach (var item in content.EnumerateArray())
        {
            var t = item.TryGetProperty("type", out var tp) ? tp.GetString() : null;
            if (t == "text" && item.TryGetProperty("text", out var tx)) texts.Add(tx.GetString() ?? "");
            else if (t == "image") imgs++;
        }
        return (string.Join("", texts), imgs);
    }
}
