using Claude2Foundry.Config;

namespace Claude2Foundry.Tests.Config;

public class DataDirResolverTests : IDisposable
{
    private readonly string? _savedC2fDataDir = Environment.GetEnvironmentVariable("C2F_DATA_DIR");
    private readonly string? _savedXdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

    public void Dispose()
    {
        SetEnv("C2F_DATA_DIR", _savedC2fDataDir);
        SetEnv("XDG_DATA_HOME", _savedXdgDataHome);
    }

    [Fact]
    public void EnvOverride_UsesC2fDataDir_AndCreatesIt()
    {
        var target = Path.Combine(Path.GetTempPath(), $"c2f-test-{Guid.NewGuid():N}");
        try
        {
            SetEnv("C2F_DATA_DIR", target);
            var result = DataDirResolver.ResolveDataDir(null);
            Assert.Equal(target, result);
            Assert.True(Directory.Exists(target));
        }
        finally
        {
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void Linux_Unset_ReturnsXdgDefault()
    {
        if (!OperatingSystem.IsLinux()) return;

        SetEnv("C2F_DATA_DIR", null);
        SetEnv("XDG_DATA_HOME", null);

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "claude2foundry");

        var result = DataDirResolver.ResolveDataDir(null);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Linux_XdgSet_UsesXdgHome()
    {
        if (!OperatingSystem.IsLinux()) return;

        var xdg = Path.Combine(Path.GetTempPath(), $"xdg-test-{Guid.NewGuid():N}");
        try
        {
            SetEnv("C2F_DATA_DIR", null);
            SetEnv("XDG_DATA_HOME", xdg);

            var expected = Path.Combine(xdg, "claude2foundry");
            var result = DataDirResolver.ResolveDataDir(null);
            Assert.Equal(expected, result);
        }
        finally
        {
            var created = Path.Combine(xdg, "claude2foundry");
            if (Directory.Exists(created)) Directory.Delete(created, recursive: true);
            if (Directory.Exists(xdg)) Directory.Delete(xdg, recursive: true);
        }
    }

    [Fact]
    public void Windows_Unset_ReturnsLocalAppData()
    {
        if (!OperatingSystem.IsWindows()) return;

        SetEnv("C2F_DATA_DIR", null);

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Claude2Foundry");

        var result = DataDirResolver.ResolveDataDir(null);
        Assert.Equal(expected, result);
    }

    private static void SetEnv(string key, string? value)
    {
        if (value is null)
            Environment.SetEnvironmentVariable(key, null);
        else
            Environment.SetEnvironmentVariable(key, value);
    }
}
