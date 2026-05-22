using System.Text.Json;
using Claude2Foundry.Config;
using Claude2Foundry.Protocol.Anthropic;
using Claude2Foundry.Protocol.OpenAI;
using Claude2Foundry.Translation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Claude2Foundry.Tests.Translation;

public class ResponseTranslatorTests
{
    private static ResponseTranslator BuildTranslator(string? policy = null)
    {
        var cfg = new ProxyConfig
        {
            BackendUrl = "https://test.openai.azure.com/openai/v1/",
            ApiKeyEnv = "FOUNDRY_API_KEY",
            DefaultModel = "DeepSeek-V3",
            ReasoningPolicies = policy is not null
                ? new() { ["DeepSeek-V3"] = policy }
                : new() { ["DeepSeek-V3"] = "none" },
        };
        return new ResponseTranslator(cfg, NullLogger<ResponseTranslator>.Instance);
    }

    private static AnthropicMessagesRequest FakeRequest() => new()
    {
        Model = "claude-sonnet-4-6",
        Messages = [],
        MaxTokens = 100,
    };

    private static ChatCompletionResponse FakeResponse(
        string? content = "Hello",
        string? reasoningContent = null,
        List<ToolCall>? toolCalls = null,
        string finishReason = "stop") => new()
    {
        Id = "test-id",
        Model = "DeepSeek-V3",
        Usage = new ChatUsage { PromptTokens = 10, CompletionTokens = 20 },
        Choices =
        [
            new ChatChoice
            {
                Index = 0,
                FinishReason = finishReason,
                Message = new AssistantMessage
                {
                    Content = content,
                    ReasoningContent = reasoningContent,
                    ToolCalls = toolCalls,
                }
            }
        ]
    };

    [Fact]
    public void SimpleText_MapsCorrectly()
    {
        var t = BuildTranslator();
        var resp = t.Translate(FakeResponse(), FakeRequest(), "DeepSeek-V3");

        Assert.Equal("claude-sonnet-4-6", resp.Model);
        Assert.Equal("test-id", resp.Id);
        Assert.Equal("end_turn", resp.StopReason);
        Assert.Equal(10, resp.Usage.InputTokens);
        Assert.Equal(20, resp.Usage.OutputTokens);
        Assert.Single(resp.Content);
        Assert.Equal("text", resp.Content[0].Type);
        Assert.Equal("Hello", resp.Content[0].Text);
    }

    [Fact]
    public void StopReason_Length_MapsToMaxTokens()
    {
        var t = BuildTranslator();
        var resp = t.Translate(FakeResponse(finishReason: "length"), FakeRequest(), "DeepSeek-V3");
        Assert.Equal("max_tokens", resp.StopReason);
    }

    [Fact]
    public void StopReason_ToolCalls_MapsToToolUse()
    {
        var t = BuildTranslator();
        var resp = t.Translate(FakeResponse(finishReason: "tool_calls"), FakeRequest(), "DeepSeek-V3");
        Assert.Equal("tool_use", resp.StopReason);
    }

    [Fact]
    public void ReasoningContent_Policy_None_Dropped()
    {
        var t = BuildTranslator("none");
        var resp = t.Translate(FakeResponse(reasoningContent: "Some reasoning"), FakeRequest(), "DeepSeek-V3");
        Assert.DoesNotContain(resp.Content, b => b.Type == "thinking");
    }

    [Fact]
    public void ReasoningContent_Policy_Passthrough_AsThinkingBlock()
    {
        var t = BuildTranslator("passthrough");
        var resp = t.Translate(FakeResponse(reasoningContent: "Some reasoning"), FakeRequest(), "DeepSeek-V3");
        var thinking = resp.Content.FirstOrDefault(b => b.Type == "thinking");
        Assert.NotNull(thinking);
        Assert.Equal("Some reasoning", thinking!.Thinking);
        Assert.NotNull(thinking.Signature);
    }

    [Fact]
    public void ReasoningContent_Policy_Effort_AsThinkingBlock()
    {
        var t = BuildTranslator("effort");
        var resp = t.Translate(FakeResponse(reasoningContent: "Effort reasoning"), FakeRequest(), "DeepSeek-V3");
        var thinking = resp.Content.FirstOrDefault(b => b.Type == "thinking");
        Assert.NotNull(thinking);
        Assert.Equal("Effort reasoning", thinking!.Thinking);
    }

    [Fact]
    public void ToolCalls_AsToolUseBlocks()
    {
        var t = BuildTranslator();
        var toolCalls = new List<ToolCall>
        {
            new() { Id = "call_1", Function = new ToolCallFunction { Name = "test_tool", Arguments = """{"key":"val"}""" } }
        };
        var resp = t.Translate(FakeResponse(toolCalls: toolCalls), FakeRequest(), "DeepSeek-V3");
        var toolBlock = resp.Content.FirstOrDefault(b => b.Type == "tool_use");
        Assert.NotNull(toolBlock);
        Assert.Equal("call_1", toolBlock!.Id);
        Assert.Equal("test_tool", toolBlock.Name);
        Assert.NotNull(toolBlock.Input);
    }

    [Fact]
    public void EmptyChoices_ThrowsAdapterException()
    {
        var t = BuildTranslator();
        var empty = new ChatCompletionResponse
        {
            Id = "x",
            Model = "DeepSeek-V3",
            Usage = new ChatUsage(),
            Choices = [],
        };
        Assert.Throws<Claude2Foundry.Errors.AdapterException>(() =>
            t.Translate(empty, FakeRequest(), "DeepSeek-V3"));
    }

    [Fact]
    public void EmptyContent_EmitsEmptyTextBlock()
    {
        var t = BuildTranslator();
        var resp = t.Translate(FakeResponse(content: null), FakeRequest(), "DeepSeek-V3");
        Assert.Single(resp.Content);
        Assert.Equal("text", resp.Content[0].Type);
        Assert.Equal("", resp.Content[0].Text);
    }

    [Fact]
    public void InvalidToolCallArguments_EmitsEmptyInput()
    {
        var t = BuildTranslator();
        var toolCalls = new List<ToolCall>
        {
            new() { Id = "c1", Function = new ToolCallFunction { Name = "f", Arguments = "not json!!!" } }
        };
        var resp = t.Translate(FakeResponse(toolCalls: toolCalls), FakeRequest(), "DeepSeek-V3");
        var toolBlock = resp.Content.First(b => b.Type == "tool_use");
        Assert.NotNull(toolBlock.Input);
    }
}
