using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claude2Foundry.Backend;
using Claude2Foundry.Config;
using Claude2Foundry.Monitor;
using Claude2Foundry.Protocol.Anthropic;
using Microsoft.Extensions.Options;

namespace Claude2Foundry.Admin;

public static class AdminApi
{
    private static readonly DateTimeOffset StartTime = DateTimeOffset.UtcNow;

    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/config", GetConfig);
        routes.MapGet("/config/schema", GetConfigSchema);
        routes.MapPost("/config", PostConfig);
        routes.MapPost("/config/test-connection", (Delegate)PostTestConnection);
        routes.MapPost("/restart", PostRestart);
        routes.MapGet("/health", GetHealth);
        routes.MapGet("/events", GetEvents);
        routes.MapGet("/events/full/{id}", GetEventsFull);
        routes.MapPost("/capture-mode", PostCaptureMode);
        routes.MapPost("/test-request", (Delegate)PostTestRequest);
    }

    // GET /api/admin/config
    private static IResult GetConfig(IOptionsMonitor<ProxyConfig> options) =>
        Results.Json(new { proxy = MaskConfig(options.CurrentValue) });

    // GET /api/admin/config/schema
    private static IResult GetConfigSchema() =>
        Results.Json(ConfigSchemaProvider.Schema);

    // POST /api/admin/config
    private static async Task<IResult> PostConfig(
        HttpContext ctx,
        IOptionsMonitor<ProxyConfig> options,
        ConfigWriter writer,
        RestartCoordinator restart,
        [FromKeyedServices("dataDir")] string dataDir)
    {
        JsonNode? body;
        try { body = await JsonNode.ParseAsync(ctx.Request.Body); }
        catch { return BadRequest("invalid_request", "Malformed JSON"); }

        var proxyNode = body?["proxy"];
        if (proxyNode is null) return BadRequest("invalid_request", "Missing 'proxy' field");

        ProxyConfig? incoming;
        try { incoming = proxyNode.Deserialize<ProxyConfig>(); }
        catch { incoming = null; }
        if (incoming is null) return BadRequest("invalid_request", "Could not deserialize proxy config");

        var issues = ConfigValidation.ValidateForApi(incoming);
        if (issues.Count > 0)
            return Results.Json(new
            {
                error = new
                {
                    type = "invalid_config",
                    issues = issues.Select(i => new { path = i.Path, message = i.Message }).ToArray()
                }
            }, statusCode: 400);

        var current = options.CurrentValue;
        var changedBootstrap = new List<string>();
        if (incoming.BackendUrl != current.BackendUrl) changedBootstrap.Add("Proxy.BackendUrl");
        if (incoming.ApiKeyEnv != current.ApiKeyEnv) changedBootstrap.Add("Proxy.ApiKeyEnv");

        try { await writer.WriteAsync(dataDir, incoming, ctx.RequestAborted); }
        catch (Exception ex) { return Results.Json(new { error = new { type = "write_failed", message = ex.Message } }, statusCode: 500); }

        return Results.Json(new { ok = true, restartRequired = changedBootstrap.Count > 0, fields = changedBootstrap.ToArray() });
    }

    // POST /api/admin/config/test-connection
    private static async Task<IResult> PostTestConnection(HttpContext ctx)
    {
        JsonNode? body;
        try { body = await JsonNode.ParseAsync(ctx.Request.Body); }
        catch { return BadRequest("invalid_request", "Malformed JSON"); }

        ProxyConfig? proposed;
        try { proposed = body?["proxy"]?.Deserialize<ProxyConfig>(); }
        catch { proposed = null; }

        if (proposed is null || string.IsNullOrWhiteSpace(proposed.BackendUrl) || string.IsNullOrWhiteSpace(proposed.ApiKeyEnv))
            return BadRequest("invalid_request", "Missing required BackendUrl or ApiKeyEnv");

        var apiKey = Environment.GetEnvironmentVariable(proposed.ApiKeyEnv) ?? "";
        using var client = new HttpClient
        {
            BaseAddress = new Uri(proposed.BackendUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.Add("api-key", apiKey);

        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await client.GetAsync("models", ctx.RequestAborted);
            sw.Stop();
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync();
                return Results.Json(new
                {
                    error = new
                    {
                        type = "foundry_unreachable",
                        message = $"{(int)resp.StatusCode} {resp.ReasonPhrase}: {errBody[..Math.Min(errBody.Length, 200)]}",
                        latencyMs = (int)sw.ElapsedMilliseconds
                    }
                }, statusCode: 502);
            }
            var respBody = await resp.Content.ReadAsStringAsync();
            int modelCount = 0;
            try
            {
                using var doc = JsonDocument.Parse(respBody);
                if (doc.RootElement.TryGetProperty("data", out var data)) modelCount = data.GetArrayLength();
            }
            catch { /* best effort */ }
            return Results.Json(new { ok = true, latencyMs = (int)sw.ElapsedMilliseconds, modelCount });
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Results.Json(new
            {
                error = new { type = "foundry_unreachable", message = ex.Message, latencyMs = (int)sw.ElapsedMilliseconds }
            }, statusCode: 502);
        }
    }

    // POST /api/admin/restart
    private static IResult PostRestart(RestartCoordinator restart, ILogger<Program> logger)
    {
        if (!restart.WrapperPresent)
            return Results.Json(new
            {
                error = new { type = "wrapper_absent", message = "C2F_WRAPPER env var not set" }
            }, statusCode: 409);

        _ = Task.Run(async () =>
        {
            await Task.Delay(50); // let response flush
            await restart.InitiateRestartAsync(logger);
        });
        return Results.Json(new { draining = true, graceMs = 60_000 });
    }

    // GET /api/admin/health
    private static async Task<IResult> GetHealth(
        FoundryHealthProbe probe,
        RestartCoordinator restart,
        RequestCapturePipeline pipeline,
        CaptureModeController captureMode,
        JsonlWriter jsonlWriter,
        IOptionsMonitor<ProxyConfig> options,
        [FromKeyedServices("dataDir")] string dataDir,
        CancellationToken ct)
    {
        var health = await probe.GetAsync(ct);
        var cfg = options.CurrentValue;
        var uptimeSec = (int)(DateTimeOffset.UtcNow - StartTime).TotalSeconds;
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

        object? logFile = null;
        var currentLog = jsonlWriter.CurrentFilePath();
        if (currentLog is not null)
        {
            long sizeBytes = 0;
            try { sizeBytes = new FileInfo(currentLog).Length; } catch { /* best effort */ }
            logFile = new { path = currentLog, sizeBytes };
        }

        string? localFileError = null;
        var localPath = Path.Combine(dataDir, "appsettings.local.json");
        if (File.Exists(localPath))
        {
            try { JsonDocument.Parse(File.ReadAllText(localPath)).Dispose(); }
            catch (JsonException ex) { localFileError = ex.Message; }
        }

        return Results.Json(new
        {
            uptimeSec,
            version,
            dotnetVersion = Environment.Version.ToString(),
            dataDir,
            foundry = new
            {
                reachable = health.Reachable,
                lastProbeMs = health.LastProbeMs,
                lastProbeAt = health.LastProbeAt,
                lastFailureAt = health.LastFailureAt,
                lastFailureMessage = health.LastFailureMessage
            },
            config = new { proxy = MaskConfig(cfg), localFileError },
            inFlight = restart.InFlightCount,
            ringBuffer = new { occupancy = pipeline.RingOccupancy, capacity = pipeline.RingCapacityMax },
            capture = new { mode = captureMode.EffectiveMode, scope = "session" },
            logFile,
            wrapperPresent = restart.WrapperPresent
        });
    }

    // GET /api/admin/events
    private static async Task GetEvents(
        HttpContext ctx,
        RequestCapturePipeline pipeline)
    {
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        DateTimeOffset? since = null;
        if (ctx.Request.Query.TryGetValue("since", out var sinceStr) &&
            DateTimeOffset.TryParse(sinceStr, out var sinceVal))
            since = sinceVal;

        var ct = ctx.RequestAborted;
        var writer = ctx.Response.Body;

        using var heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        _ = Task.Run(async () =>
        {
            try
            {
                while (await heartbeatTimer.WaitForNextTickAsync(ct))
                {
                    await writer.WriteAsync(Encoding.UTF8.GetBytes(": ping\n\n"), ct);
                    await writer.FlushAsync(ct);
                }
            }
            catch { /* client disconnected */ }
        }, ct);

        try
        {
            await foreach (var frame in pipeline.SubscribeAsync(since, ct))
            {
                var sse = $"id: {frame.Seq}\nevent: {frame.Event}\ndata: {frame.Data}\n\n";
                await writer.WriteAsync(Encoding.UTF8.GetBytes(sse), ct);
                await writer.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* client disconnected */ }
    }

    // GET /api/admin/events/full/{id}
    private static async Task<IResult> GetEventsFull(
        string id,
        FullBodyCache cache,
        JsonlWriter jsonlWriter,
        CancellationToken ct)
    {
        var record = cache.Get(id);
        if (record is not null) return Results.Json(record);

        var recovered = await jsonlWriter.FindByIdAsync(id, ct);
        if (recovered is not null) return Results.Json(recovered);

        return Results.Json(new { expired = true });
    }

    // POST /api/admin/capture-mode
    private static async Task<IResult> PostCaptureMode(
        HttpContext ctx,
        CaptureModeController captureMode,
        [FromKeyedServices("dataDir")] string dataDir)
    {
        JsonNode? body;
        try { body = await JsonNode.ParseAsync(ctx.Request.Body); }
        catch { return BadRequest("invalid_request", "Malformed JSON"); }

        var mode = body?["mode"]?.GetValue<string>();
        var scope = body?["scope"]?.GetValue<string>();

        if (mode != "hybrid" && mode != "full")
            return BadRequest("invalid_request", "mode must be 'hybrid' or 'full'");
        if (scope != "session" && scope != "persistent")
            return BadRequest("invalid_request", "scope must be 'session' or 'persistent'");

        if (scope == "persistent")
            await captureMode.SetPersistentAsync(dataDir, mode, ctx.RequestAborted);
        else
            captureMode.SetSession(mode);

        return Results.Json(new { ok = true, mode, scope });
    }

    // POST /api/admin/test-request
    private static async Task<IResult> PostTestRequest(
        HttpContext ctx,
        TestRequestRunner runner)
    {
        AnthropicMessagesRequest? req;
        try { req = await JsonSerializer.DeserializeAsync<AnthropicMessagesRequest>(ctx.Request.Body); }
        catch { return BadRequest("invalid_request", "Malformed JSON"); }

        if (req is null || req.Messages.Count == 0)
            return BadRequest("invalid_request", "At least one message is required");

        // Force non-streaming for test requests
        if (req.Stream == true)
            req = new AnthropicMessagesRequest
            {
                Model = req.Model,
                Messages = req.Messages,
                System = req.System,
                MaxTokens = req.MaxTokens,
                Tools = req.Tools,
                ToolChoice = req.ToolChoice,
                Thinking = req.Thinking,
                Stream = false
            };

        try
        {
            var result = await runner.RunAsync(req, ctx.RequestAborted);
            return Results.Json(new
            {
                anthropicResponse = result.AnthropicResponse,
                openaiRequest = result.OpenaiRequest,
                openaiResponse = result.OpenaiResponse,
                elapsedMs = result.ElapsedMs
            });
        }
        catch (TestRequestException ex)
        {
            return Results.Json(new { error = new { type = "test_request_failed", message = ex.Message } }, statusCode: ex.StatusCode);
        }
    }

    private static object MaskConfig(ProxyConfig cfg)
    {
        var resolved = Environment.GetEnvironmentVariable(cfg.ApiKeyEnv);
        if (string.IsNullOrEmpty(resolved)) return cfg;
        var json = JsonSerializer.Serialize(cfg);
        return JsonNode.Parse(json.Replace(resolved, "***"))!;
    }

    private static IResult BadRequest(string type, string message) =>
        Results.Json(new { error = new { type, message } }, statusCode: 400);
}
