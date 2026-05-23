using Claude2Foundry.Config;
using Claude2Foundry.Monitor;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Claude2Foundry.Tests.Monitor;

public class JsonlWriterTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly CancellationTokenSource _cts = new();

    private JsonlWriter BuildWriter(int retentionDays = 7, long logMaxBytes = 104_857_600)
    {
        var cfg = new ProxyConfig
        {
            BackendUrl = "https://t.openai.azure.com/openai/v1/",
            ApiKeyEnv = "K",
            DefaultModel = "m",
            Monitor = new MonitorConfig
            {
                CaptureMode = "hybrid",
                LogMaxBytes = logMaxBytes,
                LogRetentionDays = retentionDays
            }
        };
        var monitor = new OptionsMonitorStub(cfg);
        var writer = new JsonlWriter(_dir, monitor, NullLogger<JsonlWriter>.Instance);
        writer.Start(_cts.Token);
        return writer;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Write_CreatesJsonlFile()
    {
        var writer = BuildWriter();
        writer.Write("req-1", new { foo = "bar" });
        await Task.Delay(200); // let background flush

        var logsDir = Path.Combine(_dir, "logs");
        var files = Directory.GetFiles(logsDir, "requests-*.jsonl");
        Assert.Single(files);
    }

    [Fact]
    public async Task Write_WhenRetentionZero_DoesNotCreateFile()
    {
        var writer = BuildWriter(retentionDays: 0);
        writer.Write("req-1", new { foo = "bar" });
        await Task.Delay(200);

        var logsDir = Path.Combine(_dir, "logs");
        var exists = Directory.Exists(logsDir) && Directory.GetFiles(logsDir, "*.jsonl").Length > 0;
        Assert.False(exists);
    }

    [Fact]
    public async Task CurrentFilePath_ReturnsNullBeforeFirstWrite()
    {
        var writer = BuildWriter();
        // Don't write anything
        Assert.Null(writer.CurrentFilePath());
        await Task.CompletedTask;
    }

    private sealed class OptionsMonitorStub(ProxyConfig value) : IOptionsMonitor<ProxyConfig>
    {
        public ProxyConfig CurrentValue => value;
        public ProxyConfig Get(string? name) => value;
        public IDisposable? OnChange(Action<ProxyConfig, string?> listener) => null;
    }
}
