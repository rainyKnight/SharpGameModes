namespace SharpGameModes.BotMatch;

internal readonly record struct BotDifficultyTier(
    string Key,
    string DirectoryName,
    string DisplayName,
    int Level,
    int LevelCount);

internal static class BotDifficultyTierPolicy
{
    public static bool TryResolve(
        string? value,
        out BotDifficultyTier tier)
    {
        tier = value?.Trim().ToLowerInvariant() switch
        {
            "low" => new("low", "Low", "简单", 1, 4),
            "medium" => new("medium", "Medium", "普通", 2, 4),
            "hltvtop10" => new("hltvtop10", "HLTVTop10", "困难", 3, 4),
            // Keep existing installations working after the curated tier rename.
            "hltvtop37" => new("hltvtop10", "HLTVTop10", "困难", 3, 4),
            "high" => new("high", "High", "噩梦", 4, 4),
            _ => default,
        };
        return tier.DirectoryName is not null;
    }

    public static string FormatAnnouncement(string? value)
        => TryResolve(value, out var tier)
            ? $"BOT 难度：{tier.DisplayName} [{tier.Level}/{tier.LevelCount}]"
            : "BOT 难度：未知";
}
