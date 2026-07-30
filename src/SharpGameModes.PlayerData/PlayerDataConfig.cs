using System.Text.Json.Serialization;
using SharpGameModes.Domain;

namespace SharpGameModes.PlayerData;

public sealed class PlayerDataConfig
{
    public bool Enabled { get; set; } = true;
    public bool RecordPlayerData { get; set; } = true;
    public string DatabasePath { get; set; } = "data/sharp-gamemodes/autoteamlock_player_data.db";
    public int HistoryLimit { get; set; } = 100;
    public double TradeWindowSeconds { get; set; } = 5.0;
    public List<string> MapBlacklist { get; set; } = [];
    public List<string> DataWriteSkipWhitelist { get; set; } = [];
    public bool PrintTopPlayersToChat { get; set; } = true;
    public string TopPlayersChatTitle { get; set; }
        = "{red}<<<{gold}炸{lime}鱼{lightblue}狗{lightpurple}排行榜{red}>>>{default}";
    public PlayerDataRatingFormulaConfig RatingFormula { get; set; } = new();

    [JsonIgnore]
    public HashSet<ulong> DataWriteSkipWhitelistIds { get; private set; } = [];

    [JsonIgnore]
    public HashSet<string> NormalizedMapBlacklist { get; private set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(DatabasePath);
        if (HistoryLimit <= 0)
        {
            throw new InvalidDataException("history_limit must be greater than zero.");
        }

        if (!double.IsFinite(TradeWindowSeconds) || TradeWindowSeconds < 0)
        {
            throw new InvalidDataException("trade_window_seconds must be finite and non-negative.");
        }

        TopPlayersChatTitle ??= string.Empty;

        RatingFormula ??= new PlayerDataRatingFormulaConfig();
        _ = RatingFormula.ToDomain();
        MapBlacklist ??= [];
        NormalizedMapBlacklist = MapBlacklist
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeMapName)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        DataWriteSkipWhitelist ??= [];
        DataWriteSkipWhitelistIds = DataWriteSkipWhitelist
            .Where(value => ulong.TryParse(value?.Trim(), out _))
            .Select(value => ulong.Parse(value.Trim()))
            .ToHashSet();
    }

    public bool IsMapAllowed(string? mapName)
    {
        var normalized = NormalizeMapName(mapName);
        var shortName = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()
            ?? normalized;
        return !NormalizedMapBlacklist.Contains(normalized)
            && !NormalizedMapBlacklist.Contains(shortName);
    }

    private static string NormalizeMapName(string? mapName)
        => string.IsNullOrWhiteSpace(mapName)
            ? string.Empty
            : mapName.Trim().Replace('\\', '/').ToLowerInvariant();
}

public sealed class PlayerDataRatingFormulaConfig
{
    public double KastCoefficient { get; set; } = 0.0073;
    public double KillCoefficient { get; set; } = 0.3591;
    public double DeathCoefficient { get; set; } = -0.5329;
    public double ImpactCoefficient { get; set; } = 0.2372;
    public double DamageCoefficient { get; set; } = 0.0032;
    public double RatingIntercept { get; set; } = 0.1587;
    public double ImpactKillCoefficient { get; set; } = 2.13;
    public double ImpactAssistCoefficient { get; set; } = 0.42;
    public double ImpactIntercept { get; set; } = -0.41;
    public double MultiKillImpactBonus { get; set; } = 0.15;
    public double ClutchWinImpactBonus { get; set; } = 0.25;
    public double EntryKillImpactBonus { get; set; } = 0.15;
    public double EntryDeathImpactPenalty { get; set; } = 0.10;
    public double MinRoundRating { get; set; } = 0.0;
    public double MaxRoundRating { get; set; } = 3.0;

    public RatingFormula ToDomain()
    {
        var values = new[]
        {
            KastCoefficient,
            KillCoefficient,
            DeathCoefficient,
            ImpactCoefficient,
            DamageCoefficient,
            RatingIntercept,
            ImpactKillCoefficient,
            ImpactAssistCoefficient,
            ImpactIntercept,
            MultiKillImpactBonus,
            ClutchWinImpactBonus,
            EntryKillImpactBonus,
            EntryDeathImpactPenalty,
            MinRoundRating,
            MaxRoundRating,
        };
        if (values.Any(value => !double.IsFinite(value)) || MinRoundRating > MaxRoundRating)
        {
            throw new InvalidDataException("rating_formula contains invalid values.");
        }

        return new RatingFormula(
            KastCoefficient,
            KillCoefficient,
            DeathCoefficient,
            ImpactCoefficient,
            DamageCoefficient,
            RatingIntercept,
            ImpactKillCoefficient,
            ImpactAssistCoefficient,
            ImpactIntercept,
            MultiKillImpactBonus,
            ClutchWinImpactBonus,
            EntryKillImpactBonus,
            EntryDeathImpactPenalty,
            MinRoundRating,
            MaxRoundRating);
    }
}
