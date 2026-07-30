namespace SharpGameModes.BotMatch;

internal enum BotProfileSourceFormat
{
    Vpk,
    Directory,
}

internal readonly record struct BotProfileSource(
    string DifficultyTier,
    string SearchPath,
    string DatabasePath,
    BotProfileSourceFormat Format,
    long ExpectedDatabaseBytes);

internal static class BotProfileSourceResolver
{
    public static BotProfileSource? Resolve(
        string overridesRoot,
        string difficultyTier)
    {
        if (string.IsNullOrWhiteSpace(overridesRoot)
            || !TryNormalizeTier(difficultyTier, out var normalizedTier))
        {
            return null;
        }

        var tierDirectory = Path.Combine(overridesRoot, normalizedTier);
        var databasePath = Path.Combine(tierDirectory, "botprofile.db");
        var vpkPath = Path.Combine(tierDirectory, "botprofile.vpk");
        if (File.Exists(vpkPath))
        {
            return new BotProfileSource(
                normalizedTier,
                vpkPath,
                File.Exists(databasePath) ? databasePath : vpkPath,
                BotProfileSourceFormat.Vpk,
                File.Exists(databasePath)
                    ? new FileInfo(databasePath).Length
                    : 0);
        }

        if (File.Exists(databasePath))
        {
            var searchPath = Path.TrimEndingDirectorySeparator(tierDirectory)
                + Path.DirectorySeparatorChar;
            return new BotProfileSource(
                normalizedTier,
                searchPath,
                databasePath,
                BotProfileSourceFormat.Directory,
                new FileInfo(databasePath).Length);
        }

        return null;
    }

    public static bool TryNormalizeTier(
        string? difficultyTier,
        out string normalizedTier)
    {
        if (BotDifficultyTierPolicy.TryResolve(difficultyTier, out var tier))
        {
            normalizedTier = tier.DirectoryName;
            return true;
        }

        normalizedTier = string.Empty;
        return false;
    }
}
