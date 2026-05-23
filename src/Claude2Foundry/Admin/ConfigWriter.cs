using System.Text.Json;
using System.Text.Json.Nodes;
using Claude2Foundry.Config;

namespace Claude2Foundry.Admin;

public sealed class ConfigWriter(ILogger<ConfigWriter> logger)
{
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task WriteAsync(string dataDir, ProxyConfig config, CancellationToken ct = default)
    {
        var path = Path.Combine(dataDir, "appsettings.local.json");
        var tmp = path + ".tmp";

        var doc = new JsonObject { ["Proxy"] = JsonSerializer.SerializeToNode(config) };
        var json = doc.ToJsonString(IndentedOptions);

        await _lock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(dataDir);
            await File.WriteAllTextAsync(tmp, json, ct);

            if (File.Exists(path))
                File.Replace(tmp, path, null);
            else
                File.Move(tmp, path);

            logger.LogInformation("Config saved to {Path}", path);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write config to {Path}", path);
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }
}
