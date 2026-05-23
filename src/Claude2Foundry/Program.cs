using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Claude2Foundry.Admin;
using Claude2Foundry.Backend;
using Claude2Foundry.Config;
using Claude2Foundry.Errors;
using Claude2Foundry.Logging;
using Claude2Foundry.Protocol;
using Claude2Foundry.Protocol.Anthropic;
using Claude2Foundry.Protocol.OpenAI;
using Claude2Foundry.Tokens;
using Claude2Foundry.Translation;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(opts =>
{
    opts.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
    opts.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
    opts.Limits.MaxRequestBodySize = 64 * 1024 * 1024;
});

builder.Services.Configure<ProxyConfig>(builder.Configuration.GetSection("Proxy"));
builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<IOptions<ProxyConfig>>().Value;
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

builder.Services.ConfigureHttpJsonOptions(opts =>
{
    opts.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

var app = builder.Build();

try { _ = app.Services.GetRequiredService<ProxyConfig>(); }
catch (Exception ex) { app.Logger.LogCritical(ex, "Startup failed"); Environment.Exit(1); }

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseWhen(
    ctx => ctx.Request.Path.StartsWithSegments("/api/admin"),
    adminBranch => adminBranch.UseMiddleware<CorsAndCsrfGuard>());
LogStartupBanner(app, builder.Configuration);

app.MapGet("/health", () => Results.Text("ok"));
app.MapGet("/_ui/{**path}", () => Results.NotFound());

var adminGroup = app.MapGroup("/api/admin");
AdminApi.Map(adminGroup);

app.MapPost("/v1/messages/count_tokens", async (HttpContext ctx, TokenCounter counter) =>
{
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

app.MapPost("/v1/messages", async (HttpContext ctx, RequestTranslator reqT, ResponseTranslator respT,
    StreamTranslator streamT, FoundryClient foundry, ProxyConfig cfg, ILogger<Program> logger) =>
{
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

    ChatCompletionRequest openaiReq;
    string resolvedTarget;
    try { (openaiReq, resolvedTarget) = reqT.Translate(req); }
    catch (AdapterException ex) { return ErrorResult(ErrorMapping.AdapterError("invalid_request_error", ex.Message), 400, ctx); }

    if (logger.IsEnabled(LogLevel.Debug))
        logger.LogDebug("req={CorrelationId} OpenAI request: {Body}", correlationId,
            JsonSerializer.Serialize(openaiReq, AppJsonSerializerContext.Default.ChatCompletionRequest));

    var idleTimeout = TimeSpan.FromSeconds(cfg.Timeouts.StreamIdleSeconds);

    if (req.Stream == true)
    {
        var upstream = foundry.StreamAsync(openaiReq, correlationId, idleTimeout, ct);
        var enumerator = upstream.GetAsyncEnumerator(ct);
        bool hasFirst;
        try { hasFirst = await enumerator.MoveNextAsync(); }
        catch (FoundryHttpException ex)
        {
            await enumerator.DisposeAsync();
            var (_, httpStatus) = ErrorMapping.MapFoundryStatus(ex.Status);
            return ErrorResult(ErrorMapping.FoundryError(ex.Status, ex.FoundryMessage), httpStatus, ctx);
        }
        catch (Exception ex)
        {
            await enumerator.DisposeAsync();
            logger.LogError(ex, "req={CorrelationId} Stream open failed", correlationId);
            return ErrorResult(ErrorMapping.AdapterError("api_error", $"Internal error ({correlationId})"), 500, ctx);
        }

        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        ctx.Response.Headers["x-c2f-request-id"] = correlationId;

        try
        {
            if (hasFirst)
            {
                await foreach (var frame in streamT.Translate(Reattach(enumerator.Current, enumerator, ct), req, resolvedTarget, ct))
                {
                    await ctx.Response.WriteAsync(frame, ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
        }
        catch (OperationCanceledException) { logger.LogInformation("req={CorrelationId} Client disconnected", correlationId); }
        catch (TimeoutException ex)
        {
            logger.LogError("req={CorrelationId} {Msg}", correlationId, ex.Message);
            await ctx.Response.WriteAsync(ErrorMapping.SseErrorFrame(ErrorMapping.AdapterError("api_error", ex.Message)), CancellationToken.None);
        }
        catch (FoundryHttpException ex) { await ctx.Response.WriteAsync(ErrorMapping.SseErrorFrame(ErrorMapping.FoundryError(ex.Status, ex.FoundryMessage)), CancellationToken.None); }
        catch (Exception ex)
        {
            logger.LogError(ex, "req={CorrelationId} Mid-stream error", correlationId);
            await ctx.Response.WriteAsync(ErrorMapping.SseErrorFrame(ErrorMapping.AdapterError("api_error", $"Internal error ({correlationId})")), CancellationToken.None);
        }
        finally { await enumerator.DisposeAsync(); }

        logger.LogInformation("<= req={CorrelationId} status=200 elapsed={Elapsed}ms stream=true", correlationId, sw.ElapsedMilliseconds);
        return Results.Empty;
    }
    else
    {
        ChatCompletionResponse upstream;
        try { upstream = await foundry.PostAsync(openaiReq, correlationId, ct); }
        catch (FoundryHttpException ex)
        {
            var (_, httpStatus) = ErrorMapping.MapFoundryStatus(ex.Status);
            logger.LogInformation("<= req={CorrelationId} status={Status} elapsed={Elapsed}ms stream=false", correlationId, httpStatus, sw.ElapsedMilliseconds);
            return ErrorResult(ErrorMapping.FoundryError(ex.Status, ex.FoundryMessage), httpStatus, ctx);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "req={CorrelationId} Outbound timeout", correlationId);
            return ErrorResult(ErrorMapping.AdapterError("api_error", $"Adapter timeout after {cfg.Timeouts.OutboundTotalSeconds}s"), 500, ctx);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "req={CorrelationId} Foundry call failed", correlationId);
            return ErrorResult(ErrorMapping.AdapterError("api_error", $"Internal error ({correlationId})"), 500, ctx);
        }

        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("req={CorrelationId} OpenAI response: {Body}", correlationId,
                JsonSerializer.Serialize(upstream, AppJsonSerializerContext.Default.ChatCompletionResponse));

        AnthropicMessagesResponse anthropicResp;
        try { anthropicResp = respT.Translate(upstream, req, resolvedTarget); }
        catch (AdapterException ex) { return ErrorResult(ErrorMapping.AdapterError("api_error", ex.Message), 500, ctx); }

        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("req={CorrelationId} Anthropic response: {Body}", correlationId,
                JsonSerializer.Serialize(anthropicResp, AppJsonSerializerContext.Default.AnthropicMessagesResponse));

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
        "      Timeouts: outbound={Out}s stream-idle={Idle}s",
        url, cfg.BackendUrl, cfg.ApiKeyEnv, apiKeyStatus,
        cfg.DefaultModel,
        cfg.ModelAliases.Count,
        cfg.ReasoningPolicies.Count, noneCount, passthroughCount, effortCount,
        cfg.Tokenizers.Count, cl100kCount, o200kCount, hfCount,
        cfg.Timeouts.OutboundTotalSeconds, cfg.Timeouts.StreamIdleSeconds);
}

public partial class Program { }
