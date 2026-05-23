using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Claude2Foundry.Tests.Admin;

public sealed class AdminApiIntegrationTests : IClassFixture<TestAdminFactory>
{
    private readonly TestAdminFactory _factory;

    public AdminApiIntegrationTests(TestAdminFactory factory) => _factory = factory;

    [Fact]
    public async Task PostCaptureMode_MissingCsrfHeader_Returns400()
    {
        var client = _factory.CreateClient();
        var content = new StringContent("""{"mode":"hybrid","scope":"session"}""", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/admin/capture-mode", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostCaptureMode_ValidRequest_ResponseIncludesScope()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-C2F-Admin", "1");
        var content = new StringContent("""{"mode":"hybrid","scope":"session"}""", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/admin/capture-mode", content);
        response.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("hybrid", body.RootElement.GetProperty("mode").GetString());
        Assert.Equal("session", body.RootElement.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task GetConfig_DoesNotExposeResolvedApiKeyValue()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/admin/config");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(TestAdminFactory.ApiKeyValue, body);
    }
}

// Tests the degenerate case where ApiKeyEnv == key value (env var name IS the secret).
// MaskConfig must replace the key value with *** in the JSON response.
public sealed class MaskConfigRegressionTests : IClassFixture<EmbeddedKeyFactory>
{
    private readonly EmbeddedKeyFactory _factory;
    public MaskConfigRegressionTests(EmbeddedKeyFactory factory) => _factory = factory;

    [Fact]
    public async Task GetConfig_MaskConfig_ReplacesEmbeddedKeyValue()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/admin/config");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(EmbeddedKeyFactory.ApiKeyValue, body);
        Assert.Contains("***", body);
    }
}

public sealed class TestAdminFactory : WebApplicationFactory<Program>
{
    private const string ApiKeyEnvName = "C2F_ADMIN_TEST_KEY_XYZ";
    internal const string ApiKeyValue = "sk-test-abc123-fake-key-for-tests";

    protected override IHost CreateHost(IHostBuilder builder)
    {
        Environment.SetEnvironmentVariable(ApiKeyEnvName, ApiKeyValue);
        builder.ConfigureAppConfiguration(cfg =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Proxy:BackendUrl"] = "https://test.openai.azure.com/openai/v1/",
                ["Proxy:ApiKeyEnv"] = ApiKeyEnvName,
                ["Proxy:DefaultModel"] = "test-model",
                ["Proxy:Monitor:LogRetentionDays"] = "0",
            });
        });
        return base.CreateHost(builder);
    }
}

// ApiKeyEnv is set to the key value itself (degenerate but valid config).
// Env var name == env var value, so the key value appears in serialized JSON.
public sealed class EmbeddedKeyFactory : WebApplicationFactory<Program>
{
    internal const string ApiKeyValue = "sk-embed-test-zz9871";

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // env var NAME is the key value itself → key value appears in JSON
        Environment.SetEnvironmentVariable(ApiKeyValue, ApiKeyValue);
        builder.ConfigureAppConfiguration(cfg =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Proxy:BackendUrl"] = "https://test.openai.azure.com/openai/v1/",
                ["Proxy:ApiKeyEnv"] = ApiKeyValue,
                ["Proxy:DefaultModel"] = "test-model",
                ["Proxy:Monitor:LogRetentionDays"] = "0",
            });
        });
        return base.CreateHost(builder);
    }
}
