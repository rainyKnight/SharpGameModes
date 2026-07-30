using System.Text.Json.Serialization;

namespace SharpGameModes.Contracts;

public sealed record AutoTeamRuleOverrides
{
    public bool? Enabled { get; init; }
    public bool? LockTeamSelect { get; init; }
    public bool? AutoAssignOnJoin { get; init; }
    public bool? BalanceOnRoundStart { get; init; }
    public bool? DisableNativeTeamBalance { get; init; }

    [JsonPropertyName("ct_ratio")]
    public int? CounterTerroristRatio { get; init; }

    [JsonPropertyName("t_ratio")]
    public int? TerroristRatio { get; init; }

    public int? AllowedCountDeviation { get; init; }
    public string? RoundRandomizeMode { get; init; }
    public double? RoundStartBalanceDelaySeconds { get; init; }
    public bool? RecordPlayerData { get; init; }
    public bool? UsePlayerDataForBalance { get; init; }
    public bool? BalanceHealthByRating { get; init; }
    public bool? PrintTopPlayersToChat { get; init; }
    public string? TopPlayersChatTitle { get; init; }
}
