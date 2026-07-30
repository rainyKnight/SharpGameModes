namespace SharpGameModes.WorkshopMount;

internal readonly record struct WorkshopVpkPath(
    string SearchPath,
    string IndexPath,
    bool IsChunked);

internal static class WorkshopVpkResolver
{
    public static WorkshopVpkPath? Resolve(string gameRoot, ulong addonId)
    {
        if (string.IsNullOrWhiteSpace(gameRoot) || addonId == 0)
        {
            return null;
        }

        var addonName = addonId.ToString();
        foreach (var runtimeDirectory in RuntimeDirectories())
        {
            var addonDirectory = Path.Combine(
                gameRoot,
                "bin",
                runtimeDirectory,
                "steamapps",
                "workshop",
                "content",
                "730",
                addonName);
            var basePath = Path.Combine(addonDirectory, $"{addonName}.vpk");
            var indexPath = Path.Combine(addonDirectory, $"{addonName}_dir.vpk");

            if (File.Exists(indexPath))
            {
                // Source 2 expects the unsuffixed base name and resolves _dir/_### itself.
                return new WorkshopVpkPath(basePath, indexPath, IsChunked: true);
            }

            if (File.Exists(basePath))
            {
                return new WorkshopVpkPath(basePath, basePath, IsChunked: false);
            }
        }

        return null;
    }

    private static IEnumerable<string> RuntimeDirectories()
    {
        if (OperatingSystem.IsLinux())
        {
            yield return "linuxsteamrt64";
            yield break;
        }

        if (OperatingSystem.IsWindows())
        {
            yield return "win64";
            yield break;
        }

        yield return "linuxsteamrt64";
        yield return "win64";
    }
}
