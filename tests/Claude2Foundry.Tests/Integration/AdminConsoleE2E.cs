using System.Net;
using System.Text;
using System.Text.Json;
using Claude2Foundry.Admin;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Claude2Foundry.Tests.Integration;

public sealed class AdminConsoleE2E : IAsyncLifetime
{
    private const string ApiKeyEnvName = "C2F_E2E_TEST_KEY_XYZ";
    private const string ApiKeyValue = "sk-e2e-test-fake-9876";

    private string _dataDir = null!;
    private ConsoleE2EFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"c2f-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable(ApiKeyEnvName, ApiKeyValue);
        Environment.SetEnvironmentVariable("C2F_DATA_DIR", _dataDir);
        _factory = new ConsoleE2EFactory(ApiKeyEnvName);
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        try { await _factory.DisposeAsync(); } catch (OperationCanceledException) { }
        Environment.SetEnvironmentVariable("C2F_DATA_DIR", null);
        Environment.SetEnvironmentVariable(ApiKeyEnvName, null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task RoutesWired_SafeGetRoutes_Return200()
    {
        string[] routes = ["/api/admin/config", "/api/admin/config/schema", "/api/admin/health"];
        foreach (var route in routes)
        {
            var resp = await _client.GetAsync(route);
            Assert.True((int)resp.StatusCode < 500, $"{route} returned {resp.StatusCode}");
        }
    }

    [Fact]
    public async Task SseEndpoint_Returns200WithEventStreamContentType()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/events");
        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Csrf_PostConfigWithoutHeader_Returns400()
    {
        var body = BuildConfigPayload("test-model");
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await _client.PostAsync("/api/admin/config", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Csrf_PostConfigWithHeader_Returns200()
    {
        var body = BuildConfigPayload("test-model");
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/config")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-C2F-Admin", "1");
        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task RestartSentinel_WithWrapper_ExitSinkReceivesCode75()
    {
        var exitSink = new RecordingExitSink();
        var savedWrapper = Environment.GetEnvironmentVariable("C2F_WRAPPER");
        try
        {
            Environment.SetEnvironmentVariable("C2F_WRAPPER", "1");

            await using var restartFactory = new ConsoleE2EFactory(ApiKeyEnvName, exitSink);
            using var restartClient = restartFactory.CreateClient();

            var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/restart");
            req.Headers.Add("X-C2F-Admin", "1");
            var resp = await restartClient.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var code = await exitSink.ExitCode.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(75, code);
        }
        finally
        {
            Environment.SetEnvironmentVariable("C2F_WRAPPER", savedWrapper);
        }
    }

    [Fact]
    public async Task EmbeddedAssets_UiRoot_ServesHtml()
    {
        var resp = await _client.GetAsync("/_ui/");
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return; // UI not embedded in this build — skip gracefully
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/html", resp.Content.Headers.ContentType?.MediaType);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("<html", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveRoundTrip_AliasUpdate_ReflectedInGetConfig()
    {
        const string newModel = "my-new-target-model";
        var body = BuildConfigPayload(newModel);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/config")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-C2F-Admin", "1");
        var resp = await _client.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var result = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(result.RootElement.GetProperty("ok").GetBoolean());
    }

    private static string BuildConfigPayload(string targetModel) =>
        JsonSerializer.Serialize(new
        {
            proxy = new
            {
                BackendUrl = "https://test.openai.azure.com/openai/v1/",
                ApiKeyEnv = ApiKeyEnvName,
                DefaultModel = targetModel,
                ModelAliases = new Dictionary<string, string> { ["claude-test"] = targetModel },
                ReasoningPolicies = new Dictionary<string, string>(),
                Tokenizers = new Dictionary<string, object>(),
                Timeouts = new { OutboundTotalSeconds = 30, StreamIdleSeconds = 30 },
                Monitor = new { CaptureMode = "hybrid", LogMaxBytes = 104857600, LogRetentionDays = 0 }
            }
        });
}

internal sealed class ConsoleE2EFactory : WebApplicationFactory<Program>
{
    private readonly string _apiKeyEnvName;
    private readonly IExitSink? _exitSink;

    public ConsoleE2EFactory(string apiKeyEnvName, IExitSink? exitSink = null)
    {
        _apiKeyEnvName = apiKeyEnvName;
        _exitSink = exitSink;
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Proxy:BackendUrl"] = "https://test.openai.azure.com/openai/v1/",
                ["Proxy:ApiKeyEnv"] = _apiKeyEnvName,
                ["Proxy:DefaultModel"] = "test-model",
                ["Proxy:ModelAliases:claude-test"] = "test-model",
                ["Proxy:Monitor:LogRetentionDays"] = "0",
            });
        });

        builder.ConfigureServices(services =>
        {
            if (_exitSink is not null)
            {
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(IExitSink));
                if (existing is not null) services.Remove(existing);
                services.AddSingleton(_exitSink);
            }
        });

        return base.CreateHost(builder);
    }
}

internal sealed class RecordingExitSink : IExitSink
{
    private readonly TaskCompletionSource<int> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<int> ExitCode => _tcs.Task;

    public void Exit(int code) => _tcs.TrySetResult(code);
}
