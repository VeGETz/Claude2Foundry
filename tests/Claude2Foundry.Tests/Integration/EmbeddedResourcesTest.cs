using Microsoft.Extensions.FileProviders;

namespace Claude2Foundry.Tests.Integration;

public sealed class EmbeddedResourcesTest
{
    [Fact]
    public void EmbeddedProvider_IndexHtml_Exists()
    {
        var provider = new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot/_ui");
        var file = provider.GetFileInfo("index.html");
        Assert.True(file.Exists, "wwwroot/_ui/index.html not found in assembly — run dotnet build first");
        Assert.True(file.Length > 0, "index.html is empty");
    }

    [Fact]
    public void EmbeddedProvider_AssetsDirectory_ContainsJsFile()
    {
        var provider = new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot/_ui");
        var assets = provider.GetDirectoryContents("assets");
        Assert.True(assets.Exists, "assets/ directory not found in embedded resources");
        var jsFiles = assets.Where(f => f.Name.EndsWith(".js", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(jsFiles);
    }
}
