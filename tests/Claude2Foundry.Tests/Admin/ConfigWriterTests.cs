using Claude2Foundry.Admin;
using Claude2Foundry.Config;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace Claude2Foundry.Tests.Admin;

public class ConfigWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private ConfigWriter Build() => new(NullLogger<ConfigWriter>.Instance);

    private static ProxyConfig SampleConfig() => new()
    {
        BackendUrl = "https://test.openai.azure.com/openai/v1/",
        ApiKeyEnv = "TEST_KEY",
        DefaultModel = "test-model"
    };

    [Fact]
    public async Task Write_CreatesFile_WithProxyKey()
    {
        var writer = Build();
        await writer.WriteAsync(_dir, SampleConfig());

        var path = Path.Combine(_dir, "appsettings.local.json");
        Assert.True(File.Exists(path));

        var json = await File.ReadAllTextAsync(path);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("Proxy", out _));
    }

    [Fact]
    public async Task Write_PascalCaseKeys_InOutput()
    {
        var writer = Build();
        await writer.WriteAsync(_dir, SampleConfig());

        var json = await File.ReadAllTextAsync(Path.Combine(_dir, "appsettings.local.json"));
        Assert.Contains("\"BackendUrl\"", json);
        Assert.Contains("\"ApiKeyEnv\"", json);
        Assert.DoesNotContain("\"backendUrl\"", json);
    }

    [Fact]
    public async Task Write_Overwrites_ExistingFile()
    {
        var writer = Build();
        await writer.WriteAsync(_dir, SampleConfig());
        var updated = SampleConfig() with { DefaultModel = "updated-model" };
        await writer.WriteAsync(_dir, updated);

        var json = await File.ReadAllTextAsync(Path.Combine(_dir, "appsettings.local.json"));
        Assert.Contains("updated-model", json);
    }

    [Fact]
    public async Task Write_ConcurrentSaves_BothSucceed()
    {
        var writer = Build();
        var t1 = writer.WriteAsync(_dir, SampleConfig() with { DefaultModel = "model-1" });
        var t2 = writer.WriteAsync(_dir, SampleConfig() with { DefaultModel = "model-2" });
        await Task.WhenAll(t1, t2);

        var json = await File.ReadAllTextAsync(Path.Combine(_dir, "appsettings.local.json"));
        // One of the two should win; file must be valid JSON
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("Proxy", out _));
    }

    [Fact]
    public async Task Write_NoTmpFileLeft_OnSuccess()
    {
        var writer = Build();
        await writer.WriteAsync(_dir, SampleConfig());

        var tmpFiles = Directory.GetFiles(_dir, "*.tmp");
        Assert.Empty(tmpFiles);
    }
}
