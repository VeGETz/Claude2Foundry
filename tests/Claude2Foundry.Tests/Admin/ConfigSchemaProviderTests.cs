using Claude2Foundry.Admin;
using System.Text.Json;

namespace Claude2Foundry.Tests.Admin;

public class ConfigSchemaProviderTests
{
    [Fact]
    public void Schema_IsValidJson()
    {
        var schema = ConfigSchemaProvider.Schema;
        Assert.NotNull(schema);
        var json = schema.ToJsonString();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void Schema_HasRequiredTopLevelFields()
    {
        var json = ConfigSchemaProvider.Schema.ToJsonString();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("$schema", out _));
        Assert.True(root.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("BackendUrl", out _));
        Assert.True(props.TryGetProperty("ApiKeyEnv", out _));
        Assert.True(props.TryGetProperty("DefaultModel", out _));
        Assert.True(props.TryGetProperty("Monitor", out _));
    }

    [Fact]
    public void Schema_MonitorSection_HasEnabledAndMaxBodyBytes()
    {
        var json = ConfigSchemaProvider.Schema.ToJsonString();
        using var doc = JsonDocument.Parse(json);
        var monitorProps = doc.RootElement
            .GetProperty("properties")
            .GetProperty("Monitor")
            .GetProperty("properties");

        Assert.True(monitorProps.TryGetProperty("Enabled", out _));
        Assert.True(monitorProps.TryGetProperty("MaxBodyBytes", out _));
    }

    [Fact]
    public void Schema_HasTokenizerOneOf()
    {
        var json = ConfigSchemaProvider.Schema.ToJsonString();
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("$defs", out var defs));
        Assert.True(defs.TryGetProperty("Tokenizer", out var tokenizer));
        Assert.True(tokenizer.TryGetProperty("oneOf", out _));
    }
}
