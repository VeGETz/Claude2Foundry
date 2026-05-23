namespace Claude2Foundry.Backend;

public sealed class FoundryHealthProbe(HttpClient http, ILogger<FoundryHealthProbe> logger)
{
    private sealed record ProbeResult(bool Reachable, int? LatencyMs, string? FailureMessage, DateTimeOffset At);

    private ProbeResult? _cached;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    public async Task<HealthStatus> GetAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cached is { } c && now - c.At < CacheTtl)
            return ToStatus(c);

        await _lock.WaitAsync(ct);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_cached is { } c2 && now - c2.At < CacheTtl)
                return ToStatus(c2);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            ProbeResult result;
            try
            {
                using var response = await http.GetAsync("models", ct);
                sw.Stop();
                result = new ProbeResult(true, (int)sw.ElapsedMilliseconds, null, DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                sw.Stop();
                logger.LogWarning("Foundry health probe failed: {Message}", ex.Message);
                result = new ProbeResult(false, (int)sw.ElapsedMilliseconds, ex.Message, DateTimeOffset.UtcNow);
            }

            _cached = result;
            return ToStatus(result);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static HealthStatus ToStatus(ProbeResult r) => new(r.Reachable, r.LatencyMs, r.At, r.FailureMessage);
}

public sealed record HealthStatus(
    bool Reachable,
    int? LastProbeMs,
    DateTimeOffset LastProbeAt,
    string? LastFailureMessage)
{
    public DateTimeOffset? LastFailureAt => LastFailureMessage is not null ? LastProbeAt : null;
}
