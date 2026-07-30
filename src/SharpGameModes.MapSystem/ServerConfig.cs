using SharpGameModes.Contracts;

namespace SharpGameModes.MapSystem;

public sealed class ServerConfig
{
    public int SchemaVersion { get; init; } = 1;
    public string DefaultMode { get; init; } = "classic";
    public string[] EnabledModes { get; init; } = ["classic"];

    public ModeId DefaultModeId => ModeId.Parse(DefaultMode);

    public IReadOnlyList<ModeId> GetEnabledModeIds()
        => EnabledModes.Select(ModeId.Parse).Distinct().ToArray();

    public void Validate()
    {
        if (SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported server config schema_version {SchemaVersion}.");
        }

        var defaultMode = ModeId.Parse(DefaultMode);
        var parsed = EnabledModes.Select(ModeId.Parse).ToArray();
        var enabled = parsed.ToHashSet();
        if (enabled.Count == 0 || !enabled.Contains(defaultMode))
        {
            throw new InvalidDataException("default_mode must also be present in enabled_modes.");
        }

        if (enabled.Count != parsed.Length)
        {
            throw new InvalidDataException("enabled_modes must not contain duplicate modes or aliases.");
        }
    }
}
