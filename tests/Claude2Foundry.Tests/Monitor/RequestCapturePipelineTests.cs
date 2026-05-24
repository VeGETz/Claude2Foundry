using Claude2Foundry.Config;
using Claude2Foundry.Monitor;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Claude2Foundry.Tests.Monitor;

public class RequestCapturePipelineTests : IAsyncDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly RequestCapturePipeline _pipeline;
    private readonly FullBodyCache _bodyCache;
    private readonly CancellationTokenSource _cts = new();

    public RequestCapturePipelineTests()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ProxyConfig
        {
            BackendUrl = "https://test.openai.azure.com/openai/v1/",
            ApiKeyEnv = "TEST_KEY",
            DefaultModel = "m"
        });
        var monitor = new OptionsMonitorStub(options.Value);
        var jsonlWriter = new JsonlWriter(_tempDir, monitor, NullLogger<JsonlWriter>.Instance);
        _bodyCache = new FullBodyCache();
        _pipeline = new RequestCapturePipeline(jsonlWriter, _bodyCache);
        _pipeline.Start(_cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _pipeline.DisposeAsync();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task Subscribe_Receives_ReplaySnapshot_First()
    {
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var frames = new List<SseFrame>();

        var sub = _pipeline.SubscribeAsync(null, ct.Token);
        await foreach (var frame in sub)
        {
            frames.Add(frame);
            break; // take only first frame
        }

        Assert.Single(frames);
        Assert.Equal("replay.snapshot", frames[0].Event);
    }

    [Fact]
    public async Task Emit_Events_Are_Received_By_Subscriber()
    {
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var frames = new List<SseFrame>();

        var sub = _pipeline.SubscribeAsync(null, ct.Token);
        var consumeTask = Task.Run(async () =>
        {
            await foreach (var frame in sub)
            {
                frames.Add(frame);
                if (frames.Count >= 3) break; // replay + received + response
            }
        }, ct.Token);

        await Task.Delay(50); // let subscriber register

        _pipeline.Emit(new RequestReceivedEvent(
            "req-1", DateTimeOffset.UtcNow, "claude-sonnet", false,
            new Dictionary<string, string>(), null));
        _pipeline.Emit(new ResponseSentEvent("req-1", 100, null));

        await consumeTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(frames.Count >= 2);
        Assert.Contains(frames, f => f.Event == "request.received");
        Assert.Contains(frames, f => f.Event == "response.sent");
    }

    [Fact]
    public async Task Ring_Occupancy_Increases_With_Events()
    {
        _pipeline.Emit(new RequestReceivedEvent(
            "r1", DateTimeOffset.UtcNow, "m", false, [], null));

        await Task.Delay(100); // let background process

        Assert.True(_pipeline.RingOccupancy >= 1);
    }


    [Fact]
    public async Task ResponseSentEvent_Stores_AnthropicAssembled_In_Cache()
    {
        var bodyCache = GetBodyCache();
        var assembled = new { id = "fake-resp", type = "message", content = new[] { new { type = "text", text = "hello" } } };

        _pipeline.Emit(new RequestReceivedEvent("req-x", DateTimeOffset.UtcNow, "claude-test", false, [], null));
        _pipeline.Emit(new ResponseSentEvent("req-x", 50, assembled));

        await Task.Delay(150); // let background process

        var record = bodyCache.Get("req-x");
        Assert.NotNull(record);
        Assert.Equal("complete", record!.Phase);
        Assert.NotNull(record.AnthropicAssembled);
    }

    [Fact]
    public async Task AccumulateBody_Carries_OpenaiBody_Into_FullRecord()
    {
        var bodyCache = GetBodyCache();
        var openaiBody = new { model = "deepseek-v3", messages = new[] { new { role = "user", content = "hi" } } };

        _pipeline.Emit(new RequestReceivedEvent("req-y", DateTimeOffset.UtcNow, "claude-test", false, [], null));
        _pipeline.Emit(new RequestTranslatedEvent("req-y", "deepseek-v3", openaiBody));
        _pipeline.Emit(new ResponseSentEvent("req-y", 75, new { type = "message" }));

        await Task.Delay(150);

        var record = bodyCache.Get("req-y");
        Assert.NotNull(record);
        Assert.NotNull(record!.OpenaiBody);
    }

    [Fact]
    public async Task CaptureErrorEvent_Stores_ErrorPhase_In_Cache()
    {
        var bodyCache = GetBodyCache();

        _pipeline.Emit(new RequestReceivedEvent("req-z", DateTimeOffset.UtcNow, "claude-test", false, [], null));
        _pipeline.Emit(new CaptureErrorEvent("req-z", "foundry-sent", "Foundry", "timeout"));

        await Task.Delay(150);

        var record = bodyCache.Get("req-z");
        Assert.NotNull(record);
        Assert.Equal("error", record!.Phase);
    }

    [Fact]
    public async Task AccumulateBody_Carries_AnthropicBody_From_RequestReceived()
    {
        var bodyCache = GetBodyCache();
        var anthropicBody = new { model = "claude-test", messages = new[] { new { role = "user", content = "hi" } } };

        _pipeline.Emit(new RequestReceivedEvent("req-ant", DateTimeOffset.UtcNow, "claude-test", false, [], anthropicBody));
        _pipeline.Emit(new ResponseSentEvent("req-ant", 50, new { type = "message" }));

        await Task.Delay(150);

        var record = bodyCache.Get("req-ant");
        Assert.NotNull(record);
        Assert.NotNull(record!.AnthropicBody);
    }

    [Fact]
    public async Task AccumulateBody_Carries_ResponseBody_From_FoundryResponseReceived()
    {
        var bodyCache = GetBodyCache();
        var foundryResp = new { id = "chatcmpl-1", choices = new[] { new { index = 0, finish_reason = "stop" } } };

        _pipeline.Emit(new RequestReceivedEvent("req-fr", DateTimeOffset.UtcNow, "claude-test", false, [], null));
        _pipeline.Emit(new FoundryResponseReceivedEvent("req-fr", foundryResp));
        _pipeline.Emit(new ResponseSentEvent("req-fr", 60, new { type = "message" }));

        await Task.Delay(150);

        var record = bodyCache.Get("req-fr");
        Assert.NotNull(record);
        Assert.NotNull(record!.ResponseBody);
    }

    private FullBodyCache GetBodyCache() => _bodyCache;

    private sealed class OptionsMonitorStub(ProxyConfig value) : IOptionsMonitor<ProxyConfig>
    {
        public ProxyConfig CurrentValue => value;
        public ProxyConfig Get(string? name) => value;
        public IDisposable? OnChange(Action<ProxyConfig, string?> listener) => null;
    }
}
