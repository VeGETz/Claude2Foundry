using System.Text.Json.Nodes;
using Claude2Foundry.Config;
using Claude2Foundry.Monitor;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Claude2Foundry.Tests.Monitor;

public class JsonlWriterTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly CancellationTokenSource _cts = new();

    private JsonlWriter BuildWriter(bool enabled = true, long maxBodyBytes = 10_485_760)
    {
        var cfg = new ProxyConfig
        {
            BackendUrl = "https://t.openai.azure.com/openai/v1/",
            ApiKeyEnv = "K",
            DefaultModel = "m",
            Monitor = new MonitorConfig { Enabled = enabled, MaxBodyBytes = maxBodyBytes }
        };
        var monitor = new OptionsMonitorStub(cfg);
        return new JsonlWriter(_dir, monitor, NullLogger<JsonlWriter>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Enqueue_CreatesJsonlFile()
    {
        var writer = BuildWriter();
        await writer.StartAsync(CancellationToken.None);

        writer.Enqueue("request.received", "req-1", null, "claude-opus-4-7");
        await Task.Delay(300);
        await writer.StopAsync(CancellationToken.None);

        var logsDir = Path.Combine(_dir, "logs");
        var files = Directory.GetFiles(logsDir, "requests-*.jsonl");
        Assert.Single(files);
    }

    [Fact]
    public async Task Enqueue_WhenDisabled_DoesNotCreateFile()
    {
        var writer = BuildWriter(enabled: false);
        await writer.StartAsync(CancellationToken.None);

        writer.Enqueue("request.received", "req-1", null);
        await Task.Delay(200);
        await writer.StopAsync(CancellationToken.None);

        var logsDir = Path.Combine(_dir, "logs");
        var exists = Directory.Exists(logsDir) && Directory.GetFiles(logsDir, "*.jsonl").Length > 0;
        Assert.False(exists);
    }

    [Fact]
    public async Task StartAsync_PurgesOldJsonlFiles()
    {
        var logsDir = Path.Combine(_dir, "logs");
        Directory.CreateDirectory(logsDir);
        var old1 = Path.Combine(logsDir, "requests-20260101-000000-1.jsonl");
        var old2 = Path.Combine(logsDir, "requests-20260102-000000-2.jsonl");
        await File.WriteAllTextAsync(old1, "old");
        await File.WriteAllTextAsync(old2, "old");

        var writer = BuildWriter();
        await writer.StartAsync(CancellationToken.None);
        await writer.StopAsync(CancellationToken.None);

        Assert.False(File.Exists(old1));
        Assert.False(File.Exists(old2));
    }

    [Fact]
    public async Task CurrentFilePath_ReturnsNullWhenDisabled()
    {
        var writer = BuildWriter(enabled: false);
        await writer.StartAsync(CancellationToken.None);
        Assert.Null(writer.CurrentFilePath());
        await writer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CurrentFilePath_ReturnsPathWhenEnabled()
    {
        var writer = BuildWriter();
        await writer.StartAsync(CancellationToken.None);
        Assert.NotNull(writer.CurrentFilePath());
        await writer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Enqueue_WritesValidJsonLine()
    {
        var writer = BuildWriter();
        await writer.StartAsync(CancellationToken.None);

        var data = new JsonObject { ["foo"] = "bar" };
        writer.Enqueue("request.received", "req-abc", data, "claude-opus-4-7");

        await writer.StopAsync(CancellationToken.None);

        var path = writer.CurrentFilePath()!;
        var lines = await File.ReadAllLinesAsync(path);
        Assert.Single(lines.Where(l => !string.IsNullOrWhiteSpace(l)));

        var node = System.Text.Json.Nodes.JsonNode.Parse(lines[0])!;
        Assert.Equal("req-abc", node["id"]!.GetValue<string>());
        Assert.Equal("request.received", node["kind"]!.GetValue<string>());
        Assert.Equal("claude-opus-4-7", node["model"]!.GetValue<string>());
        Assert.Equal("bar", node["data"]!["foo"]!.GetValue<string>());
    }

    [Fact]
    public void CapBody_ReturnsNodeWhenUnderCap()
    {
        var writer = BuildWriter(maxBodyBytes: 1000);
        var node = new JsonObject { ["x"] = "small" };
        var result = writer.CapBody(node);
        Assert.Same(node, result);
    }

    [Fact]
    public void CapBody_ReturnsTruncationMarkerWhenOverCap()
    {
        var writer = BuildWriter(maxBodyBytes: 5);
        var node = new JsonObject { ["x"] = "this_is_longer_than_5_bytes" };
        var result = writer.CapBody(node)!.AsObject();
        Assert.True(result["__truncated"]!.GetValue<bool>());
        Assert.True(result["originalBytes"]!.GetValue<long>() > 5);
    }

    [Fact]
    public async Task Enqueue_ConcurrentRequests_NoInterleavedLines()
    {
        var writer = BuildWriter();
        await writer.StartAsync(CancellationToken.None);

        var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(() =>
        {
            for (int j = 0; j < 5; j++)
                writer.Enqueue("request.received", $"req-{i}", new JsonObject { ["seq"] = j });
        })).ToArray();

        await Task.WhenAll(tasks);
        await writer.StopAsync(CancellationToken.None);

        var path = writer.CurrentFilePath()!;
        var lines = (await File.ReadAllLinesAsync(path)).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

        // Every line must be valid JSON
        foreach (var line in lines)
        {
            var ex = Record.Exception(() => System.Text.Json.Nodes.JsonNode.Parse(line));
            Assert.Null(ex);
        }
        Assert.Equal(50, lines.Count);
    }

    private sealed class OptionsMonitorStub(ProxyConfig value) : IOptionsMonitor<ProxyConfig>
    {
        public ProxyConfig CurrentValue => value;
        public ProxyConfig Get(string? name) => value;
        public IDisposable? OnChange(Action<ProxyConfig, string?> listener) => null;
    }
}
