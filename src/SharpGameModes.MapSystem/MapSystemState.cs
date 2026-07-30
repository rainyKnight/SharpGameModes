namespace SharpGameModes.MapSystem;

internal sealed class MapSystemState
{
    public int SchemaVersion { get; set; } = 1;
    public string? CurrentEntryId { get; set; }
    public string? NextEntryId { get; set; }
    public string? PendingActivationEntryId { get; set; }
    public DateTimeOffset? CurrentMapStartedAtUtc { get; set; }
    public List<string> RecentEntryIds { get; set; } = [];
}
