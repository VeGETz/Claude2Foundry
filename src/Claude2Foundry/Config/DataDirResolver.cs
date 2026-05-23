namespace Claude2Foundry.Config;

public static class DataDirResolver
{
    public static string ResolveDataDir(IHostEnvironment? env, ILogger? logger = null)
    {
        var envOverride = Environment.GetEnvironmentVariable("C2F_DATA_DIR");
        if (!string.IsNullOrEmpty(envOverride))
        {
            Directory.CreateDirectory(envOverride);
            return envOverride;
        }

        try
        {
            var dir = GetOsDefaultDir();
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch (Exception ex)
        {
            var fallback = Environment.CurrentDirectory;
            logger?.LogWarning(
                "Failed to create data dir ({Reason}); falling back to {Fallback}",
                ex.Message, fallback);
            return fallback;
        }
    }

    private static string GetOsDefaultDir()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Claude2Foundry");

        if (OperatingSystem.IsMacOS())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "Claude2Foundry");

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdgDataHome))
            return Path.Combine(xdgDataHome, "claude2foundry");

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "claude2foundry");
    }
}
