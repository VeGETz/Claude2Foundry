using System.Text.Json;
using System.Text.Json.Serialization;
using Claude2Foundry.Config;
using Claude2Foundry.Monitor;

namespace Claude2Foundry.Tests.Monitor;

public class SerializationCasingTests
{
    private static readonly JsonSerializerOptions ApiOptions = BuildApiOptions();

    private static JsonSerializerOptions BuildApiOptions()
    {
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        opts.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return opts;
    }

    [Fact]
    public void ProxyConfig_SerializesWithPascalCaseKeys()
    {
        var cfg = new ProxyConfig
        {
            BackendUrl = "https://test.openai.azure.com/openai/v1/",
            ApiKeyEnv = "TEST_KEY",
            DefaultModel = "test-model",
        };

        var json = JsonSerializer.Serialize(cfg, ApiOptions);

        Assert.Contains("\"BackendUrl\"", json);
        Assert.Contains("\"ApiKeyEnv\"", json);
        Assert.Contains("\"DefaultModel\"", json);
        Assert.DoesNotContain("\"backendUrl\"", json);
        Assert.DoesNotContain("\"apiKeyEnv\"", json);
    }

    [Fact]
    public void ResponseDto_SerializesWithCamelCaseKeys()
    {
        var dto = new TestHealthStub { UptimeSec = 42, WrapperPresent = true };

        var json = JsonSerializer.Serialize(dto, ApiOptions);

        Assert.Contains("\"uptimeSec\"", json);
        Assert.Contains("\"wrapperPresent\"", json);
        Assert.DoesNotContain("\"UptimeSec\"", json);
        Assert.DoesNotContain("\"WrapperPresent\"", json);
    }

    [Fact]
    public void CaptureMode_SerializesAsLowercase()
    {
        var json = JsonSerializer.Serialize(CaptureMode.Hybrid, ApiOptions);
        Assert.Equal("\"hybrid\"", json);

        json = JsonSerializer.Serialize(CaptureMode.Full, ApiOptions);
        Assert.Equal("\"full\"", json);
    }

    [Fact]
    public void CaptureScope_SerializesAsLowercase()
    {
        var json = JsonSerializer.Serialize(CaptureScope.Session, ApiOptions);
        Assert.Equal("\"session\"", json);

        json = JsonSerializer.Serialize(CaptureScope.Persistent, ApiOptions);
        Assert.Equal("\"persistent\"", json);
    }

    private sealed record TestHealthStub
    {
        public int UptimeSec { get; init; }
        public bool WrapperPresent { get; init; }
    }
}
