namespace Claude2Foundry.Monitor;

public sealed record RequestSummary
{
    public required string Id { get; init; }
    public required DateTimeOffset Ts { get; init; }
    public required string OriginalModel { get; init; }
    public required bool Stream { get; init; }
    public object? BodyPreview { get; init; }
    public Dictionary<string, string> Headers { get; init; } = [];
}

public sealed record RequestFullRecord
{
    public required string Id { get; init; }
    public object? AnthropicBody { get; init; }
    public object? OpenaiBody { get; init; }
    public object? ResponseBody { get; init; }
    public Dictionary<string, string> Headers { get; init; } = [];
    public required string Phase { get; init; }
}

public sealed record RequestUsage
{
    public int Input { get; init; }
    public int Output { get; init; }
}

public enum CaptureMode { Hybrid, Full }

public enum CaptureScope { Session, Persistent }
