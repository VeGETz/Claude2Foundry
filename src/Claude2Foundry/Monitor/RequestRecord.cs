namespace Claude2Foundry.Monitor;

// Ring buffer compact summary (one per request, for replay.snapshot)
public sealed record RingSummary
{
    public required string Id { get; init; }
    public required DateTimeOffset Ts { get; init; }
    public required string OriginalModel { get; init; }
    public string? ResolvedModel { get; init; }
    public required string Phase { get; init; }   // received|translated|foundry-sent|streaming|complete|error
    public string? Status { get; init; }           // ok|error
    public int? ElapsedMs { get; init; }
    public UsageDto? Usage { get; init; }
    public string? Error { get; init; }
}

public sealed record UsageDto
{
    public int Input { get; init; }
    public int Output { get; init; }
}

// Compact summary carried in ring and SSE request.received
public sealed record RequestSummary
{
    public required string Id { get; init; }
    public required DateTimeOffset Ts { get; init; }
    public required string OriginalModel { get; init; }
    public required bool Stream { get; init; }
    public object? BodyPreview { get; init; }
    public Dictionary<string, string> Headers { get; init; } = [];
}

// Full record returned by GET /api/admin/events/full/{id}
public sealed record RequestFullRecord
{
    public required string Id { get; init; }
    public object? AnthropicBody { get; init; }
    public object? OpenaiBody { get; init; }
    public object? ResponseBody { get; init; }
    public Dictionary<string, string> Headers { get; init; } = [];
    public required string Phase { get; init; }
    public bool Expired { get; init; }
}

public sealed record RequestUsage
{
    public int Input { get; init; }
    public int Output { get; init; }
}

public enum CaptureMode { Hybrid, Full }
public enum CaptureScope { Session, Persistent }

// ---------------------------------------------------------------------------
// Capture event hierarchy — one type per SSE event phase
// ---------------------------------------------------------------------------

public abstract record CaptureEvent(string Id);

public sealed record RequestReceivedEvent(
    string Id,
    DateTimeOffset Ts,
    string OriginalModel,
    bool Stream,
    Dictionary<string, string> Headers,
    object? BodyPreview) : CaptureEvent(Id);

public sealed record RequestTranslatedEvent(
    string Id,
    string ResolvedModel,
    object? OpenaiBody) : CaptureEvent(Id);

public sealed record FoundrySentEvent(
    string Id,
    DateTimeOffset Ts) : CaptureEvent(Id);

public sealed record FoundryChunkEvent(
    string Id,
    int Seq,
    string? DeltaText,
    object? DeltaToolCall,
    string? ReasoningDelta) : CaptureEvent(Id);

public sealed record FoundryCompleteEvent(
    string Id,
    UsageDto Usage,
    string FinishReason) : CaptureEvent(Id);

public sealed record ResponseSentEvent(
    string Id,
    int ElapsedMs,
    object? AnthropicAssembled) : CaptureEvent(Id);

public sealed record CaptureErrorEvent(
    string Id,
    string Phase,
    string Origin,
    string Message) : CaptureEvent(Id);
