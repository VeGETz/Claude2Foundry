using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Claude2Foundry.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Claude2Foundry.Monitor;

public sealed class JsonlWriter : IHostedService, IAsyncDisposable
{
    private readonly Channel<string> _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true
    });
    private readonly IOptionsMonitor<ProxyConfig> _options;
    private readonly string _dataDir;
    private readonly ILogger<JsonlWriter> _logger;
    private readonly List<Channel<string>> _subscribers = [];
    private readonly Lock _subscribersLock = new();
    private string? _currentFilePath;
    private Task? _writerTask;

    public JsonlWriter(string dataDir, IOptionsMonitor<ProxyConfig> options, ILogger<JsonlWriter> logger)
    {
        _dataDir = dataDir;
        _options = options;
        _logger = logger;
    }

    public bool Enabled => _options.CurrentValue.Monitor.Enabled;
    public string? CurrentFilePath() => _currentFilePath;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Monitor.Enabled) return Task.CompletedTask;

        var logsDir = Path.Combine(_dataDir, "logs");
        PurgeOldFiles(logsDir);
        Directory.CreateDirectory(logsDir);

        var bootId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}";
        _currentFilePath = Path.Combine(logsDir, $"requests-{bootId}.jsonl");

        _writerTask = RunAsync(_currentFilePath);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        if (_writerTask is not null)
        {
            try { await _writerTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);

    public void Enqueue(string kind, string id, JsonNode? data, string? model = null)
    {
        if (!_options.CurrentValue.Monitor.Enabled) return;
        try
        {
            var line = BuildLine(kind, id, data, model);
            _channel.Writer.TryWrite(line);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Monitor capture failed for {Kind} id={Id}; writing error line", kind, id);
            try
            {
                var errLine = BuildLine("request.error", id, new JsonObject
                {
                    ["phase"] = "serialize",
                    ["message"] = ex.Message
                });
                _channel.Writer.TryWrite(errLine);
            }
            catch { /* best effort */ }
        }
    }

    public JsonNode? CapBody(JsonNode? node)
    {
        if (node is null) return null;
        var json = node.ToJsonString();
        var maxBytes = _options.CurrentValue.Monitor.MaxBodyBytes;
        if (json.Length <= maxBytes) return node;
        return new JsonObject { ["__truncated"] = true, ["originalBytes"] = (long)json.Length };
    }

    public Channel<string> Subscribe()
    {
        var ch = Channel.CreateBounded<string>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
        lock (_subscribersLock) _subscribers.Add(ch);
        return ch;
    }

    public void Unsubscribe(Channel<string> ch)
    {
        lock (_subscribersLock) _subscribers.Remove(ch);
        ch.Writer.TryComplete();
    }

    private void PurgeOldFiles(string logsDir)
    {
        if (!Directory.Exists(logsDir)) return;
        foreach (var file in Directory.EnumerateFiles(logsDir, "requests-*.jsonl"))
        {
            try { File.Delete(file); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete old JSONL file {File}", file); }
        }
    }

    private static string BuildLine(string kind, string id, JsonNode? data, string? model = null)
    {
        var obj = new JsonObject
        {
            ["id"] = id,
            ["ts"] = DateTimeOffset.UtcNow.ToString("O"),
            ["kind"] = kind
        };
        if (model is not null) obj["model"] = model;
        if (data is not null) obj["data"] = data;
        return obj.ToJsonString();
    }

    private void BroadcastToSubscribers(string line)
    {
        lock (_subscribersLock)
        {
            foreach (var sub in _subscribers)
                sub.Writer.TryWrite(line);
        }
    }

    private async Task RunAsync(string filePath)
    {
        StreamWriter? writer = null;
        try
        {
            var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            writer = new StreamWriter(fs, System.Text.Encoding.UTF8) { AutoFlush = false };
            _logger.LogDebug("JSONL writer opened {Path}", filePath);

            await foreach (var line in _channel.Reader.ReadAllAsync(CancellationToken.None))
            {
                await writer.WriteLineAsync(line);
                BroadcastToSubscribers(line);
                if (_channel.Reader.Count == 0)
                    await writer.FlushAsync();
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "JSONL writer task failed"); }
        finally
        {
            if (writer is not null)
            {
                try { await writer.FlushAsync(); } catch { }
                await writer.DisposeAsync();
            }
            lock (_subscribersLock)
            {
                foreach (var sub in _subscribers)
                    sub.Writer.TryComplete();
                _subscribers.Clear();
            }
        }
    }
}
