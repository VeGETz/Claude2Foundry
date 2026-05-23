namespace Claude2Foundry.Admin;

public sealed class CorsAndCsrfGuard
{
    private static readonly HashSet<string> MutatingMethods = ["POST", "PUT", "DELETE", "PATCH"];
    private readonly RequestDelegate _next;

    public CorsAndCsrfGuard(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var req = context.Request;
        var res = context.Response;

        if (req.Method == HttpMethods.Options)
        {
            var origin = req.Headers.Origin.ToString();
            if (!IsAllowedOrigin(origin))
            {
                res.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            res.Headers.Append("Access-Control-Allow-Origin", origin);
            res.Headers.Append("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, PATCH");
            res.Headers.Append("Access-Control-Allow-Headers", "Content-Type, X-C2F-Admin");
            res.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        res.OnStarting(() =>
        {
            res.Headers["Access-Control-Allow-Origin"] = "null";
            return Task.CompletedTask;
        });

        if (MutatingMethods.Contains(req.Method))
        {
            if (req.Headers["X-C2F-Admin"].ToString() != "1")
            {
                res.StatusCode = StatusCodes.Status400BadRequest;
                await res.WriteAsync("missing X-C2F-Admin");
                return;
            }
        }

        await _next(context);
    }

    private static bool IsAllowedOrigin(string origin)
    {
        if (origin == "http://127.0.0.1:8787") return true;
        if (origin == "http://127.0.0.1:5173" &&
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("C2F_DEV")))
            return true;
        return false;
    }
}
