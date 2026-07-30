using SharpGameModes.Contracts;

namespace SharpGameModes.Domain;

public enum RoundRandomizeMode
{
    BalanceOnly,
    FirstRoundThenBalance,
    EveryRound,
}

public static class RoundRandomizeModeNames
{
    public const string BalanceOnly = "balance_only";
    public const string FirstRoundThenBalance = "first_round_then_balance";
    public const string EveryRound = "every_round";

    public static RoundRandomizeMode Parse(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            EveryRound => RoundRandomizeMode.EveryRound,
            FirstRoundThenBalance => RoundRandomizeMode.FirstRoundThenBalance,
            BalanceOnly or null or "" => RoundRandomizeMode.BalanceOnly,
            _ => throw new InvalidDataException($"Unsupported round_randomize_mode '{value}'."),
        };
}

public sealed record AutoTeamRuleDefaults(
    bool Enabled,
    bool LockTeamSelect,
    bool AutoAssignOnJoin,
    bool BalanceOnRoundStart,
    bool DisableNativeTeamBalance,
    int CounterTerroristRatio,
    int TerroristRatio,
    int AllowedCountDeviation,
    RoundRandomizeMode RoundRandomizeMode,
    double RoundStartBalanceDelaySeconds,
    bool UsePlayerDataForBalance,
    bool BalanceHealthByRating);

public sealed record EffectiveAutoTeamRule(
    string RuleName,
    bool Enabled,
    bool LockTeamSelect,
    bool AutoAssignOnJoin,
    bool BalanceOnRoundStart,
    bool DisableNativeTeamBalance,
    int CounterTerroristRatio,
    int TerroristRatio,
    int AllowedCountDeviation,
    RoundRandomizeMode RoundRandomizeMode,
    double RoundStartBalanceDelaySeconds,
    bool UsePlayerDataForBalance,
    bool BalanceHealthByRating);

public static class AutoTeamRuleResolver
{
    public static EffectiveAutoTeamRule Resolve(
        AutoTeamRuleDefaults defaults,
        AutoTeamRuleOverrides? overrides,
        bool playerDataAllowed,
        string ruleName)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleName);
        Validate(overrides, ruleName);

        return new EffectiveAutoTeamRule(
            ruleName.Trim(),
            overrides?.Enabled ?? defaults.Enabled,
            overrides?.LockTeamSelect ?? defaults.LockTeamSelect,
            overrides?.AutoAssignOnJoin ?? defaults.AutoAssignOnJoin,
            overrides?.BalanceOnRoundStart ?? defaults.BalanceOnRoundStart,
            overrides?.DisableNativeTeamBalance ?? defaults.DisableNativeTeamBalance,
            overrides?.CounterTerroristRatio ?? defaults.CounterTerroristRatio,
            overrides?.TerroristRatio ?? defaults.TerroristRatio,
            overrides?.AllowedCountDeviation ?? defaults.AllowedCountDeviation,
            overrides?.RoundRandomizeMode is null
                ? defaults.RoundRandomizeMode
                : RoundRandomizeModeNames.Parse(overrides.RoundRandomizeMode),
            overrides?.RoundStartBalanceDelaySeconds ?? defaults.RoundStartBalanceDelaySeconds,
            playerDataAllowed
                && (overrides?.UsePlayerDataForBalance ?? defaults.UsePlayerDataForBalance),
            playerDataAllowed
                && (overrides?.BalanceHealthByRating ?? defaults.BalanceHealthByRating));
    }

    public static AutoTeamRuleOverrides? Merge(
        AutoTeamRuleOverrides? modeRule,
        AutoTeamRuleOverrides? mapRule)
    {
        if (modeRule is null && mapRule is null)
        {
            return null;
        }

        Validate(modeRule, "mode");
        Validate(mapRule, "map");
        return new AutoTeamRuleOverrides
        {
            Enabled = mapRule?.Enabled ?? modeRule?.Enabled,
            LockTeamSelect = mapRule?.LockTeamSelect ?? modeRule?.LockTeamSelect,
            AutoAssignOnJoin = mapRule?.AutoAssignOnJoin ?? modeRule?.AutoAssignOnJoin,
            BalanceOnRoundStart = mapRule?.BalanceOnRoundStart ?? modeRule?.BalanceOnRoundStart,
            DisableNativeTeamBalance = mapRule?.DisableNativeTeamBalance ?? modeRule?.DisableNativeTeamBalance,
            CounterTerroristRatio = mapRule?.CounterTerroristRatio ?? modeRule?.CounterTerroristRatio,
            TerroristRatio = mapRule?.TerroristRatio ?? modeRule?.TerroristRatio,
            AllowedCountDeviation = mapRule?.AllowedCountDeviation ?? modeRule?.AllowedCountDeviation,
            RoundRandomizeMode = mapRule?.RoundRandomizeMode ?? modeRule?.RoundRandomizeMode,
            RoundStartBalanceDelaySeconds = mapRule?.RoundStartBalanceDelaySeconds
                ?? modeRule?.RoundStartBalanceDelaySeconds,
            RecordPlayerData = mapRule?.RecordPlayerData ?? modeRule?.RecordPlayerData,
            UsePlayerDataForBalance = mapRule?.UsePlayerDataForBalance ?? modeRule?.UsePlayerDataForBalance,
            BalanceHealthByRating = mapRule?.BalanceHealthByRating ?? modeRule?.BalanceHealthByRating,
            PrintTopPlayersToChat = mapRule?.PrintTopPlayersToChat ?? modeRule?.PrintTopPlayersToChat,
            TopPlayersChatTitle = mapRule?.TopPlayersChatTitle ?? modeRule?.TopPlayersChatTitle,
        };
    }

    public static void Validate(AutoTeamRuleOverrides? rule, string source)
    {
        if (rule is null)
        {
            return;
        }

        if (rule.CounterTerroristRatio is <= 0 || rule.TerroristRatio is <= 0)
        {
            throw new InvalidDataException($"{source} auto_team ratios must be positive.");
        }

        if (rule.AllowedCountDeviation is < 0 or > 64)
        {
            throw new InvalidDataException(
                $"{source} auto_team allowed_count_deviation must be between 0 and 64.");
        }

        if (rule.RoundStartBalanceDelaySeconds is { } delay
            && (!double.IsFinite(delay) || delay is < 0 or > 10))
        {
            throw new InvalidDataException(
                $"{source} auto_team round_start_balance_delay_seconds must be between 0 and 10.");
        }

        if (rule.RoundRandomizeMode is not null)
        {
            _ = RoundRandomizeModeNames.Parse(rule.RoundRandomizeMode);
        }
    }
}
