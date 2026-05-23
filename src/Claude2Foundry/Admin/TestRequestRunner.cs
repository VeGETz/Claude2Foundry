using System.Diagnostics;
using System.Text.Json;
using Claude2Foundry.Backend;
using Claude2Foundry.Config;
using Claude2Foundry.Errors;
using Claude2Foundry.Protocol;
using Claude2Foundry.Protocol.Anthropic;
using Claude2Foundry.Protocol.OpenAI;
using Claude2Foundry.Translation;

namespace Claude2Foundry.Admin;

public sealed class TestRequestRunner(
    RequestTranslator reqT,
    ResponseTranslator respT,
    FoundryClient foundry,
    ILogger<TestRequestRunner> logger)
{
    public async Task<TestRequestResult> RunAsync(AnthropicMessagesRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        ChatCompletionRequest openaiReq;
        string resolvedTarget;
        try { (openaiReq, resolvedTarget) = reqT.Translate(req); }
        catch (AdapterException ex) { throw new TestRequestException(400, ex.Message); }

        var openaiReqJson = JsonSerializer.SerializeToNode(openaiReq);

        ChatCompletionResponse upstream;
        try { upstream = await foundry.PostAsync(openaiReq, "test-request", ct); }
        catch (FoundryHttpException ex) { throw new TestRequestException(502, ex.FoundryMessage); }

        var openaiRespJson = JsonSerializer.SerializeToNode(upstream);

        AnthropicMessagesResponse anthropicResp;
        try { anthropicResp = respT.Translate(upstream, req, resolvedTarget); }
        catch (AdapterException ex) { throw new TestRequestException(500, ex.Message); }

        sw.Stop();
        return new TestRequestResult(
            JsonSerializer.SerializeToNode(anthropicResp),
            openaiReqJson,
            openaiRespJson,
            (int)sw.ElapsedMilliseconds);
    }
}

public sealed record TestRequestResult(
    object? AnthropicResponse,
    object? OpenaiRequest,
    object? OpenaiResponse,
    int ElapsedMs);

public sealed class TestRequestException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
