using Claude2Foundry.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claude2Foundry.Tests.Config;

public class ConfigValidationMonitorTests
{
    private static ProxyConfig BaseConfig() => new()
    {
        BackendUrl = "https://test.openai.azure.com/openai/v1/",
        ApiKeyEnv = "TEST_API_KEY",
        DefaultModel = "test-model"
    };

    [Theory]
    [InlineData("Proxy.Monitor.CaptureMode", "invalid")]
    public void InvalidCaptureMode_Returns_Issue(string expectedPath, string badMode)
    {
        var cfg = BaseConfig() with { Monitor = new MonitorConfig { CaptureMode = badMode } };
        var issues = ConfigValidation.ValidateForApi(cfg);
        Assert.Contains(issues, i => i.Path == expectedPath);
    }

    [Fact]
    public void LogMaxBytes_TooSmall_Returns_Issue()
    {
        var cfg = BaseConfig() with { Monitor = new MonitorConfig { LogMaxBytes = 100 } };
        var issues = ConfigValidation.ValidateForApi(cfg);
        Assert.Contains(issues, i => i.Path == "Proxy.Monitor.LogMaxBytes");
    }

    [Fact]
    public void LogRetentionDays_Negative_Returns_Issue()
    {
        var cfg = BaseConfig() with { Monitor = new MonitorConfig { LogRetentionDays = -1 } };
        var issues = ConfigValidation.ValidateForApi(cfg);
        Assert.Contains(issues, i => i.Path == "Proxy.Monitor.LogRetentionDays");
    }

    [Fact]
    public void ValidMonitorSection_NoIssues()
    {
        var cfg = BaseConfig() with
        {
            Monitor = new MonitorConfig
            {
                CaptureMode = "full",
                LogMaxBytes = 10_485_760,
                LogRetentionDays = 14
            }
        };
        // Validate without env var requirement (base API validation)
        var issues = ConfigValidation.ValidateForApi(cfg);
        var monitorIssues = issues.Where(i => i.Path.StartsWith("Proxy.Monitor")).ToList();
        Assert.Empty(monitorIssues);
    }

    [Fact]
    public void BadBackendUrl_Returns_IssueWithCorrectPath()
    {
        var cfg = new ProxyConfig
        {
            BackendUrl = "not-a-url",
            ApiKeyEnv = "KEY",
            DefaultModel = "model"
        };
        var issues = ConfigValidation.ValidateForApi(cfg);
        Assert.Contains(issues, i => i.Path == "Proxy.BackendUrl");
    }
}
