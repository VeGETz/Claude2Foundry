using System.Text.Json;
using Claude2Foundry.Config;
using Claude2Foundry.Protocol.Anthropic;
using Claude2Foundry.Protocol.OpenAI;
using Microsoft.Extensions.Logging;

namespace Claude2Foundry.Translation;

public sealed class StreamTranslator(ProxyConfig config)
{
    public async IAsyncEnumerable<string> Translate(
        IAsyncEnumerable<ChatCompletionChunk> upstream,
        AnthropicMessagesRequest original,
        string resolvedTarget,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var policy = config.ReasoningPolicies.GetValueOrDefault(resolvedTarget, "none");
        var state = new StreamState();
        var firstChunk = true;

        await foreach (var chunk in upstream.WithCancellation(ct))
        {
            if (firstChunk)
            {
                firstChunk = false;
                yield return SseFrame("message_start", new AnthropicStreamEvent
                {
                    Type = "message_start",
                    Message = new AnthropicMessagesResponse
                    {
                        Id = chunk.Id,
                        Model = original.Model,
                        Content = [],
                        StopReason = null,
                        Usage = new AnthropicUsage { InputTokens = 0, OutputTokens = 0 }
                    }
                });
                // vLLM skips processing the first chunk's delta after emitting message_start
                // but if choices present, still process them
                if (chunk.Choices.Count == 0) continue;
            }

            // Accumulate usage from any chunk that carries it
            if (chunk.Usage is { } usage)
            {
                state.CurrentUsage.InputTokens = usage.PromptTokens;
                state.CurrentUsage.OutputTokens = usage.CompletionTokens;
            }

            // Final usage-only chunk (choices empty) — emit message_delta + message_stop
            if (chunk.Choices.Count == 0)
            {
                foreach (var e in StopActiveBlock(state))
                    yield return e;

                yield return SseFrame("message_delta", new AnthropicStreamEvent
                {
                    Type = "message_delta",
                    Delta = new AnthropicDelta
                    {
                        StopReason = StopReasonMap.Map(state.FinishReason),
                        StopSequence = null,
                    },
                    Usage = new AnthropicUsage
                    {
                        InputTokens = state.CurrentUsage.InputTokens,
                        OutputTokens = state.CurrentUsage.OutputTokens,
                    }
                });
                yield return SseFrame("message_stop", new AnthropicStreamEvent { Type = "message_stop" });
                yield break;
            }

            var choice = chunk.Choices[0];
            if (choice.FinishReason is not null)
                state.FinishReason = choice.FinishReason;

            var delta = choice.Delta;
            var reasoning = delta.ReasoningContent ?? delta.Reasoning;

            if (!string.IsNullOrEmpty(reasoning) && policy is "passthrough" or "effort")
            {
                if (state.Active?.BlockType != "thinking")
                {
                    foreach (var e in StopActiveBlock(state)) yield return e;
                    yield return StartBlock(state, "thinking");
                }
                yield return SseFrame("content_block_delta", new AnthropicStreamEvent
                {
                    Type = "content_block_delta",
                    Index = state.Active!.BlockIndex,
                    Delta = new AnthropicDelta { Type = "thinking_delta", Thinking = reasoning }
                });
            }

            if (!string.IsNullOrEmpty(delta.Content))
            {
                if (state.Active?.BlockType != "text")
                {
                    foreach (var e in StopActiveBlock(state)) yield return e;
                    yield return StartBlock(state, "text");
                }
                yield return SseFrame("content_block_delta", new AnthropicStreamEvent
                {
                    Type = "content_block_delta",
                    Index = state.Active!.BlockIndex,
                    Delta = new AnthropicDelta { Type = "text_delta", Text = delta.Content }
                });
            }

            foreach (var toolCall in delta.ToolCalls ?? [])
            {
                if (toolCall.Id is not null)
                {
                    state.ToolIndexToId[toolCall.Index] = toolCall.Id;
                    var toolName = toolCall.Function?.Name;
                    if (state.Active?.ToolUseId != toolCall.Id && toolName is not null)
                    {
                        foreach (var e in StopActiveBlock(state)) yield return e;
                        yield return StartBlock(state, "tool_use", toolCall.Id, toolName);
                    }
                    // Arguments that arrived with the id chunk
                    if (!string.IsNullOrEmpty(toolCall.Function?.Arguments) && state.Active?.ToolUseId == toolCall.Id)
                    {
                        yield return SseFrame("content_block_delta", new AnthropicStreamEvent
                        {
                            Type = "content_block_delta",
                            Index = state.Active!.BlockIndex,
                            Delta = new AnthropicDelta { Type = "input_json_delta", PartialJson = toolCall.Function!.Arguments }
                        });
                    }
                }
                else
                {
                    var toolUseId = state.ToolIndexToId.GetValueOrDefault(toolCall.Index);
                    if (toolUseId is not null
                        && !string.IsNullOrEmpty(toolCall.Function?.Arguments)
                        && state.Active?.ToolUseId == toolUseId)
                    {
                        yield return SseFrame("content_block_delta", new AnthropicStreamEvent
                        {
                            Type = "content_block_delta",
                            Index = state.Active!.BlockIndex,
                            Delta = new AnthropicDelta { Type = "input_json_delta", PartialJson = toolCall.Function!.Arguments }
                        });
                    }
                }
            }
        }

        // Stream ended without a zero-choices usage chunk — emit termination
        foreach (var e in StopActiveBlock(state))
            yield return e;

        yield return SseFrame("message_delta", new AnthropicStreamEvent
        {
            Type = "message_delta",
            Delta = new AnthropicDelta
            {
                StopReason = StopReasonMap.Map(state.FinishReason),
                StopSequence = null,
            },
            Usage = new AnthropicUsage
            {
                InputTokens = state.CurrentUsage.InputTokens,
                OutputTokens = state.CurrentUsage.OutputTokens,
            }
        });
        yield return SseFrame("message_stop", new AnthropicStreamEvent { Type = "message_stop" });
    }

    private static IEnumerable<string> StopActiveBlock(StreamState state)
    {
        if (state.Active is null) yield break;

        if (state.Active.BlockType == "thinking" && state.Active.Signature is not null)
        {
            yield return SseFrame("content_block_delta", new AnthropicStreamEvent
            {
                Type = "content_block_delta",
                Index = state.Active.BlockIndex,
                Delta = new AnthropicDelta { Type = "signature_delta", Signature = state.Active.Signature }
            });
        }

        yield return SseFrame("content_block_stop", new AnthropicStreamEvent
        {
            Type = "content_block_stop",
            Index = state.Active.BlockIndex,
        });

        state.NextBlockIndex++;
        state.Active = null;
    }

    private static string StartBlock(StreamState state, string type, string? toolUseId = null, string? toolName = null)
    {
        var idx = state.NextBlockIndex;
        state.Active = new ActiveBlock
        {
            BlockIndex = idx,
            BlockType = type,
            ToolUseId = toolUseId,
            Signature = type == "thinking" ? Guid.NewGuid().ToString("N") : null,
        };

        AnthropicContentBlock startBlock = type switch
        {
            "text" => new AnthropicContentBlock { Type = "text", Text = "" },
            "thinking" => new AnthropicContentBlock { Type = "thinking", Thinking = "", Signature = "" },
            "tool_use" => new AnthropicContentBlock { Type = "tool_use", Id = toolUseId!, Name = toolName!, Input = JsonDocument.Parse("{}").RootElement },
            _ => throw new InvalidOperationException($"Unknown block type {type}")
        };

        return SseFrame("content_block_start", new AnthropicStreamEvent
        {
            Type = "content_block_start",
            Index = idx,
            ContentBlock = startBlock,
        });
    }

    private static string SseFrame(string eventType, AnthropicStreamEvent evt)
    {
        var json = JsonSerializer.Serialize(evt, Protocol.AppJsonSerializerContext.Default.AnthropicStreamEvent);
        return $"event: {eventType}\ndata: {json}\n\n";
    }

    private sealed class StreamState
    {
        public AnthropicUsage CurrentUsage { get; } = new();
        public int NextBlockIndex { get; set; }
        public ActiveBlock? Active { get; set; }
        public Dictionary<int, string> ToolIndexToId { get; } = [];
        public string? FinishReason { get; set; }
    }

    private sealed class ActiveBlock
    {
        public required int BlockIndex { get; init; }
        public required string BlockType { get; init; }
        public string? ToolUseId { get; init; }
        public string? Signature { get; init; }
    }
}
