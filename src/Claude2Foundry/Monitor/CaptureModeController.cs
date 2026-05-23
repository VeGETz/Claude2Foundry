using Claude2Foundry.Admin;
using Claude2Foundry.Config;
using Microsoft.Extensions.Options;

namespace Claude2Foundry.Monitor;

public sealed class CaptureModeController(
    IOptionsMonitor<ProxyConfig> options,
    ConfigWriter writer,
    ILogger<CaptureModeController> logger)
{
    private volatile string? _sessionOverride;

    public string EffectiveMode => _sessionOverride ?? options.CurrentValue.Monitor.CaptureMode;

    public void SetSession(string mode)
    {
        _sessionOverride = mode;
        logger.LogInformation("Capture mode set to '{Mode}' (session scope)", mode);
    }

    public async Task SetPersistentAsync(string dataDir, string mode, CancellationToken ct = default)
    {
        var current = options.CurrentValue;
        var updated = current with { Monitor = current.Monitor with { CaptureMode = mode } };
        await writer.WriteAsync(dataDir, updated, ct);
        _sessionOverride = null;
        logger.LogInformation("Capture mode set to '{Mode}' (persistent scope)", mode);
    }
}
