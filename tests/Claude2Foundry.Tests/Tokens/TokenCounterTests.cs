using System.Text.Json;
using Claude2Foundry.Config;
using Claude2Foundry.Protocol.Anthropic;
using Claude2Foundry.Tokens;
using Xunit;

namespace Claude2Foundry.Tests.Tokens;

public class TokenCounterTests
{
    private static TokenCounter BuildCounter() => new(new ProxyConfig
    {
        BackendUrl = "https://test.openai.azure.com/openai/v1/",
        ApiKeyEnv = "FOUNDRY_API_KEY",
        DefaultModel = "DeepSeek-V3",
        ModelAliases = new() { ["claude-sonnet-4-6"] = "DeepSeek-V3" },
        Tokenizers = new() { ["DeepSeek-V3"] = new TokenizerConfig { Source = "TiktokenCl100k" } },
    });

    private static JsonElement StringEl(string s) =>
        JsonSerializer.Deserialize<JsonElement>($"\"{s.Replace("\"", "\\\"")}\"");

    [Fact]
    public void SimpleTextMessages_ReturnsPositiveCount()
    {
        var counter = BuildCounter();
        var req = new AnthropicCountTokensRequest
        {
            Model = "claude-sonnet-4-6",
            Messages =
            [
                new AnthropicMessage { Role = "user", Content = StringEl("Hello, how are you?") }
            ],
        };
        var count = counter.Count(req);
        Assert.True(count > 0);
    }

    [Fact]
    public void EmptyMessages_ReturnsZeroOrSmall()
    {
        var counter = BuildCounter();
        var req = new AnthropicCountTokensRequest
        {
            Model = "claude-sonnet-4-6",
            Messages =
            [
                new AnthropicMessage { Role = "user", Content = StringEl("") }
            ],
        };
        var count = counter.Count(req);
        Assert.True(count >= 0);
    }

    [Fact]
    public void ImageBlock_Counts1500()
    {
        var counter = BuildCounter();
        var content = JsonSerializer.Deserialize<JsonElement>(
            """[{"type":"image","source":{"type":"base64","media_type":"image/jpeg","data":"abc"}}]""");
        var req = new AnthropicCountTokensRequest
        {
            Model = "claude-sonnet-4-6",
            Messages = [new AnthropicMessage { Role = "user", Content = content }],
        };
        var count = counter.Count(req);
        Assert.Equal(1500, count);
    }

    [Fact]
    public void SystemPrompt_IncludedInCount()
    {
        var counter = BuildCounter();
        var withSystem = new AnthropicCountTokensRequest
        {
            Model = "claude-sonnet-4-6",
            Messages = [new AnthropicMessage { Role = "user", Content = StringEl("Hi") }],
            System = StringEl("You are a helpful assistant with a very long system prompt that adds many tokens"),
        };
        var withoutSystem = new AnthropicCountTokensRequest
        {
            Model = "claude-sonnet-4-6",
            Messages = [new AnthropicMessage { Role = "user", Content = StringEl("Hi") }],
        };
        Assert.True(counter.Count(withSystem) > counter.Count(withoutSystem));
    }

    [Fact]
    public void Tools_IncludedInCount()
    {
        var counter = BuildCounter();
        var withTools = new AnthropicCountTokensRequest
        {
            Model = "claude-sonnet-4-6",
            Messages = [new AnthropicMessage { Role = "user", Content = StringEl("Hi") }],
            Tools =
            [
                new AnthropicTool
                {
                    Name = "get_weather",
                    Description = "Get current weather for a location",
                    InputSchema = JsonDocument.Parse("""{"type":"object","properties":{"city":{"type":"string"}}}""").RootElement,
                }
            ],
        };
        var withoutTools = new AnthropicCountTokensRequest
        {
            Model = "claude-sonnet-4-6",
            Messages = [new AnthropicMessage { Role = "user", Content = StringEl("Hi") }],
        };
        Assert.True(counter.Count(withTools) > counter.Count(withoutTools));
    }

    [Fact]
    public void ToolResultWithImage_CountsImageAnd1500()
    {
        var counter = BuildCounter();
        var content = JsonSerializer.Deserialize<JsonElement>("""
            [{"type":"tool_result","tool_use_id":"c1","content":[
                {"type":"text","text":"Result text"},
                {"type":"image","source":{"type":"base64","media_type":"image/png","data":"xyz"}}
            ]}]
            """);
        var req = new AnthropicCountTokensRequest
        {
            Model = "claude-sonnet-4-6",
            Messages = [new AnthropicMessage { Role = "user", Content = content }],
        };
        var count = counter.Count(req);
        // Should include image cost of 1500 plus text tokens
        Assert.True(count >= 1500);
    }

    [Fact]
    public void CountIsDeterministic()
    {
        var counter = BuildCounter();
        var req = new AnthropicCountTokensRequest
        {
            Model = "claude-sonnet-4-6",
            Messages = [new AnthropicMessage { Role = "user", Content = StringEl("The quick brown fox jumps over the lazy dog") }],
        };
        var c1 = counter.Count(req);
        var c2 = counter.Count(req);
        Assert.Equal(c1, c2);
    }
}
