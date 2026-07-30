namespace SharpGameModes.Contracts;

public sealed record PlayerMatchResultSnapshot(
    ulong SteamId,
    string PlayerName,
    string MapName,
    DateTimeOffset RecordedAt,
    int RoundsPlayed,
    double Rating,
    double Impact,
    double Adr);

public interface IPlayerMatchResultSource
{
    public const string Identity = "SharpGameModes.Contracts.IPlayerMatchResultSource";

    IDisposable Subscribe(Action<IReadOnlyList<PlayerMatchResultSnapshot>> listener);
}
