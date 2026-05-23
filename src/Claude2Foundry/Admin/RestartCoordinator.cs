namespace Claude2Foundry.Admin;

public sealed class RestartCoordinator
{
    private volatile int _inFlightCount;
    private readonly CancellationTokenSource _draining = new();
    private readonly IExitSink _exitSink;

    public RestartCoordinator(IExitSink? exitSink = null) =>
        _exitSink = exitSink ?? new EnvironmentExitSink();

    public bool WrapperPresent => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("C2F_WRAPPER"));

    public IDisposable TrackRequest()
    {
        Interlocked.Increment(ref _inFlightCount);
        return new RequestScope(this);
    }

    public int InFlightCount => _inFlightCount;
    public bool IsDraining => _draining.IsCancellationRequested;
    public CancellationToken DrainToken => _draining.Token;

    public async Task InitiateRestartAsync(ILogger logger)
    {
        if (!WrapperPresent)
            throw new InvalidOperationException("C2F_WRAPPER not set");

        logger.LogInformation("Restart requested → draining in-flight requests (grace 60 s)");
        _draining.Cancel();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (_inFlightCount > 0 && !timeout.IsCancellationRequested)
            await Task.Delay(100, timeout.Token).ConfigureAwait(false);

        if (_inFlightCount > 0)
            logger.LogWarning("Drain grace expired with {Count} in-flight request(s); exiting anyway", _inFlightCount);

        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            _exitSink.Exit(75);
        });
    }

    private sealed class RequestScope(RestartCoordinator parent) : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref parent._inFlightCount);
    }
}
