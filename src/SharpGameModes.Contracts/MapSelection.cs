namespace SharpGameModes.Contracts;

public sealed record MapSelection(
    string EntryId,
    ModeId Mode,
    string MapName,
    string DisplayName,
    bool Workshop,
    ulong? WorkshopId,
    AutoTeamRuleOverrides? AutoTeam = null);

public sealed record ModeContextSnapshot(
    MapSelection Selection,
    long Generation,
    DateTimeOffset ActivatedAt,
    string Source);
