namespace Claude2Foundry.Logging;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var id = GenerateId();
        ctx.TraceIdentifier = id;
        ctx.Items["CorrelationId"] = id;
        ctx.Response.OnStarting(() =>
        {
            ctx.Response.Headers["x-c2f-request-id"] = id;
            return Task.CompletedTask;
        });

        using (ctx.RequestServices.GetRequiredService<ILogger<CorrelationIdMiddleware>>()
            .BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
        {
            await next(ctx);
        }
    }

    private static string GenerateId() =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
}

public static class CorrelationIdExtensions
{
    public static string GetCorrelationId(this HttpContext ctx) =>
        ctx.Items.TryGetValue("CorrelationId", out var id) ? id as string ?? ctx.TraceIdentifier : ctx.TraceIdentifier;
}
