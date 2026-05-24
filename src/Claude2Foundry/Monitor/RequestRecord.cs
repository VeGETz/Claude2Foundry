using System.Text.Json.Nodes;

namespace Claude2Foundry.Monitor;

// List item returned by GET /api/admin/monitor/list
public sealed record MonitorListItem
{
    public required string Id { get; init; }
    public required DateTimeOffset Ts { get; init; }
    public string? Model { get; init; }
    public string? OriginalModel { get; init; }
    public string? MappedModel { get; init; }
    public required string Status { get; init; }  // ok | error | running
    public int? LatencyMs { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
}

// Detail returned by GET /api/admin/monitor/{id}
public sealed record MonitorDetail
{
    public required string Id { get; init; }
    public required DateTimeOffset Ts { get; init; }
    public string? Model { get; init; }
    public required string Status { get; init; }
    public int? LatencyMs { get; init; }
    public JsonNode? AnthropicBody { get; init; }
    public JsonNode? OpenaiBody { get; init; }
    public JsonNode? OpenaiResponse { get; init; }
    public JsonNode? AnthropicResponse { get; init; }
    public JsonNode? Headers { get; init; }
    public JsonNode? Error { get; init; }
}

// SSE frame for GET /api/admin/monitor/events
public sealed record SseFrame(long Seq, string Event, string Data);
