using Claude2Foundry.Config;

namespace Claude2Foundry.Tests.Config;

public class ConfigValidationMonitorTests
{
    private static ProxyConfig BaseConfig() => new()
    {
        BackendUrl = "https://test.openai.azure.com/openai/v1/",
        ApiKeyEnv = "TEST_API_KEY",
        DefaultModel = "test-model"
    };

    [Fact]
    public void MaxBodyBytes_Zero_Returns_Issue()
    {
        var cfg = BaseConfig() with { Monitor = new MonitorConfig { MaxBodyBytes = 0 } };
        var issues = ConfigValidation.ValidateForApi(cfg);
        Assert.Contains(issues, i => i.Path == "Proxy.Monitor.MaxBodyBytes");
    }

    [Fact]
    public void ValidMonitorSection_NoIssues()
    {
        var cfg = BaseConfig() with
        {
            Monitor = new MonitorConfig { Enabled = true, MaxBodyBytes = 10_485_760 }
        };
        var issues = ConfigValidation.ValidateForApi(cfg);
        var monitorIssues = issues.Where(i => i.Path.StartsWith("Proxy.Monitor")).ToList();
        Assert.Empty(monitorIssues);
    }

    [Fact]
    public void MonitorDisabled_NoIssues()
    {
        var cfg = BaseConfig() with
        {
            Monitor = new MonitorConfig { Enabled = false, MaxBodyBytes = 10_485_760 }
        };
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
