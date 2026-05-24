using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Claude2Foundry.Admin;
using Claude2Foundry.Backend;
using Claude2Foundry.Config;
using Claude2Foundry.Errors;
using Claude2Foundry.Logging;
using Claude2Foundry.Monitor;
using Claude2Foundry.Protocol;
using Claude2Foundry.Protocol.Anthropic;
using Claude2Foundry.Protocol.OpenAI;
using Claude2Foundry.Tokens;
using Claude2Foundry.Translation;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// --- Data dir (resolved before config so it can be used as config source path) ---
var dataDir = DataDirResolver.ResolveDataDir(builder.Environment);

// --- Configuration layering: base + optional local override ---
builder.Configuration.AddJsonFile(
    Path.Combine(dataDir, "appsettings.local.json"),
    optional: true,
    reloadOnChange: true);

builder.WebHost.ConfigureKestrel(opts =>
{
    opts.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
    opts.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
    opts.Limits.MaxRequestBodySize = 64 * 1024 * 1024;
});

// Expose ProxyConfig as IOptionsMonitor for hot-reload awareness
builder.Services.Configure<ProxyConfig>(builder.Configuration.GetSection("Proxy"));

// Singleton ProxyConfig snapshot (validated at startup; used by translators on hot-path)
builder.Services.AddSingleton(sp =>
{
    var monitor = sp.GetRequiredService<IOptionsMonitor<ProxyConfig>>();
    var cfg = monitor.CurrentValue;
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Claude2Foundry.Config.ConfigValidation");
    ConfigValidation.Validate(cfg, logger);
    return cfg;
});

builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<ProxyConfig>();
    var apiKey = Environment.GetEnvironmentVariable(cfg.ApiKeyEnv)!;

    var handler = new FoundryLoggingHandler { InnerHandler = new HttpClientHandler() };
    var client = new HttpClient(handler)
    {
        BaseAddress = new Uri(cfg.BackendUrl.TrimEnd('/') + "/"),
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        Timeout = TimeSpan.FromSeconds(cfg.Timeouts.OutboundTotalSeconds),
    };
    client.DefaultRequestHeaders.Add("api-key", apiKey);
    return client;
});

builder.Services.AddSingleton<FoundryClient>();
builder.Services.AddSingleton<RequestTranslator>();
builder.Services.AddSingleton<ResponseTranslator>();
builder.Services.AddSingleton<StreamTranslator>();
builder.Services.AddSingleton<TokenCounter>();

// --- Admin + Monitor services ---
builder.Services.AddKeyedSingleton<string>("dataDir", dataDir);
builder.Services.AddSingleton<ConfigWriter>();
builder.Services.AddSingleton<IExitSink, EnvironmentExitSink>();
builder.Services.AddSingleton<RestartCoordinator>();
builder.Services.AddSingleton<FoundryHealthProbe>();
builder.Services.AddSingleton<FullBodyCache>();
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptionsMonitor<ProxyConfig>>();
    var logger = sp.GetRequiredService<ILogger<JsonlWriter>>();
    var writer = new JsonlWriter(dataDir, options, logger);
    return writer;
});
builder.Services.AddSingleton<CaptureModeController>();
builder.Services.AddSingleton(sp =>
{
    var jsonlWriter = sp.GetRequiredService<JsonlWriter>();
    var bodyCache = sp.GetRequiredService<FullBodyCache>();
    var pipelineLogger = sp.GetRequiredService<ILogger<RequestCapturePipeline>>();
    return new RequestCapturePipeline(jsonlWriter, bodyCache, pipelineLogger);
});
builder.Services.AddSingleton<IRequestCaptureSink>(sp =>
    sp.GetRequiredService<RequestCapturePipeline>());
builder.Services.AddSingleton<TestRequestRunner>();

builder.Services.ConfigureHttpJsonOptions(opts =>
{
    opts.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

var app = builder.Build();

// Start background pipeline services
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var pipeline = app.Services.GetRequiredService<RequestCapturePipeline>();
var jsonlWriter = app.Services.GetRequiredService<JsonlWriter>();

lifetime.ApplicationStarted.Register(() =>
{
    pipeline.Start(lifetime.ApplicationStopping);
    jsonlWriter.Start(lifetime.ApplicationStopping);
});

lifetime.ApplicationStopping.Register(() =>
{
    pipeline.DrainAsync().GetAwaiter().GetResult();
    jsonlWriter.DrainAsync().GetAwaiter().GetResult();
});

try { _ = app.Services.GetRequiredService<ProxyConfig>(); }
catch (Exception ex) { app.Logger.LogCritical(ex, "Startup failed"); Environment.Exit(1); }

app.UseMiddleware<CorrelationIdMiddleware>();

// Admin CORS+CSRF middleware + routes
app.Map("/api/admin", adminApp =>
{
    adminApp.UseMiddleware<CorsAndCsrfGuard>();
    adminApp.UseRouting();
    adminApp.UseEndpoints(endpoints =>
    {
        var group = endpoints.MapGroup("");
        AdminApi.Map(group);
    });
});

LogStartupBanner(app, builder.Configuration);

app.MapGet("/health", async (FoundryHealthProbe probe, CancellationToken ct) =>
{
    var status = await probe.GetAsync(ct);
    return Results.Text(status.Reachable ? "ok" : "degraded");
});

ManifestEmbeddedFileProvider? uiProvider = null;
try { uiProvider = new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot/_ui/dist"); }
catch { /* UI not embedded; /_ui/ returns 404 until next build with embedded assets */ }
var mimeMap = new FileExtensionContentTypeProvider();

app.MapGet("/_ui/{**path}", async (HttpContext ctx, string? path) =>
{
    if (uiProvider is null) return Results.NotFound();
    var requested = string.IsNullOrEmpty(path) ? "index.html" : path;
    var file = uiProvider.GetFileInfo(requested);
    if (!file.Exists) file = uiProvider.GetFileInfo("index.html"); // SPA fallback
    if (!file.Exists) return Results.NotFound();
    if (!mimeMap.TryGetContentType(file.Name, out var contentType))
        contentType = "application/octet-stream";
    ctx.Response.ContentType = contentType;
    await using var stream = file.CreateReadStream();
    await stream.CopyToAsync(ctx.Response.Body);
    return Results.Empty;
});

app.MapPost("/v1/messages/count_tokens", async (
    HttpContext ctx,
    TokenCounter counter,
    RestartCoordinator restartCoordinator) =>
{
    using var _ = restartCoordinator.TrackRequest();
    var correlationId = ctx.GetCorrelationId();
    var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();

    AnthropicCountTokensRequest? req;
    try { req = await ctx.Request.ReadFromJsonAsync(AppJsonSerializerContext.Default.AnthropicCountTokensRequest); }
    catch (JsonException ex) { return ErrorResult(ErrorMapping.AdapterError("invalid_request_error", $"Malformed request: {ex.Message}"), 400, ctx); }

    if (req is null) return ErrorResult(ErrorMapping.AdapterError("invalid_request_error", "Malformed request: empty body"), 400, ctx);
    if (req.Messages.Count == 0) return ErrorResult(ErrorMapping.AdapterError("invalid_request_error", "At least one message is required"), 400, ctx);

    try
    {
        var count = counter.Count(req);
        return Results.Json(new AnthropicCountTokensResponse { InputTokens = count }, AppJsonSerializerContext.Default.AnthropicCountTokensResponse);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "req={CorrelationId} Token count failed", correlationId);
        return ErrorResult(ErrorMapping.AdapterError("api_error", $"Internal error ({correlationId})"), 500, ctx);
    }
});

app.MapPost("/v1/messages", async (
    HttpContext ctx,
    RequestTranslator reqT,
    ResponseTranslator respT,
    StreamTranslator streamT,
    FoundryClient foundry,
    IOptionsMonitor<ProxyConfig> optionsMonitor,
    IRequestCaptureSink sink,
    RestartCoordinator restartCoordinator,
    ILogger<Program> logger) =>
{
    using var _ = restartCoordinator.TrackRequest();
    var snapshot = optionsMonitor.CurrentValue;
    var correlationId = ctx.GetCorrelationId();
    var sw = Stopwatch.StartNew();
    var ct = ctx.RequestAborted;

    AnthropicMessagesRequest? req;
    try { req = await ctx.Request.ReadFromJsonAsync(AppJsonSerializerContext.Default.AnthropicMessagesRequest, ct); }
    catch (JsonException ex) { return ErrorResult(ErrorMapping.AdapterError("invalid_request_error", $"Malformed request: {ex.Message}"), 400, ctx); }

    if (req is null) return ErrorResult(ErrorMapping.AdapterError("invalid_request_error", "Malformed request: empty body"), 400, ctx);
    if (req.Messages.Count == 0) return ErrorResult(ErrorMapping.AdapterError("invalid_request_error", "At least one message is required"), 400, ctx);
    if (req.MaxTokens is null) return ErrorResult(ErrorMapping.AdapterError("invalid_request_error", "max_tokens is required"), 400, ctx);
    if (req.ToolChoice?.Type == "tool")
    {
        var toolNames = req.Tools?.Select(t => t.Name).ToHashSet() ?? [];
        if (req.ToolChoice.Name is null || !toolNames.Contains(req.ToolChoice.Name))
            return ErrorResult(ErrorMapping.AdapterError("invalid_request_error", "tool_choice.name not found in tools"), 400, ctx);
    }

    logger.LogInformation("=> req={CorrelationId} POST /v1/messages stream={Stream}", correlationId, req.Stream == true);

    // Emit request.received
    sink.Emit(new RequestReceivedEvent(
        correlationId,
        DateTimeOffset.UtcNow,
        req.Model,
        req.Stream == true,
        RequestRecordBuilder.RedactHeaders(ctx.Request.Headers),
        JsonSnapshot.Take(req)));

    ChatCompletionRequest openaiReq;
    string resolvedTarget;
    try { (openaiReq, resolvedTarget) = reqT.Translate(req, snapshot); }
    catch (AdapterException ex)
    {
        sink.Emit(new CaptureErrorEvent(correlationId, "translation", "Adapter", ex.Message));
        sink.Finalize(correlationId);
        return ErrorResult(ErrorMapping.AdapterError("invalid_request_error", ex.Message), 400, ctx);
    }

    // Emit request.translated
    sink.Emit(new RequestTranslatedEvent(correlationId, resolvedTarget, JsonSnapshot.Take(openaiReq)));

    if (logger.IsEnabled(LogLevel.Debug))
        logger.LogDebug("req={CorrelationId} OpenAI request: {Body}", correlationId,
            JsonSerializer.Serialize(openaiReq, AppJsonSerializerContext.Default.ChatCompletionRequest));

    var idleTimeout = TimeSpan.FromSeconds(snapshot.Timeouts.StreamIdleSeconds);
    int chunkSeq = 0;

    if (req.Stream == true)
    {
        // Emit foundry.request.sent
        sink.Emit(new FoundrySentEvent(correlationId, DateTimeOffset.UtcNow));

        var upstream = foundry.StreamAsync(openaiReq, correlationId, idleTimeout, ct);
        var enumerator = upstream.GetAsyncEnumerator(ct);
        bool hasFirst;
        try { hasFirst = await enumerator.MoveNextAsync(); }
        catch (FoundryHttpException ex)
        {
            await enumerator.DisposeAsync();
            var (_, httpStatus) = ErrorMapping.MapFoundryStatus(ex.Status);
            sink.Emit(new CaptureErrorEvent(correlationId, "foundry-sent", "Foundry", ex.FoundryMessage));
            sink.Finalize(correlationId);
            return ErrorResult(ErrorMapping.FoundryError(ex.Status, ex.FoundryMessage), httpStatus, ctx);
        }
        catch (Exception ex)
        {
            await enumerator.DisposeAsync();
            logger.LogError(ex, "req={CorrelationId} Stream open failed", correlationId);
            sink.Emit(new CaptureErrorEvent(correlationId, "foundry-sent", "Adapter", ex.Message));
            sink.Finalize(correlationId);
            return ErrorResult(ErrorMapping.AdapterError("api_error", $"Internal error ({correlationId})"), 500, ctx);
        }

        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        ctx.Response.Headers["x-c2f-request-id"] = correlationId;

        // Accumulate raw OpenAI chunks and Anthropic SSE frames for capture
        var rawChunks = new List<ChatCompletionChunk>();
        var sseFrames = new List<string>();

        try
        {
            if (hasFirst)
            {
                await foreach (var frame in streamT.Translate(TapChunks(Reattach(enumerator.Current, enumerator, ct), rawChunks), req, resolvedTarget, ct, snapshot))
                {
                    sink.Emit(new FoundryChunkEvent(correlationId, chunkSeq++, null, null, null));
                    sseFrames.Add(frame);
                    await ctx.Response.WriteAsync(frame, ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
            // Emit captured raw OpenAI chunks
            sink.Emit(new FoundryResponseReceivedEvent(correlationId, rawChunks.Count > 0 ? JsonSnapshot.Take(rawChunks) : null));
            // Emit assembled: store SSE frames so the drawer can show them
            sink.Emit(new ResponseSentEvent(correlationId, (int)sw.ElapsedMilliseconds,
                sseFrames.Count > 0 ? JsonSnapshot.Take(new { frames = sseFrames }) : null));
            sink.Finalize(correlationId);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("req={CorrelationId} Client disconnected", correlationId);
            sink.Finalize(correlationId, new OperationCanceledException("Client disconnected"));
        }
        catch (TimeoutException ex)
        {
            logger.LogError("req={CorrelationId} {Msg}", correlationId, ex.Message);
            await ctx.Response.WriteAsync(ErrorMapping.SseErrorFrame(ErrorMapping.AdapterError("api_error", ex.Message)), CancellationToken.None);
            sink.Emit(new CaptureErrorEvent(correlationId, "streaming", "Adapter", ex.Message));
            sink.Finalize(correlationId);
        }
        catch (FoundryHttpException ex)
        {
            await ctx.Response.WriteAsync(ErrorMapping.SseErrorFrame(ErrorMapping.FoundryError(ex.Status, ex.FoundryMessage)), CancellationToken.None);
            sink.Emit(new CaptureErrorEvent(correlationId, "streaming", "Foundry", ex.FoundryMessage));
            sink.Finalize(correlationId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "req={CorrelationId} Mid-stream error", correlationId);
            await ctx.Response.WriteAsync(ErrorMapping.SseErrorFrame(ErrorMapping.AdapterError("api_error", $"Internal error ({correlationId})")), CancellationToken.None);
            sink.Emit(new CaptureErrorEvent(correlationId, "streaming", "Adapter", ex.Message));
            sink.Finalize(correlationId);
        }
        finally { await enumerator.DisposeAsync(); }

        logger.LogInformation("<= req={CorrelationId} status=200 elapsed={Elapsed}ms stream=true", correlationId, sw.ElapsedMilliseconds);
        return Results.Empty;
    }
    else
    {
        // Emit foundry.request.sent
        sink.Emit(new FoundrySentEvent(correlationId, DateTimeOffset.UtcNow));

        ChatCompletionResponse upstream;
        try { upstream = await foundry.PostAsync(openaiReq, correlationId, ct); }
        catch (FoundryHttpException ex)
        {
            var (_, httpStatus) = ErrorMapping.MapFoundryStatus(ex.Status);
            logger.LogInformation("<= req={CorrelationId} status={Status} elapsed={Elapsed}ms stream=false", correlationId, httpStatus, sw.ElapsedMilliseconds);
            sink.Emit(new CaptureErrorEvent(correlationId, "foundry-sent", "Foundry", ex.FoundryMessage));
            sink.Finalize(correlationId);
            return ErrorResult(ErrorMapping.FoundryError(ex.Status, ex.FoundryMessage), httpStatus, ctx);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "req={CorrelationId} Outbound timeout", correlationId);
            sink.Emit(new CaptureErrorEvent(correlationId, "foundry-sent", "Adapter", "timeout"));
            sink.Finalize(correlationId);
            return ErrorResult(ErrorMapping.AdapterError("api_error", $"Adapter timeout after {snapshot.Timeouts.OutboundTotalSeconds}s"), 500, ctx);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "req={CorrelationId} Foundry call failed", correlationId);
            sink.Emit(new CaptureErrorEvent(correlationId, "foundry-sent", "Adapter", ex.Message));
            sink.Finalize(correlationId);
            return ErrorResult(ErrorMapping.AdapterError("api_error", $"Internal error ({correlationId})"), 500, ctx);
        }

        // Emit raw Foundry (OpenAI) response
        sink.Emit(new FoundryResponseReceivedEvent(correlationId, JsonSnapshot.Take(upstream)));

        // Emit foundry.complete
        var usage = upstream.Usage;
        sink.Emit(new FoundryCompleteEvent(
            correlationId,
            new UsageDto { Input = usage?.PromptTokens ?? 0, Output = usage?.CompletionTokens ?? 0 },
            upstream.Choices.FirstOrDefault()?.FinishReason ?? "stop"));

        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("req={CorrelationId} OpenAI response: {Body}", correlationId,
                JsonSerializer.Serialize(upstream, AppJsonSerializerContext.Default.ChatCompletionResponse));

        AnthropicMessagesResponse anthropicResp;
        try { anthropicResp = respT.Translate(upstream, req, resolvedTarget, snapshot); }
        catch (AdapterException ex)
        {
            sink.Emit(new CaptureErrorEvent(correlationId, "translation", "Adapter", ex.Message));
            sink.Finalize(correlationId);
            return ErrorResult(ErrorMapping.AdapterError("api_error", ex.Message), 500, ctx);
        }

        sink.Emit(new ResponseSentEvent(correlationId, (int)sw.ElapsedMilliseconds, JsonSnapshot.Take(anthropicResp)));
        sink.Finalize(correlationId);

        logger.LogInformation("<= req={CorrelationId} status=200 elapsed={Elapsed}ms in={In} out={Out} stream=false",
            correlationId, sw.ElapsedMilliseconds, anthropicResp.Usage.InputTokens, anthropicResp.Usage.OutputTokens);
        return Results.Json(anthropicResp, AppJsonSerializerContext.Default.AnthropicMessagesResponse);
    }
});

app.Run();

static IResult ErrorResult(AnthropicErrorResponse err, int status, HttpContext ctx)
{
    ctx.Response.Headers["x-c2f-request-id"] = ctx.GetCorrelationId();
    return Results.Json(err, AppJsonSerializerContext.Default.AnthropicErrorResponse, statusCode: status);
}

static async IAsyncEnumerable<ChatCompletionChunk> Reattach(
    ChatCompletionChunk first, IAsyncEnumerator<ChatCompletionChunk> rest,
    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
{
    yield return first;
    while (await rest.MoveNextAsync()) yield return rest.Current;
}

static async IAsyncEnumerable<ChatCompletionChunk> TapChunks(
    IAsyncEnumerable<ChatCompletionChunk> source,
    List<ChatCompletionChunk> sink,
    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
{
    await foreach (var chunk in source.WithCancellation(ct))
    {
        sink.Add(chunk);
        yield return chunk;
    }
}

static void LogStartupBanner(WebApplication app, IConfiguration config)
{
    var cfg = app.Services.GetRequiredService<ProxyConfig>();
    var url = config["Kestrel:Endpoints:Http:Url"] ?? "http://127.0.0.1:8787";
    var apiKeyStatus = string.IsNullOrEmpty(Environment.GetEnvironmentVariable(cfg.ApiKeyEnv)) ? "missing" : "present";

    var noneCount = cfg.ReasoningPolicies.Values.Count(v => v == "none");
    var passthroughCount = cfg.ReasoningPolicies.Values.Count(v => v == "passthrough");
    var effortCount = cfg.ReasoningPolicies.Values.Count(v => v == "effort");
    var cl100kCount = cfg.Tokenizers.Values.Count(v => v.Source == "TiktokenCl100k");
    var o200kCount = cfg.Tokenizers.Values.Count(v => v.Source == "TiktokenO200k");
    var hfCount = cfg.Tokenizers.Values.Count(v => v.Source == "HuggingFace");

    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Claude2Foundry.Startup");
    logger.LogInformation(
        "Listening on {Url}\n" +
        "      Backend: {Backend} (api-key from env {ApiKeyEnv}: {ApiKeyStatus})\n" +
        "      Default model: {Default}\n" +
        "      Aliases: {Aliases} entries\n" +
        "      Reasoning policies: {Policies} entries (none={None}, passthrough={Passthrough}, effort={Effort})\n" +
        "      Tokenizers: {Toks} entries (TiktokenCl100k={Cl100k}, TiktokenO200k={O200k}, HuggingFace={HF})\n" +
        "      Timeouts: outbound={Out}s stream-idle={Idle}s\n" +
        "      Admin console: {Url}/_ui/",
        url, cfg.BackendUrl, cfg.ApiKeyEnv, apiKeyStatus,
        cfg.DefaultModel,
        cfg.ModelAliases.Count,
        cfg.ReasoningPolicies.Count, noneCount, passthroughCount, effortCount,
        cfg.Tokenizers.Count, cl100kCount, o200kCount, hfCount,
        cfg.Timeouts.OutboundTotalSeconds, cfg.Timeouts.StreamIdleSeconds,
        url);
}

public partial class Program { }
