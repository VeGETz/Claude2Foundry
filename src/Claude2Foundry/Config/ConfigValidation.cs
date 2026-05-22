using Claude2Foundry.Config;
using Microsoft.Extensions.Logging;

namespace Claude2Foundry.Config;

public static class ConfigValidation
{
    private static readonly HashSet<string> ValidReasoningPolicies = ["none", "passthrough", "effort"];
    private static readonly HashSet<string> ValidTokenizerSources = ["TiktokenCl100k", "TiktokenO200k", "HuggingFace"];

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
}
