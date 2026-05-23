using Claude2Foundry.Config;
using Claude2Foundry.Monitor;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Claude2Foundry.Tests.Monitor;

public class RequestCapturePipelineTests : IAsyncDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly RequestCapturePipeline _pipeline;
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
        var bodyCache = new FullBodyCache();
        _pipeline = new RequestCapturePipeline(jsonlWriter, bodyCache);
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

    private sealed class OptionsMonitorStub(ProxyConfig value) : IOptionsMonitor<ProxyConfig>
    {
        public ProxyConfig CurrentValue => value;
        public ProxyConfig Get(string? name) => value;
        public IDisposable? OnChange(Action<ProxyConfig, string?> listener) => null;
    }
}
