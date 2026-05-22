# 07 — Endpoints and hosting

## Framework

ASP.NET Core 10 Minimal API. Single `Program.cs`. No MVC controllers, no Razor, no SignalR.

## Routes

| Method | Path | Description |
|---|---|---|
| POST | `/v1/messages` | Main translation endpoint. Streaming or non-streaming based on request `stream`. |
| POST | `/v1/messages/count_tokens` | Local token count (see [08](./08-token-counting.md)). |
| GET  | `/health` | Plain text `"ok"`, HTTP 200. For container health checks. |

No other routes. No `/v1/models`, no `/v1/complete` (Anthropic's legacy endpoint), no admin surface.

## Request pipeline

```
Inbound HTTP
  → JSON deserialize (System.Text.Json source-gen)
  → adapter validation [05]
  → assign correlation id, attach to ILogger scope
  → RequestTranslator.Translate (pure)
  → if stream: stream backend call + StreamTranslator
    else:      non-stream backend call + ResponseTranslator
  → write Anthropic-shaped response
  → log one-line summary
```

## Streaming response handler

Pseudocode:

```csharp
app.MapPost("/v1/messages", async (HttpContext ctx, AnthropicMessagesRequest req, ...) => {
  // ...validation, translation...
  if (req.Stream == true) {
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    ctx.Response.Headers["x-c2f-request-id"] = correlationId;

    var upstream = await backend.OpenStream(openaiReq, ct);
    // Peek first chunk to detect early upstream errors before SSE commit.
    // If early error: clear headers, write JSON error.
    // Else: write Anthropic SSE frames from StreamTranslator.

    await foreach (var sseFrame in StreamTranslator.Translate(upstream, req, target, cfg, ct)) {
      await ctx.Response.WriteAsync(sseFrame, ct);
      await ctx.Response.Body.FlushAsync(ct);
    }
  } else {
    var upstream = await backend.PostJson(openaiReq, ct);
    var anthropic = ResponseTranslator.Translate(upstream, req, target, cfg);
    ctx.Response.Headers["x-c2f-request-id"] = correlationId;
    return Results.Json(anthropic, OptionsForAnthropic);
  }
});
```

The "peek first chunk" detail matters: see [04](./04-streaming-translation.md) for the rule about emitting JSON error vs SSE error event. Implement by reading the upstream response status code and `Content-Type` before subscribing to its body stream — if it's a non-2xx JSON response, treat as non-streaming error path.

## Kestrel configuration

In `Program.cs` builder:

```csharp
builder.WebHost.ConfigureKestrel(opts => {
  opts.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
  opts.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
  opts.Limits.MaxRequestBodySize = 64 * 1024 * 1024;  // 64 MiB — Anthropic allows big tool_result image payloads
});
```

Default listen URL `http://127.0.0.1:8787`, overridable via `Kestrel:Endpoints:Http:Url` (per [06](./06-config.md)).

## Backend HTTP client

Single `HttpClient` registered as singleton, configured:

- `BaseAddress = ProxyConfig.BackendUrl`.
- Default `api-key` header from env var lookup at startup.
- `Timeout = TimeSpan.FromSeconds(ProxyConfig.Timeouts.OutboundTotalSeconds)` (10 min default) — for non-streaming.
- For streaming: do not rely on `HttpClient.Timeout`; cancellation comes from the per-chunk 60s idle timer in `StreamTranslator`. Use `HttpCompletionOption.ResponseHeadersRead` so the body stream isn't buffered.
- Enable HTTP/2 (`DefaultRequestVersion = HttpVersion.Version20`, `DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher`). Foundry supports HTTP/2.

## Cancellation

Inbound `HttpContext.RequestAborted` propagates to the outbound `HttpClient` call. Client disconnect tears down the upstream request. Test: kill `claude` mid-stream → adapter logs cancellation at `Information`, releases the Foundry connection.

## Concurrency

No per-process throttle in v1. Kestrel's default is plenty for a single-developer adapter. Foundry rate-limits server-side via 429.

## Packaging

Single `dotnet publish` profile per OS:

```
dotnet publish -c Release -r win-x64   --self-contained true /p:PublishSingleFile=true
dotnet publish -c Release -r linux-x64 --self-contained true /p:PublishSingleFile=true
dotnet publish -c Release -r osx-arm64 --self-contained true /p:PublishSingleFile=true
```

Output: one executable plus `appsettings.json` plus an optional `tokenizers/` folder. Operator copies the three to a directory and runs the executable.

NativeAOT (`/p:PublishAot=true`) is a stretch goal — requires no reflection-heavy JSON paths. Source-gen serializer makes it viable but isn't a v1 requirement.

## Local launch

```
set FOUNDRY_API_KEY=<key>
.\Claude2Foundry.exe
```

Logs to stdout. Ctrl-C to stop.
