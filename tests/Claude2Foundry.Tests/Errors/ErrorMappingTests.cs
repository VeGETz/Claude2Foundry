using Claude2Foundry.Errors;
using Xunit;

namespace Claude2Foundry.Tests.Errors;

public class ErrorMappingTests
{
    [Theory]
    [InlineData(400, "invalid_request_error", 400)]
    [InlineData(401, "authentication_error", 401)]
    [InlineData(403, "permission_error", 403)]
    [InlineData(404, "not_found_error", 404)]
    [InlineData(408, "api_error", 500)]
    [InlineData(413, "request_too_large", 413)]
    [InlineData(422, "invalid_request_error", 400)]
    [InlineData(429, "rate_limit_error", 429)]
    [InlineData(500, "api_error", 500)]
    [InlineData(502, "api_error", 500)]
    [InlineData(503, "api_error", 500)]
    [InlineData(504, "api_error", 500)]
    public void StatusCodeMapping(int foundryStatus, string expectedType, int expectedHttpStatus)
    {
        var (type, http) = ErrorMapping.MapFoundryStatus(foundryStatus);
        Assert.Equal(expectedType, type);
        Assert.Equal(expectedHttpStatus, http);
    }

    [Fact]
    public void FoundryError_HasFoundryPrefix()
    {
        var err = ErrorMapping.FoundryError(429, "Too many requests");
        Assert.StartsWith("[Foundry]", err.Error.Message);
        Assert.Contains("Too many requests", err.Error.Message);
        Assert.Equal("rate_limit_error", err.Error.Type);
    }

    [Fact]
    public void AdapterError_HasAdapterPrefix()
    {
        var err = ErrorMapping.AdapterError("invalid_request_error", "max_tokens is required");
        Assert.StartsWith("[Adapter]", err.Error.Message);
        Assert.Contains("max_tokens is required", err.Error.Message);
    }

    [Fact]
    public void SseErrorFrame_WellFormed()
    {
        var err = ErrorMapping.AdapterError("api_error", "test error");
        var frame = ErrorMapping.SseErrorFrame(err);
        Assert.Contains("event: error", frame);
        Assert.Contains("data:", frame);
        Assert.EndsWith("\n\n", frame);
    }
}
