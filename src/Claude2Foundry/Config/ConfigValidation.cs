using Claude2Foundry.Config;
using Microsoft.Extensions.Logging;

namespace Claude2Foundry.Config;

public static class ConfigValidation
{
    private static readonly HashSet<string> ValidReasoningPolicies = ["none", "passthrough", "effort"];
    private static readonly HashSet<string> ValidTokenizerSources = ["TiktokenCl100k", "TiktokenO200k", "HuggingFace"];
    private static readonly HashSet<string> ValidCaptureModes = ["hybrid", "full"];

    public static void Validate(ProxyConfig cfg, ILogger logger)
    {
        var errors = new List<string>();

        var backendUrl = cfg.BackendUrl ?? "";
        var isAzureOpenAI = backendUrl.Contains(".openai.azure.com", StringComparison.OrdinalIgnoreCase);
        var isFoundryServices = backendUrl.Contains(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(backendUrl) ||
            !backendUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            (!isAzureOpenAI && !isFoundryServices) ||
            !backendUrl.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Proxy:BackendUrl must be a well-formed https://*.openai.azure.com/openai/v1/ or https://*.services.ai.azure.com/.../openai/v1/ URL");
        }

        var apiKeyEnv = cfg.ApiKeyEnv;
        if (string.IsNullOrWhiteSpace(apiKeyEnv))
        {
            errors.Add("Proxy:ApiKeyEnv is required");
        }
        else
        {
            var keyValue = Environment.GetEnvironmentVariable(apiKeyEnv);
            if (string.IsNullOrWhiteSpace(keyValue))
                errors.Add($"Env var '{apiKeyEnv}' (Proxy:ApiKeyEnv) is unset or empty");
        }

        if (string.IsNullOrWhiteSpace(cfg.DefaultModel))
            errors.Add("Proxy:DefaultModel is required");

        foreach (var (target, policy) in cfg.ReasoningPolicies)
        {
            if (!ValidReasoningPolicies.Contains(policy))
                errors.Add($"ReasoningPolicies['{target}'] = '{policy}' is not valid; must be none|passthrough|effort");
        }

        foreach (var (target, tokCfg) in cfg.Tokenizers)
        {
            if (!ValidTokenizerSources.Contains(tokCfg.Source))
            {
                errors.Add($"Tokenizers['{target}'].Source = '{tokCfg.Source}' is not valid; must be TiktokenCl100k|TiktokenO200k|HuggingFace");
                continue;
            }
            if (tokCfg.Source == "HuggingFace")
            {
                if (string.IsNullOrWhiteSpace(tokCfg.Path))
                    errors.Add($"Tokenizers['{target}'].Path is required for HuggingFace source");
                else if (!File.Exists(tokCfg.Path))
                    errors.Add($"Tokenizers['{target}'].Path '{tokCfg.Path}' does not exist");
            }
        }

        if (cfg.Timeouts.OutboundTotalSeconds < 1)
            errors.Add("Proxy:Timeouts:OutboundTotalSeconds must be >= 1");
        if (cfg.Timeouts.StreamIdleSeconds < 1)
            errors.Add("Proxy:Timeouts:StreamIdleSeconds must be >= 1");

        // Monitor section validation
        if (!ValidCaptureModes.Contains(cfg.Monitor.CaptureMode))
            errors.Add($"Proxy:Monitor:CaptureMode must be 'hybrid' or 'full'; got '{cfg.Monitor.CaptureMode}'");
        if (cfg.Monitor.LogMaxBytes < 1_048_576)
            errors.Add("Proxy:Monitor:LogMaxBytes must be >= 1048576 (1 MB)");
        if (cfg.Monitor.LogRetentionDays < 0)
            errors.Add("Proxy:Monitor:LogRetentionDays must be >= 0");

        if (errors.Count > 0)
        {
            foreach (var e in errors)
                logger.LogCritical("Config error: {Error}", e);
            throw new InvalidOperationException($"Configuration invalid: {errors[0]}");
        }

        // Warn but don't fail for missing policy/tokenizer entries
        foreach (var target in cfg.ModelAliases.Values.Distinct())
        {
            if (!cfg.ReasoningPolicies.ContainsKey(target))
                logger.LogWarning("Target '{Target}' has no ReasoningPolicies entry; defaulting to 'none'", target);
            if (!cfg.Tokenizers.ContainsKey(target))
                logger.LogWarning("Target '{Target}' has no Tokenizers entry; defaulting to TiktokenCl100k", target);
        }
    }

    // Returns structured issue list for API usage (POST /api/admin/config validation)
    public static List<ConfigIssue> ValidateForApi(ProxyConfig cfg)
    {
        var issues = new List<ConfigIssue>();

        var backendUrl = cfg.BackendUrl ?? "";
        var isAzureOpenAI = backendUrl.Contains(".openai.azure.com", StringComparison.OrdinalIgnoreCase);
        var isFoundryServices = backendUrl.Contains(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(backendUrl) ||
            !backendUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            (!isAzureOpenAI && !isFoundryServices) ||
            !backendUrl.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new("Proxy.BackendUrl",
                "Must be https://*.openai.azure.com/openai/v1/ or https://*.services.ai.azure.com/.../openai/v1/"));
        }

        if (string.IsNullOrWhiteSpace(cfg.ApiKeyEnv))
            issues.Add(new("Proxy.ApiKeyEnv", "Required"));

        if (string.IsNullOrWhiteSpace(cfg.DefaultModel))
            issues.Add(new("Proxy.DefaultModel", "Required"));

        foreach (var (target, policy) in cfg.ReasoningPolicies)
        {
            if (!ValidReasoningPolicies.Contains(policy))
                issues.Add(new($"Proxy.ReasoningPolicies.{target}", "Must be none|passthrough|effort"));
        }

        foreach (var (target, tokCfg) in cfg.Tokenizers)
        {
            if (!ValidTokenizerSources.Contains(tokCfg.Source))
                issues.Add(new($"Proxy.Tokenizers.{target}.Source", "Must be TiktokenCl100k|TiktokenO200k|HuggingFace"));
            else if (tokCfg.Source == "HuggingFace" && string.IsNullOrWhiteSpace(tokCfg.Path))
                issues.Add(new($"Proxy.Tokenizers.{target}.Path", "Required for HuggingFace source"));
        }

        if (cfg.Timeouts.OutboundTotalSeconds < 1)
            issues.Add(new("Proxy.Timeouts.OutboundTotalSeconds", "Must be >= 1"));
        if (cfg.Timeouts.StreamIdleSeconds < 1)
            issues.Add(new("Proxy.Timeouts.StreamIdleSeconds", "Must be >= 1"));

        if (!ValidCaptureModes.Contains(cfg.Monitor.CaptureMode))
            issues.Add(new("Proxy.Monitor.CaptureMode", "Must be 'hybrid' or 'full'"));
        if (cfg.Monitor.LogMaxBytes < 1_048_576)
            issues.Add(new("Proxy.Monitor.LogMaxBytes", "Must be >= 1048576 (1 MB)"));
        if (cfg.Monitor.LogRetentionDays < 0)
            issues.Add(new("Proxy.Monitor.LogRetentionDays", "Must be >= 0"));

        return issues;
    }
}

public sealed record ConfigIssue(string Path, string Message);
