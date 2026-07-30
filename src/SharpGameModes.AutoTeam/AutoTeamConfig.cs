using System.Text.Json.Serialization;
using SharpGameModes.Domain;

namespace SharpGameModes.AutoTeam;

public sealed class AutoTeamConfig
{
    public bool Enabled { get; set; } = true;
    public string Prefix { get; set; } = "[AutoTeam]";
    public bool LockTeamSelect { get; set; } = true;
    public bool AutoAssignOnJoin { get; set; } = true;
    public bool BalanceOnRoundStart { get; set; } = true;
    public bool DisableNativeTeamBalance { get; set; } = true;
    public int AllowedCountDeviation { get; set; }
    public string RoundRandomizeMode { get; set; } = RoundRandomizeModeNames.BalanceOnly;
    public double RoundStartBalanceDelaySeconds { get; set; } = 0.2;
    public bool UsePlayerDataForBalance { get; set; } = true;
    public bool BalanceHealthByRating { get; set; } = true;
    public int CounterTerroristRatio { get; set; } = 1;
    public int TerroristRatio { get; set; } = 1;
    public double DefaultRating { get; set; } = 1.0;
    public string HealthCompensationStatePath { get; set; }
        = "data/sharp-gamemodes/autoteamlock_health_compensation.json";
    public List<string> HealthCompensationBlacklist { get; set; } = [];
    public List<string> ObserverWhitelist { get; set; } = [];
    public Dictionary<string, double> RatingOverrides { get; set; } = new(StringComparer.Ordinal);
    public LowRatingHealthPolicyConfig LowRatingHealthCompensation { get; set; } = new();

    [JsonIgnore]
    public HashSet<ulong> HealthCompensationBlacklistIds { get; private set; } = [];

    [JsonIgnore]
    public HashSet<ulong> ObserverWhitelistIds { get; private set; } = [];

    public void Validate()
    {
        if (CounterTerroristRatio <= 0 || TerroristRatio <= 0)
        {
            throw new InvalidDataException("Team ratios must be positive.");
        }

        if (!double.IsFinite(RoundStartBalanceDelaySeconds)
            || RoundStartBalanceDelaySeconds is < 0 or > 10)
        {
            throw new InvalidDataException("round_start_balance_delay_seconds must be between 0 and 10.");
        }

        if (AllowedCountDeviation is < 0 or > 64)
        {
            throw new InvalidDataException("allowed_count_deviation must be between 0 and 64.");
        }

        _ = RoundRandomizeModeNames.Parse(RoundRandomizeMode);

        if (!double.IsFinite(DefaultRating) || DefaultRating <= 0)
        {
            throw new InvalidDataException("default_rating must be a positive finite number.");
        }

        RatingOverrides ??= new Dictionary<string, double>(StringComparer.Ordinal);
        if (RatingOverrides.Any(pair => !ulong.TryParse(pair.Key, out _)
            || !double.IsFinite(pair.Value)
            || pair.Value <= 0))
        {
            throw new InvalidDataException(
                "rating_overrides keys must be SteamID64 values and ratings must be positive finite numbers.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(HealthCompensationStatePath);
        HealthCompensationBlacklist ??= [];
        HealthCompensationBlacklistIds = HealthCompensationBlacklist
            .Where(value => ulong.TryParse(value?.Trim(), out _))
            .Select(value => ulong.Parse(value.Trim()))
            .ToHashSet();
        ObserverWhitelist ??= [];
        ObserverWhitelistIds = ObserverWhitelist
            .Where(value => ulong.TryParse(value?.Trim(), out _))
            .Select(value => ulong.Parse(value.Trim()))
            .ToHashSet();
        LowRatingHealthCompensation ??= new LowRatingHealthPolicyConfig();
        _ = LowRatingHealthCompensation.ToDomain();
    }

    public AutoTeamRuleDefaults ToRuleDefaults()
        => new(
            Enabled,
            LockTeamSelect,
            AutoAssignOnJoin,
            BalanceOnRoundStart,
            DisableNativeTeamBalance,
            CounterTerroristRatio,
            TerroristRatio,
            AllowedCountDeviation,
            RoundRandomizeModeNames.Parse(RoundRandomizeMode),
            RoundStartBalanceDelaySeconds,
            UsePlayerDataForBalance,
            BalanceHealthByRating);
}

public sealed class LowRatingHealthPolicyConfig
{
    public bool Enabled { get; set; } = true;
    public double TargetRating { get; set; } = 1.0;
    public int MaxHealth { get; set; } = 1000;
    public double LearningRate { get; set; } = 0.35;
    public double RatingEmaAlpha { get; set; } = 0.3;
    public int MinimumRounds { get; set; } = 8;
    public double RatingErrorDeadband { get; set; } = 0.1;
    public double MaxHealthAdjustmentRatio { get; set; } = 0.1;

    public LowRatingHealthPolicy ToDomain()
    {
        var policy = new LowRatingHealthPolicy(
            Enabled,
            TargetRating,
            MaxHealth,
            LearningRate,
            RatingEmaAlpha,
            MinimumRounds,
            RatingErrorDeadband,
            MaxHealthAdjustmentRatio);
        policy.Validate();
        return policy;
    }
}

public sealed class HealthCompensationStateStore
{
    public int Version { get; set; } = 1;
    public DateTimeOffset LastUpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, HealthCompensationState> Players { get; set; }
        = new(StringComparer.Ordinal);
}
