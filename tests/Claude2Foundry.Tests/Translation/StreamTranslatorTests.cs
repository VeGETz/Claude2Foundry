using System.Text.Json;
using Claude2Foundry.Config;
using Claude2Foundry.Protocol.Anthropic;
using Claude2Foundry.Protocol.OpenAI;
using Claude2Foundry.Translation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Claude2Foundry.Tests.Translation;

public class StreamTranslatorTests
{
    private static StreamTranslator BuildTranslator(string policy = "none")
    {
        var cfg = new ProxyConfig
        {
            BackendUrl = "https://test.openai.azure.com/openai/v1/",
            ApiKeyEnv = "FOUNDRY_API_KEY",
            DefaultModel = "target",
            ReasoningPolicies = new() { ["target"] = policy },
        };
        return new StreamTranslator(cfg);
    }

    private static AnthropicMessagesRequest FakeReq() => new()
    {
        Model = "claude-sonnet-4-6",
        Messages = [],
        MaxTokens = 100,
    };

    private static async IAsyncEnumerable<ChatCompletionChunk> Chunks(
        params ChatCompletionChunk[] items)
    {
        foreach (var c in items) yield return c;
    }

    private static ChatCompletionChunk TextChunk(string id, string text, string? finishReason = null) =>
        new()
        {
            Id = id,
            Choices =
            [
                new ChunkChoice
                {
                    Index = 0,
                    Delta = new ChunkDelta { Content = text },
                    FinishReason = finishReason,
                }
            ]
        };

    private static ChatCompletionChunk UsageChunk(string id, int promptTokens, int completionTokens) =>
        new()
        {
            Id = id,
            Choices = [],
            Usage = new ChatUsage { PromptTokens = promptTokens, CompletionTokens = completionTokens },
        };

    private static List<AnthropicStreamEvent> ParseEvents(IEnumerable<string> frames)
    {
        var events = new List<AnthropicStreamEvent>();
        foreach (var frame in frames)
        {
            var lines = frame.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            string? dataLine = null;
            foreach (var line in lines)
                if (line.StartsWith("data:"))
                    dataLine = line[5..].Trim();
            if (dataLine is not null)
            {
                var evt = JsonSerializer.Deserialize<AnthropicStreamEvent>(dataLine);
                if (evt is not null) events.Add(evt);
            }
        }
        return events;
    }

    [Fact]
    public async Task SingleTextChunk_ProducesFullEventSequence()
    {
        var t = BuildTranslator();
        var upstream = Chunks(
            TextChunk("id1", "Hello", "stop"),
            UsageChunk("id1", 5, 10));

        var frames = new List<string>();
        await foreach (var f in t.Translate(upstream, FakeReq(), "target", CancellationToken.None))
            frames.Add(f);

        var events = ParseEvents(frames);
        var types = events.Select(e => e.Type).ToList();

        Assert.Contains("message_start", types);
        Assert.Contains("content_block_start", types);
        Assert.Contains("content_block_delta", types);
        Assert.Contains("content_block_stop", types);
        Assert.Contains("message_delta", types);
        Assert.Contains("message_stop", types);
    }

    [Fact]
    public async Task MultipleTextChunks_AllDeltasEmitted()
    {
        var t = BuildTranslator();
        var upstream = Chunks(
            TextChunk("id1", "Hello"),
            TextChunk("id1", " world", "stop"),
            UsageChunk("id1", 5, 10));

        var frames = new List<string>();
        await foreach (var f in t.Translate(upstream, FakeReq(), "target", CancellationToken.None))
            frames.Add(f);

        var events = ParseEvents(frames);
        var textDeltas = events
            .Where(e => e.Type == "content_block_delta" && e.Delta?.Type == "text_delta")
            .Select(e => e.Delta!.Text)
            .ToList();

        Assert.Equal(2, textDeltas.Count);
        Assert.Equal("Hello", textDeltas[0]);
        Assert.Equal(" world", textDeltas[1]);
    }

    [Fact]
    public async Task ToolCallChunks_ProduceToolUseBlock()
    {
        var t = BuildTranslator();
        var upstream = Chunks(
            new ChatCompletionChunk
            {
                Id = "id1",
                Choices =
                [
                    new ChunkChoice
                    {
                        Index = 0,
                        Delta = new ChunkDelta
                        {
                            ToolCalls =
                            [
                                new ToolCallChunk { Index = 0, Id = "call_abc", Function = new ToolCallChunkFunction { Name = "test_tool", Arguments = "" } }
                            ]
                        }
                    }
                ]
            },
            new ChatCompletionChunk
            {
                Id = "id1",
                Choices =
                [
                    new ChunkChoice
                    {
                        Index = 0,
                        Delta = new ChunkDelta
                        {
                            ToolCalls =
                            [
                                new ToolCallChunk { Index = 0, Function = new ToolCallChunkFunction { Arguments = "{\"k\":" } }
                            ]
                        }
                    }
                ]
            },
            new ChatCompletionChunk
            {
                Id = "id1",
                Choices =
                [
                    new ChunkChoice
                    {
                        Index = 0,
                        Delta = new ChunkDelta
                        {
                            ToolCalls =
                            [
                                new ToolCallChunk { Index = 0, Function = new ToolCallChunkFunction { Arguments = "\"v\"}" } }
                            ]
                        },
                        FinishReason = "tool_calls"
                    }
                ]
            },
            UsageChunk("id1", 10, 5));

        var frames = new List<string>();
        await foreach (var f in t.Translate(upstream, FakeReq(), "target", CancellationToken.None))
            frames.Add(f);

        var events = ParseEvents(frames);
        var toolStart = events.FirstOrDefault(e => e.Type == "content_block_start" && e.ContentBlock?.Type == "tool_use");
        Assert.NotNull(toolStart);
        Assert.Equal("call_abc", toolStart!.ContentBlock!.Id);
        Assert.Equal("test_tool", toolStart.ContentBlock.Name);

        var jsonDeltas = events
            .Where(e => e.Type == "content_block_delta" && e.Delta?.Type == "input_json_delta")
            .ToList();
        Assert.NotEmpty(jsonDeltas);

        var msgDelta = events.FirstOrDefault(e => e.Type == "message_delta");
        Assert.Equal("tool_use", msgDelta?.Delta?.StopReason);
    }

    [Fact]
    public async Task ReasoningContent_PolicyNone_Dropped()
    {
        var t = BuildTranslator("none");
        var upstream = Chunks(
            new ChatCompletionChunk
            {
                Id = "id1",
                Choices =
                [
                    new ChunkChoice
                    {
                        Index = 0,
                        Delta = new ChunkDelta { ReasoningContent = "think think" },
                    }
                ]
            },
            TextChunk("id1", "Answer", "stop"),
            UsageChunk("id1", 5, 10));

        var frames = new List<string>();
        await foreach (var f in t.Translate(upstream, FakeReq(), "target", CancellationToken.None))
            frames.Add(f);

        var events = ParseEvents(frames);
        Assert.DoesNotContain(events, e => e.ContentBlock?.Type == "thinking");
        Assert.DoesNotContain(events, e => e.Delta?.Type == "thinking_delta");
    }

    [Fact]
    public async Task ReasoningContent_PolicyPassthrough_EmitsThinkingBlock()
    {
        var t = BuildTranslator("passthrough");
        var upstream = Chunks(
            new ChatCompletionChunk
            {
                Id = "id1",
                Choices =
                [
                    new ChunkChoice { Index = 0, Delta = new ChunkDelta { ReasoningContent = "reasoning text" } }
                ]
            },
            TextChunk("id1", "Final answer", "stop"),
            UsageChunk("id1", 5, 10));

        var frames = new List<string>();
        await foreach (var f in t.Translate(upstream, FakeReq(), "target", CancellationToken.None))
            frames.Add(f);

        var events = ParseEvents(frames);
        Assert.Contains(events, e => e.ContentBlock?.Type == "thinking");
        Assert.Contains(events, e => e.Delta?.Type == "thinking_delta" && e.Delta.Thinking == "reasoning text");
        Assert.Contains(events, e => e.Delta?.Type == "signature_delta");
    }

    [Fact]
    public async Task MessageStart_HasOriginalModelName()
    {
        var t = BuildTranslator();
        var upstream = Chunks(
            TextChunk("msg-123", "Hi", "stop"),
            UsageChunk("msg-123", 5, 10));

        var frames = new List<string>();
        await foreach (var f in t.Translate(upstream, FakeReq(), "target", CancellationToken.None))
            frames.Add(f);

        var events = ParseEvents(frames);
        var start = events.First(e => e.Type == "message_start");
        Assert.Equal("claude-sonnet-4-6", start.Message?.Model);
        Assert.Equal("msg-123", start.Message?.Id);
    }
}
