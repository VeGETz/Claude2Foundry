using System.Text.Json.Serialization;

namespace Claude2Foundry.Config;

public sealed record ProxyConfig
{
    [JsonPropertyName("BackendUrl")]
    public required string BackendUrl { get; init; }
    [JsonPropertyName("ApiKeyEnv")]
    public required string ApiKeyEnv { get; init; }
    [JsonPropertyName("DefaultModel")]
    public required string DefaultModel { get; init; }
    [JsonPropertyName("ModelAliases")]
    public Dictionary<string, string> ModelAliases { get; init; } = [];
    [JsonPropertyName("ReasoningPolicies")]
    public Dictionary<string, string> ReasoningPolicies { get; init; } = [];
    [JsonPropertyName("Tokenizers")]
    public Dictionary<string, TokenizerConfig> Tokenizers { get; init; } = [];
    [JsonPropertyName("Timeouts")]
    public TimeoutsConfig Timeouts { get; init; } = new();
    [JsonPropertyName("Monitor")]
    public MonitorConfig Monitor { get; init; } = new();
}

public sealed record TokenizerConfig
{
    [JsonPropertyName("Source")]
    public required string Source { get; init; }
    [JsonPropertyName("Path")]
    public string? Path { get; init; }
}

public sealed record TimeoutsConfig
{
    [JsonPropertyName("OutboundTotalSeconds")]
    public int OutboundTotalSeconds { get; init; } = 600;
    [JsonPropertyName("StreamIdleSeconds")]
    public int StreamIdleSeconds { get; init; } = 60;
}

public sealed record MonitorConfig
{
    [JsonPropertyName("CaptureMode")]
    public string CaptureMode { get; init; } = "hybrid";
    [JsonPropertyName("LogMaxBytes")]
    public long LogMaxBytes { get; init; } = 100 * 1024 * 1024;
    [JsonPropertyName("LogRetentionDays")]
    public int LogRetentionDays { get; init; } = 7;
}
