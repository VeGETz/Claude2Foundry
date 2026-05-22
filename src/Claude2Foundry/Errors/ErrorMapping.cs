using Claude2Foundry.Protocol.Anthropic;

namespace Claude2Foundry.Errors;

public static class ErrorMapping
{
    public static (string ErrorType, int HttpStatus) MapFoundryStatus(int foundryStatus) => foundryStatus switch
    {
        400 => ("invalid_request_error", 400),
        401 => ("authentication_error", 401),
        403 => ("permission_error", 403),
        404 => ("not_found_error", 404),
        408 => ("api_error", 500),
        413 => ("request_too_large", 413),
        422 => ("invalid_request_error", 400),
        429 => ("rate_limit_error", 429),
        500 => ("api_error", 500),
        502 => ("api_error", 500),
        503 => ("api_error", 500),
        504 => ("api_error", 500),
        _ => ("api_error", 500),
    };

    public static AnthropicErrorResponse FoundryError(int foundryStatus, string message)
    {
        var (type, _) = MapFoundryStatus(foundryStatus);
        return new AnthropicErrorResponse
        {
            Error = new AnthropicErrorDetail
            {
                Type = type,
                Message = $"[Foundry] {message}",
            }
        };
    }

    public static AnthropicErrorResponse AdapterError(string errorType, string message) =>
        new()
        {
            Error = new AnthropicErrorDetail
            {
                Type = errorType,
                Message = $"[Adapter] {message}",
            }
        };

    public static string SseErrorFrame(AnthropicErrorResponse err)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(err,
            Protocol.AppJsonSerializerContext.Default.AnthropicErrorResponse);
        return $"event: error\ndata: {json}\n\n";
    }
}

public sealed class AdapterException(string message) : Exception(message);
