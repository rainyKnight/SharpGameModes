namespace SharpGameModes.Contracts;

public sealed record PlayerRatingSnapshot(
    ulong SteamId,
    string LastKnownName,
    int RoundsRecorded,
    int MatchesRecorded,
    int HistoryCount,
    double Rating,
    double Impact,
    double Kast,
    double Adr,
    double KillsPerRound,
    double DeathsPerRound,
    double AssistsPerRound,
    int TotalKills,
    int TotalDeaths,
    int TotalAssists,
    int TotalDamage,
    int TotalEntryKills,
    int TotalEntryDeaths,
    int TotalHeadshots,
    int MultiKillRounds,
    int ClutchesWon,
    string LastMap,
    DateTimeOffset LastUpdatedAt);

public interface IPlayerRatingProvider
{
    public const string Identity = "SharpGameModes.Contracts.IPlayerRatingProvider";

    int Count { get; }

    DateTimeOffset LoadedAt { get; }

    bool TryGetRating(ulong steamId, out PlayerRatingSnapshot? rating);

    bool IsMapAllowed(string mapName);

    int Reload();
}
