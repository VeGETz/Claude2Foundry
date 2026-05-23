using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Claude2Foundry.Config;

namespace Claude2Foundry.Tests.Integration;

public sealed class HotReloadTests : IAsyncLifetime
{
    private const string ApiKeyEnvName = "C2F_HOT_RELOAD_TEST_KEY_XYZ";
    private const string ApiKeyValue = "sk-hot-reload-fake-key-9999";

    private string _dataDir = null!;
    private FakeFoundryHandler _fakeHandler = null!;
    private HotReloadFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"c2f-hotreload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);

        // Write initial config file with ModelA alias BEFORE host starts
        var initialConfig = BuildLocalConfig("ModelA");
        await File.WriteAllTextAsync(Path.Combine(_dataDir, "appsettings.local.json"), initialConfig);

        Environment.SetEnvironmentVariable(ApiKeyEnvName, ApiKeyValue);
        Environment.SetEnvironmentVariable("C2F_DATA_DIR", _dataDir);

        _fakeHandler = new FakeFoundryHandler();
        _factory = new HotReloadFactory(_fakeHandler, _dataDir);
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        Environment.SetEnvironmentVariable("C2F_DATA_DIR", null);
        Environment.SetEnvironmentVariable(ApiKeyEnvName, null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task HotReload_StreamDrains_OnOldConfig_NextRequestUsesNew()
    {
        // Register OnChange before triggering config reload
        var monitor = _factory.Services.GetRequiredService<IOptionsMonitor<ProxyConfig>>();
        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = monitor.OnChange(_ => reloaded.TrySetResult());

        // Step 1: Fire streaming request (ModelA) — don't await full completion
        var streamRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent(BuildMessagesBody("claude-test", stream: true), Encoding.UTF8, "application/json"),
        };
        streamRequest.Headers.Add("anthropic-version", "2023-06-01");
        streamRequest.Headers.Add("x-api-key", "test-key");

        var streamResp = await _client.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead);
        streamResp.EnsureSuccessStatusCode();

        // Step 2: Wait until at least one chunk has arrived (mid-flight confirmed)
        await _fakeHandler.FirstChunkReady.WaitAsync(TimeSpan.FromSeconds(5));

        // Step 3: Push new config with ModelB alias via admin API
        var newConfigJson = BuildAdminConfigPayload("ModelB");
        var adminRequest = new HttpRequestMessage(HttpMethod.Post, "/api/admin/config")
        {
            Content = new StringContent(newConfigJson, Encoding.UTF8, "application/json"),
        };
        adminRequest.Headers.Add("X-C2F-Admin", "1");
        var configResp = await _client.SendAsync(adminRequest);
        configResp.EnsureSuccessStatusCode();

        // Step 4: Wait for IOptionsMonitor to pick up the file change
        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Step 5: Drain original stream — must finish cleanly
        var streamBody = await streamResp.Content.ReadAsStringAsync();
        Assert.Contains("event:", streamBody);

        // Step 6: Assert original stream used ModelA
        var calls1 = _fakeHandler.Calls;
        Assert.Single(calls1);
        Assert.Equal("ModelA", calls1[0].Model);
        Assert.True(calls1[0].IsStream);

        // Step 7: Fire second request (non-streaming) — must use ModelB
        var req2 = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent(BuildMessagesBody("claude-test", stream: false), Encoding.UTF8, "application/json"),
        };
        req2.Headers.Add("anthropic-version", "2023-06-01");
        req2.Headers.Add("x-api-key", "test-key");
        var resp2 = await _client.SendAsync(req2);
        resp2.EnsureSuccessStatusCode();

        var calls2 = _fakeHandler.Calls;
        Assert.Equal(2, calls2.Count);
        Assert.Equal("ModelB", calls2[1].Model);
        Assert.False(calls2[1].IsStream);
    }

    private static string BuildMessagesBody(string model, bool stream) =>
        JsonSerializer.Serialize(new
        {
            model,
            stream,
            max_tokens = 100,
            messages = new[] { new { role = "user", content = "hi" } }
        });

    private string BuildAdminConfigPayload(string targetModel) =>
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

    private static string BuildLocalConfig(string targetModel) =>
        JsonSerializer.Serialize(new
        {
            Proxy = new
            {
                BackendUrl = "https://test.openai.azure.com/openai/v1/",
                ApiKeyEnv = ApiKeyEnvName,
                DefaultModel = targetModel,
                ModelAliases = new Dictionary<string, string> { ["claude-test"] = targetModel },
                Timeouts = new { OutboundTotalSeconds = 30, StreamIdleSeconds = 30 },
                Monitor = new { CaptureMode = "hybrid", LogMaxBytes = 104857600, LogRetentionDays = 0 }
            }
        }, new JsonSerializerOptions { WriteIndented = true });
}

internal sealed class HotReloadFactory : WebApplicationFactory<Program>
{
    private readonly FakeFoundryHandler _handler;
    private readonly string _dataDir;

    public HotReloadFactory(FakeFoundryHandler handler, string dataDir)
    {
        _handler = handler;
        _dataDir = dataDir;
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            // Only override non-model keys; ModelAliases come from the file only
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Proxy:Monitor:LogRetentionDays"] = "0",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Replace HttpClient singleton with one backed by the fake handler
            var existing = services.SingleOrDefault(d => d.ServiceType == typeof(HttpClient));
            if (existing is not null) services.Remove(existing);

            services.AddSingleton<HttpClient>(_ => new HttpClient(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri("https://test.openai.azure.com/openai/v1/"),
            });
        });

        return base.CreateHost(builder);
    }
}

internal sealed record UpstreamCall(string Model, bool IsStream);

internal sealed class FakeFoundryHandler : HttpMessageHandler
{
    private readonly List<UpstreamCall> _calls = [];
    private readonly TaskCompletionSource _firstChunkReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<UpstreamCall> Calls { get { lock (_calls) return [.. _calls]; } }
    public Task FirstChunkReady => _firstChunkReady.Task;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is not null
            ? await request.Content.ReadAsStringAsync(ct)
            : "{}";

        using var doc = JsonDocument.Parse(body);
        var model = doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
        var isStream = doc.RootElement.TryGetProperty("stream", out var s) && s.GetBoolean();

        lock (_calls) _calls.Add(new UpstreamCall(model, isStream));

        if (isStream)
        {
            var content = new SlowSseContent(_firstChunkReady);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return response;
        }
        else
        {
            var json = JsonSerializer.Serialize(new
            {
                id = "resp-fake",
                model,
                choices = new[]
                {
                    new { index = 0, message = new { role = "assistant", content = "ok" }, finish_reason = "stop" }
                },
                usage = new { prompt_tokens = 5, completion_tokens = 2, total_tokens = 7 }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}

internal sealed class SlowSseContent : HttpContent
{
    private readonly TaskCompletionSource _firstChunkReady;

    public SlowSseContent(TaskCompletionSource firstChunkReady)
    {
        _firstChunkReady = firstChunkReady;
        Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        WriteChunksAsync(stream, CancellationToken.None);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) =>
        WriteChunksAsync(stream, ct);

    // Override to avoid full buffering — return a readable end of a Pipe
    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken ct)
    {
        var pipe = new Pipe();
        _ = Task.Run(async () =>
        {
            try { await WriteChunksAsync(pipe.Writer.AsStream(), ct); }
            catch (Exception ex) { await pipe.Writer.CompleteAsync(ex); return; }
            await pipe.Writer.CompleteAsync();
        }, ct);
        return Task.FromResult<Stream>(pipe.Reader.AsStream());
    }

    private async Task WriteChunksAsync(Stream stream, CancellationToken ct)
    {
        await using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { NewLine = "\n" };

        for (int i = 0; i < 5; i++)
        {
            var chunkJson = JsonSerializer.Serialize(new
            {
                id = "c",
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        delta = new { role = i == 0 ? "assistant" : (string?)null, content = "tok" },
                        finish_reason = (string?)null
                    }
                }
            });

            await writer.WriteAsync($"data: {chunkJson}\n\n");
            await writer.FlushAsync(ct);

            if (i == 0)
                _firstChunkReady.TrySetResult();

            await Task.Delay(80, ct);
        }

        // Usage event
        var usageJson = JsonSerializer.Serialize(new
        {
            id = "c",
            choices = Array.Empty<object>(),
            usage = new { prompt_tokens = 5, completion_tokens = 5, total_tokens = 10 }
        });
        await writer.WriteAsync($"data: {usageJson}\n\n");
        await writer.WriteAsync("data: [DONE]\n\n");
        await writer.FlushAsync(ct);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }
}
