using System.Text.Json;
using System.Threading.Channels;
using Claude2Foundry.Config;
using Microsoft.Extensions.Options;

namespace Claude2Foundry.Monitor;

public sealed class JsonlWriter : IAsyncDisposable
{
    private readonly Channel<string> _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true
    });
    private readonly IOptionsMonitor<ProxyConfig> _options;
    private readonly string _dataDir;
    private readonly ILogger<JsonlWriter> _logger;
    private Task? _backgroundTask;

    public JsonlWriter(string dataDir, IOptionsMonitor<ProxyConfig> options, ILogger<JsonlWriter> logger)
    {
        _dataDir = dataDir;
        _options = options;
        _logger = logger;
    }

    public void Start(CancellationToken appStopping)
    {
        _backgroundTask = RunAsync(appStopping);
        _ = RunRetentionSweepAsync(appStopping);
    }

    public bool Write(string requestId, object record)
    {
        var monitor = _options.CurrentValue.Monitor;
        if (monitor.LogRetentionDays == 0) return false;   // persistence disabled

        var line = JsonSerializer.Serialize(new { id = requestId, ts = DateTimeOffset.UtcNow, data = record });
        return _channel.Writer.TryWrite(line);
    }

    public async Task DrainAsync()
    {
        _channel.Writer.TryComplete();
        if (_backgroundTask is not null)
            await _backgroundTask.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        if (_backgroundTask is not null)
            await _backgroundTask.ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var logsDir = Path.Combine(_dataDir, "logs");
        Directory.CreateDirectory(logsDir);

        StreamWriter? writer = null;
        string? currentPath = null;
        DateOnly currentDate = DateOnly.MinValue;
        long currentBytes = 0;
        int rotationSuffix = 1;

        try
        {
            await foreach (var line in _channel.Reader.ReadAllAsync(ct))
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                var monitor = _options.CurrentValue.Monitor;

                if (writer is null || today != currentDate || currentBytes >= monitor.LogMaxBytes)
                {
                    if (writer is not null)
                    {
                        await writer.FlushAsync(ct);
                        await writer.DisposeAsync();
                    }

                    if (today != currentDate)
                    {
                        currentDate = today;
                        rotationSuffix = 1;
                    }
                    else
                    {
                        rotationSuffix++;
                    }

                    currentPath = BuildPath(logsDir, today, rotationSuffix);
                    var fs = new FileStream(currentPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                    writer = new StreamWriter(fs) { AutoFlush = false };
                    currentBytes = fs.Length;
                    _logger.LogDebug("JSONL writer opened {Path}", currentPath);
                }

                await writer.WriteLineAsync(line.AsMemory(), ct);
                currentBytes += line.Length + 1;

                if (_channel.Reader.Count == 0)
                    await writer.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex) { _logger.LogError(ex, "JSONL writer background task failed"); }
        finally
        {
            if (writer is not null)
            {
                try { await writer.FlushAsync(); } catch { /* best effort */ }
                await writer.DisposeAsync();
            }
        }
    }

    private async Task RunRetentionSweepAsync(CancellationToken ct)
    {
        await SweepAsync();
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await SweepAsync();
        }
        catch (OperationCanceledException) { /* normal */ }
    }

    private async Task SweepAsync()
    {
        var retentionDays = _options.CurrentValue.Monitor.LogRetentionDays;
        if (retentionDays == 0) return;

        var logsDir = Path.Combine(_dataDir, "logs");
        if (!Directory.Exists(logsDir)) return;

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        try
        {
            foreach (var file in Directory.EnumerateFiles(logsDir, "requests-*.jsonl"))
            {
                if (File.GetCreationTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    _logger.LogInformation("Deleted old JSONL file: {File}", file);
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Retention sweep failed"); }
        await Task.CompletedTask;
    }

    private static string BuildPath(string logsDir, DateOnly date, int suffix)
    {
        var dateStr = date.ToString("yyyy-MM-dd");
        var filename = suffix == 1 ? $"requests-{dateStr}.jsonl" : $"requests-{dateStr}-{suffix}.jsonl";
        return Path.Combine(logsDir, filename);
    }

    public string? CurrentFilePath()
    {
        var logsDir = Path.Combine(_dataDir, "logs");
        var today = DateOnly.FromDateTime(DateTime.Now);
        var dateStr = today.ToString("yyyy-MM-dd");
        // Find the highest-suffix file for today
        if (!Directory.Exists(logsDir)) return null;
        var files = Directory.GetFiles(logsDir, $"requests-{dateStr}*.jsonl")
            .OrderByDescending(f => f).ToArray();
        return files.Length > 0 ? files[0] : null;
    }
}
