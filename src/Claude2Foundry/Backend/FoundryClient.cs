using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Claude2Foundry.Config;
using Claude2Foundry.Errors;
using Claude2Foundry.Protocol;
using Claude2Foundry.Protocol.OpenAI;
using Microsoft.Extensions.Logging;

namespace Claude2Foundry.Backend;

public sealed class FoundryClient(HttpClient http, ILogger<FoundryClient> logger)
{
    public async Task<ChatCompletionResponse> PostAsync(ChatCompletionRequest req, string correlationId, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(req, AppJsonSerializerContext.Default.ChatCompletionRequest);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("chat/completions", content, ct);

        if (!response.IsSuccessStatusCode)
            throw await BuildFoundryException(response, correlationId);

        var body = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize(body, AppJsonSerializerContext.Default.ChatCompletionResponse)
            ?? throw new AdapterException("Foundry returned empty body");
    }

    public async IAsyncEnumerable<ChatCompletionChunk> StreamAsync(
        ChatCompletionRequest req,
        string correlationId,
        TimeSpan idleTimeout,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(req, AppJsonSerializerContext.Default.ChatCompletionRequest);
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
            Version = HttpVersion.Version20,
        };

        using var response = await http.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
            throw await BuildFoundryException(response, correlationId);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idleCts.CancelAfter(idleTimeout);

            string? line;
            try
            {
                line = await reader.ReadLineAsync(idleCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Stream idle timeout after {idleTimeout.TotalSeconds}s");
            }

            if (line is null) yield break;
            if (string.IsNullOrEmpty(line)) continue;

            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line[5..].Trim();
            if (data == "[DONE]") yield break;

            ChatCompletionChunk? chunk = null;
            try
            {
                chunk = JsonSerializer.Deserialize(data, AppJsonSerializerContext.Default.ChatCompletionChunk);
            }
            catch (JsonException ex)
            {
                logger.LogWarning("req={CorrelationId} Failed to parse stream chunk: {Error}", correlationId, ex.Message);
                continue;
            }

            if (chunk is not null)
                yield return chunk;
        }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync("models", ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<FoundryHttpException> BuildFoundryException(HttpResponseMessage response, string correlationId)
    {
        var status = (int)response.StatusCode;
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        var body = await response.Content.ReadAsStringAsync();

        string message;
        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            message = $"{status} {response.ReasonPhrase}";
        }
        else
        {
            // Try to extract Foundry error message
            try
            {
                var err = JsonSerializer.Deserialize(body, AppJsonSerializerContext.Default.FoundryErrorResponse);
                message = err?.Error?.Message ?? err?.Error?.Code ?? body[..Math.Min(body.Length, 500)];
            }
            catch
            {
                message = body[..Math.Min(body.Length, 500)];
            }
        }

        logger.LogError("req={CorrelationId} Foundry error status={Status} body={Body}",
            correlationId, status, body[..Math.Min(body.Length, 2048)]);

        return new FoundryHttpException(status, message);
    }
}

public sealed class FoundryHttpException(int status, string message)
    : Exception($"[Foundry] {message}")
{
    public int Status { get; } = status;
    public string FoundryMessage { get; } = message;
}
