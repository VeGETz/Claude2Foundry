using System.Text.Json;
using Claude2Foundry.Config;
using Claude2Foundry.Protocol.Anthropic;
using Claude2Foundry.Translation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Claude2Foundry.Tests.Translation;

public class RequestTranslatorTests
{
    private static RequestTranslator BuildTranslator(
        Dictionary<string, string>? aliases = null,
        Dictionary<string, string>? policies = null)
    {
        var cfg = new ProxyConfig
        {
            BackendUrl = "https://test.openai.azure.com/openai/v1/",
            ApiKeyEnv = "FOUNDRY_API_KEY",
            DefaultModel = "DeepSeek-V3",
            ModelAliases = aliases ?? new() { ["claude-sonnet-4-6"] = "DeepSeek-V3" },
            ReasoningPolicies = policies ?? new() { ["DeepSeek-V3"] = "none" },
        };
        return new RequestTranslator(cfg, NullLogger<RequestTranslator>.Instance);
    }

    private static JsonElement StringEl(string s) =>
        JsonSerializer.Deserialize<JsonElement>($"\"{s.Replace("\"", "\\\"")}\"");

    private static AnthropicMessagesRequest SimpleReq(string? systemText = null)
    {
        JsonElement? sys = systemText is not null ? StringEl(systemText) : null;
        return new AnthropicMessagesRequest
        {
            Model = "claude-sonnet-4-6",
            Messages = [new AnthropicMessage { Role = "user", Content = StringEl("Hello") }],
            MaxTokens = 100,
            System = sys,
        };
    }

    [Fact]
    public void SimpleTextMessage_Translated()
    {
        var t = BuildTranslator();
        var (req, target) = t.Translate(SimpleReq());

        Assert.Equal("DeepSeek-V3", req.Model);
        Assert.Equal("DeepSeek-V3", target);
        Assert.Single(req.Messages);
        Assert.Equal("user", req.Messages[0].Role);
    }

    [Fact]
    public void SystemAsString_BecomesFirstMessage()
    {
        var t = BuildTranslator();
        var (req, _) = t.Translate(SimpleReq("You are helpful"));

        Assert.Equal(2, req.Messages.Count);
        Assert.Equal("system", req.Messages[0].Role);
    }

    [Fact]
    public void SystemAsBlockList_ConcatenatesText()
    {
        var t = BuildTranslator();
        var sysBlocks = JsonSerializer.Deserialize<JsonElement>(
            """[{"type":"text","text":"Block one"},{"type":"text","text":"Block two"}]""");
        var req = new AnthropicMessagesRequest
        {
            Model = "claude-sonnet-4-6",
            Messages = [new AnthropicMessage { Role = "user", Content = StringEl("Hi") }],
            MaxTokens = 100,
            System = sysBlocks,
        };
        var (openaiReq, _) = t.Translate(req);

        Assert.Equal(2, openaiReq.Messages.Count);
        var sysContent = openaiReq.Messages[0].Content!.Value.GetString();
        Assert.Contains("Block one", sysContent);
        Assert.Contains("Block two", sysContent);
    }

    [Fact]
    public void SystemBillingHeaderSkipped()
    {
        var t = BuildTranslator();
        var sysBlocks = JsonSerializer.Deserialize<JsonElement>(
            """[{"type":"text","text":"x-anthropic-billing-header: abc123"},{"type":"text","text":"Real prompt"}]""");
        var req = new AnthropicMessagesRequest
        {
            Model = "claude-sonnet-4-6",
            Messages = [new AnthropicMessage { Role = "user", Content = StringEl("Hi") }],
            MaxTokens = 100,
            System = sysBlocks,
        };
        var (openaiReq, _) = t.Translate(req);
        var sysContent = openaiReq.Messages[0].Content!.Value.GetString();
        Assert.DoesNotContain("x-anthropic-billing-header", sysContent);
        Assert.Contains("Real prompt", sysContent);
    }

    [Fact]
    public void ToolUseBlock_BecomesToolCall()
    {
        var t = BuildTranslator();
        var content = JsonSerializer.Deserialize<JsonElement>(
            """[{"type":"tool_use","id":"call_abc","name":"get_weather","input":{"city":"NYC"}}]""");
        var req = new AnthropicMessagesRequest
        {
            Model = "claude-sonnet-4-6",
            Messages = [new AnthropicMessage { Role = "assistant", Content = content }],
            MaxTokens = 100,
        };
        var (openaiReq, _) = t.Translate(req);

        Assert.NotNull(openaiReq.Messages[0].ToolCalls);
        Assert.Single(openaiReq.Messages[0].ToolCalls!);
        Assert.Equal("call_abc", openaiReq.Messages[0].ToolCalls![0].Id);
        Assert.Equal("get_weather", openaiReq.Messages[0].ToolCalls![0].Function.Name);
    }

    [Fact]
    public void ToolResultBlock_BecomesToolMessage()
    {
        var t = BuildTranslator();
        var content = JsonSerializer.Deserialize<JsonElement>(
            """[{"type":"tool_result","tool_use_id":"call_abc","content":"Sunny, 72F"}]""");
        var req = new AnthropicMessagesRequest
        {
            Model = "claude-sonnet-4-6",
            Messages = [new AnthropicMessage { Role = "user", Content = content }],
            MaxTokens = 100,
        };
        var (openaiReq, _) = t.Translate(req);

        var toolMsg = openaiReq.Messages.FirstOrDefault(m => m.Role == "tool");
        Assert.NotNull(toolMsg);
        Assert.Equal("call_abc", toolMsg!.ToolCallId);
    }

    [Fact]
    public void ThinkingBlock_BecomesReasoningContent()
    {
        var t = BuildTranslator();
        var content = JsonSerializer.Deserialize<JsonElement>(
            """[{"type":"thinking","thinking":"Let me think..."},{"type":"text","text":"Answer"}]""");
        var req = new AnthropicMessagesRequest
        {
            Model = "claude-sonnet-4-6",
            Messages = [new AnthropicMessage { Role = "assistant", Content = content }],
            MaxTokens = 100,
        };
        var (openaiReq, _) = t.Translate(req);

        Assert.Equal("Let me think...", openaiReq.Messages[0].ReasoningContent);
    }

    [Fact]
    public void ToolChoiceAny_MapsToRequired()
    {
        var t = BuildTranslator();
        var req = new AnthropicMessagesRequest
        {
            Model = "claude-sonnet-4-6",
            Messages = [new AnthropicMessage { Role = "user", Content = StringEl("Use a tool") }],
            MaxTokens = 100,
            ToolChoice = new AnthropicToolChoice { Type = "any" },
            Tools = [new AnthropicTool { Name = "test", InputSchema = JsonDocument.Parse("{}").RootElement }],
        };
        var (openaiReq, _) = t.Translate(req);

        Assert.NotNull(openaiReq.ToolChoice);
        Assert.Equal("required", openaiReq.ToolChoice!.Value.GetString());
    }

    [Fact]
    public void ToolChoiceNone_MapsToNone()
    {
        var t = BuildTranslator();
        var req = new AnthropicMessagesRequest
        {
            Model = "claude-sonnet-4-6",
            Messages = [new AnthropicMessage { Role = "user", Content = StringEl("Hi") }],
            MaxTokens = 100,
            ToolChoice = new AnthropicToolChoice { Type = "none" },
        };
        var (openaiReq, _) = t.Translate(req);
        Assert.Equal("none", openaiReq.ToolChoice!.Value.GetString());
    }

    [Fact]
    public void ReasoningPolicy_Effort_LowBudget()
    {
        var t = BuildTranslator(
            aliases: new() { ["claude-opus-4-7"] = "o4-mini" },
            policies: new() { ["o4-mini"] = "effort" });
        var req = new AnthropicMessagesRequest
        {
            Model = "claude-opus-4-7",
            Messages = [new AnthropicMessage { Role = "user", Content = StringEl("Think") }],
            MaxTokens = 100,
            Thinking = new AnthropicThinkingConfig { Type = "enabled", BudgetTokens = 1000 },
        };
        var (openaiReq, _) = t.Translate(req);
        Assert.Equal("low", openaiReq.ReasoningEffort);
    }

    [Fact]
    public void ReasoningPolicy_Effort_MediumBudget()
    {
        var t = BuildTranslator(
            aliases: new() { ["claude-opus-4-7"] = "o4-mini" },
            policies: new() { ["o4-mini"] = "effort" });
        var req = new AnthropicMessagesRequest
        {
            Model = "claude-opus-4-7",
            Messages = [new AnthropicMessage { Role = "user", Content = StringEl("Think") }],
            MaxTokens = 100,
            Thinking = new AnthropicThinkingConfig { Type = "enabled", BudgetTokens = 5000 },
        };
        var (openaiReq, _) = t.Translate(req);
        Assert.Equal("medium", openaiReq.ReasoningEffort);
    }

    [Fact]
    public void ReasoningPolicy_Effort_HighBudget()
    {
        var t = BuildTranslator(
            aliases: new() { ["claude-opus-4-7"] = "o4-mini" },
            policies: new() { ["o4-mini"] = "effort" });
        var req = new AnthropicMessagesRequest
        {
            Model = "claude-opus-4-7",
            Messages = [new AnthropicMessage { Role = "user", Content = StringEl("Think") }],
            MaxTokens = 100,
            Thinking = new AnthropicThinkingConfig { Type = "enabled", BudgetTokens = 20000 },
        };
        var (openaiReq, _) = t.Translate(req);
        Assert.Equal("high", openaiReq.ReasoningEffort);
    }

    [Fact]
    public void ReasoningPolicy_None_NoReasoningEffort()
    {
        var t = BuildTranslator();
        var req = new AnthropicMessagesRequest
        {
            Model = "claude-sonnet-4-6",
            Messages = [new AnthropicMessage { Role = "user", Content = StringEl("Hi") }],
            MaxTokens = 100,
            Thinking = new AnthropicThinkingConfig { Type = "enabled", BudgetTokens = 5000 },
        };
        var (openaiReq, _) = t.Translate(req);
        Assert.Null(openaiReq.ReasoningEffort);
    }

    [Fact]
    public void ReasoningPolicy_Effort_NoThinkingBlock_DefaultsMedium()
    {
        var t = BuildTranslator(
            aliases: new() { ["claude-opus-4-7"] = "o4-mini" },
            policies: new() { ["o4-mini"] = "effort" });
        var req = new AnthropicMessagesRequest
        {
            Model = "claude-opus-4-7",
            Messages = [new AnthropicMessage { Role = "user", Content = StringEl("Hi") }],
            MaxTokens = 100,
        };
        var (openaiReq, _) = t.Translate(req);
        Assert.Equal("medium", openaiReq.ReasoningEffort);
    }

    [Fact]
    public void DisableParallelToolUse_SetsParallelFalse()
    {
        var t = BuildTranslator();
        var req = new AnthropicMessagesRequest
        {
            Model = "claude-sonnet-4-6",
            Messages = [new AnthropicMessage { Role = "user", Content = StringEl("Hi") }],
            MaxTokens = 100,
            DisableParallelToolUse = true,
        };
        var (openaiReq, _) = t.Translate(req);
        Assert.Equal(false, openaiReq.ParallelToolCalls);
    }

    [Fact]
    public void StopSequences_TruncatedAt4()
    {
        var t = BuildTranslator();
        var req = new AnthropicMessagesRequest
        {
            Model = "claude-sonnet-4-6",
            Messages = [new AnthropicMessage { Role = "user", Content = StringEl("Hi") }],
            MaxTokens = 100,
            StopSequences = ["a", "b", "c", "d", "e"],
        };
        var (openaiReq, _) = t.Translate(req);
        Assert.Equal(4, openaiReq.Stop!.Count);
    }

    [Fact]
    public void MaxTokens_SetOnBothFields()
    {
        var t = BuildTranslator();
        var (req, _) = t.Translate(SimpleReq());
        Assert.Equal(100, req.MaxTokens);
        Assert.Equal(100, req.MaxCompletionTokens);
    }

    [Fact]
    public void Translate_UsesSnapshot_WhenProvided()
    {
        var constructorCfg = new ProxyConfig
        {
            BackendUrl = "https://a.openai.azure.com/openai/v1/",
            ApiKeyEnv = "FOUNDRY_API_KEY",
            DefaultModel = "model-from-constructor",
        };
        var snapshotCfg = new ProxyConfig
        {
            BackendUrl = "https://a.openai.azure.com/openai/v1/",
            ApiKeyEnv = "FOUNDRY_API_KEY",
            DefaultModel = "model-from-snapshot",
        };

        var t = new RequestTranslator(constructorCfg, NullLogger<RequestTranslator>.Instance);
        var req = new AnthropicMessagesRequest
        {
            Model = "no-alias-match",
            Messages = [new AnthropicMessage { Role = "user", Content = StringEl("hi") }],
            MaxTokens = 10,
        };

        var (result, target) = t.Translate(req, snapshotCfg);

        Assert.Equal("model-from-snapshot", result.Model);
        Assert.Equal("model-from-snapshot", target);
    }
}
