namespace Claude2Foundry.Config;

public sealed record ProxyConfig
{
    public required string BackendUrl { get; init; }
    public required string ApiKeyEnv { get; init; }
    public required string DefaultModel { get; init; }
    public Dictionary<string, string> ModelAliases { get; init; } = [];
    public Dictionary<string, string> ReasoningPolicies { get; init; } = [];
    public Dictionary<string, TokenizerConfig> Tokenizers { get; init; } = [];
    public TimeoutsConfig Timeouts { get; init; } = new();
    public MonitorConfig Monitor { get; init; } = new();
}

public sealed record TokenizerConfig
{
    public required string Source { get; init; }
    public string? Path { get; init; }
}

public sealed record TimeoutsConfig
{
    public int OutboundTotalSeconds { get; init; } = 600;
    public int StreamIdleSeconds { get; init; } = 60;
}

public sealed record MonitorConfig
{
    public string CaptureMode { get; init; } = "hybrid";
    public long LogMaxBytes { get; init; } = 100 * 1024 * 1024;
    public int LogRetentionDays { get; init; } = 7;
}
