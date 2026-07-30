namespace SharpGameModes.Cosmetics;

public sealed class CosmeticsConfig
{
    public bool Enabled { get; init; } = true;
    public bool WeaponSkinsEnabled { get; init; } = true;
    public bool KnivesEnabled { get; init; } = true;
    public string DatabasePath { get; init; } = "data/sharp-gamemodes/cosmetics.db";
    public string WeaponSkinCatalogPath { get; init; } = "data/sharp-gamemodes/cosmetics/skins_en.json";
    public double SpawnApplyDelaySeconds { get; init; } = 0.1;
    public double DefaultWear { get; init; } = 0.01;
    public int DefaultSeed { get; init; }
    public string Prefix { get; init; } = "[Cosmetics]";
    public string[] WeaponSkinCommands { get; init; } = ["s", "skins", "paints"];
    public string[] KnifeCommands { get; init; } = ["k", "knife", "knives"];

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(WeaponSkinCatalogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(Prefix);
        if (SpawnApplyDelaySeconds is < 0 or > 5)
        {
            throw new InvalidDataException("spawn_apply_delay_seconds must be between 0 and 5.");
        }

        if (DefaultWear is < 0 or > 1)
        {
            throw new InvalidDataException("default_wear must be between 0 and 1.");
        }

        ValidateCommands(WeaponSkinCommands, nameof(WeaponSkinCommands));
        ValidateCommands(KnifeCommands, nameof(KnifeCommands));
    }

    private static void ValidateCommands(IEnumerable<string> commands, string name)
    {
        if (!commands.Any() || commands.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException($"{name} must contain at least one non-empty command.");
        }
    }
}
