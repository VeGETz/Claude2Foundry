using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Claude2Foundry.Config;
using Claude2Foundry.Monitor;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Claude2Foundry.Tests.Integration;

public sealed class AnthropicAssembledTests : IAsyncLifetime
{
    private const string ApiKeyEnvName = "C2F_ASSEMBLED_TEST_KEY_XYZ";

    private AssembledFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable(ApiKeyEnvName, "sk-assembled-fake-key");
        _factory = new AssembledFactory();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-C2F-Admin", "1");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        try { await _factory.DisposeAsync(); } catch (OperationCanceledException) { /* normal host shutdown */ }
        Environment.SetEnvironmentVariable(ApiKeyEnvName, null);
    }

    [Fact]
    public async Task NonStreaming_MonitorDetail_HasAnthropicResponse()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    model = "claude-test",
                    stream = false,
                    max_tokens = 10,
                    messages = new[] { new { role = "user", content = "hi" } }
                }),
                Encoding.UTF8, "application/json")
        };
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Headers.Add("x-api-key", "test-key");

        var resp = await _client.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        Assert.True(resp.Headers.TryGetValues("x-c2f-request-id", out var ids));
        var requestId = ids!.First();
        Assert.NotEmpty(requestId);

        // Let writer task flush to disk
        await Task.Delay(300);

        var detailResp = await _client.GetAsync($"/api/admin/monitor/{requestId}");
        detailResp.EnsureSuccessStatusCode();

        var body = await detailResp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("anthropicResponse", out var assembled),
            $"Missing anthropicResponse in response: {body}");
        Assert.NotEqual(JsonValueKind.Null, assembled.ValueKind);
    }

    [Fact]
    public async Task Streaming_MonitorDetail_HasAnthropicResponse()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    model = "claude-test",
                    stream = true,
                    max_tokens = 10,
                    messages = new[] { new { role = "user", content = "hi" } }
                }),
                Encoding.UTF8, "application/json")
        };
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Headers.Add("x-api-key", "test-key");

        var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        Assert.True(resp.Headers.TryGetValues("x-c2f-request-id", out var ids));
        var requestId = ids!.First();

        // Drain stream fully
        await resp.Content.ReadAsStringAsync();
        await Task.Delay(300);

        var detailResp = await _client.GetAsync($"/api/admin/monitor/{requestId}");
        detailResp.EnsureSuccessStatusCode();

        var body = await detailResp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("anthropicResponse", out var assembled),
            $"Missing anthropicResponse in streaming response: {body}");
        Assert.NotEqual(JsonValueKind.Null, assembled.ValueKind);
    }
}

internal sealed class AssembledFactory : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Proxy:BackendUrl"] = "https://test.openai.azure.com/openai/v1/",
                ["Proxy:ApiKeyEnv"] = "C2F_ASSEMBLED_TEST_KEY_XYZ",
                ["Proxy:DefaultModel"] = "test-model",
                ["Proxy:ModelAliases:claude-test"] = "test-model",
                ["Proxy:Monitor:Enabled"] = "true",
            });
        });

        builder.ConfigureServices(services =>
        {
            var existing = services.SingleOrDefault(d => d.ServiceType == typeof(HttpClient));
            if (existing is not null) services.Remove(existing);

            services.AddSingleton<HttpClient>(_ => new HttpClient(new AssembledFakeHandler(), disposeHandler: false)
            {
                BaseAddress = new Uri("https://test.openai.azure.com/openai/v1/"),
            });

            // Isolate each factory instance to its own temp dir so parallel test
            // runs don't race on PurgeOldFiles deleting each other's JSONL files.
            var existingWriter = services.SingleOrDefault(d => d.ServiceType == typeof(JsonlWriter));
            if (existingWriter is not null) services.Remove(existingWriter);
            var tempDir = Path.Combine(Path.GetTempPath(), $"c2f-test-{Guid.NewGuid():N}");
            services.AddSingleton<JsonlWriter>(sp => new JsonlWriter(
                tempDir,
                sp.GetRequiredService<IOptionsMonitor<ProxyConfig>>(),
                sp.GetRequiredService<ILogger<JsonlWriter>>()
            ));
        });

        return base.CreateHost(builder);
    }
}

internal sealed class AssembledFakeHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is not null
            ? await request.Content.ReadAsStringAsync(ct)
            : "{}";

        using var doc = JsonDocument.Parse(body);
        var isStream = doc.RootElement.TryGetProperty("stream", out var s) && s.GetBoolean();
        var model = doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() ?? "test-model" : "test-model";

        if (isStream)
        {
            var sse = "data: {\"id\":\"chatcmpl-x\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"hi\"},\"finish_reason\":null}]}\n\ndata: {\"id\":\"chatcmpl-x\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            var content = new StringContent(sse, Encoding.UTF8, "text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
        else
        {
            var json = JsonSerializer.Serialize(new
            {
                id = "chatcmpl-assembled-test",
                model,
                choices = new[]
                {
                    new { index = 0, message = new { role = "assistant", content = "hello" }, finish_reason = "stop" }
                },
                usage = new { prompt_tokens = 5, completion_tokens = 3, total_tokens = 8 }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
