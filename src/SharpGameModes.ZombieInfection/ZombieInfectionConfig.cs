using System.Numerics;

namespace SharpGameModes.ZombieInfection;

public sealed class ZombieInfectionConfig
{
    public bool Enabled { get; init; } = true;
    public string Prefix { get; init; } = "[Zombie]";
    public int MinimumPlayers { get; init; } = 2;
    public bool IncludeBotsInRound { get; init; }
    public int FirstInfectionDelaySeconds { get; init; } = 15;
    public int RoundDurationSeconds { get; init; } = 180;
    public double PostRoundDelaySeconds { get; init; } = 5;
    public int MinimumInitialZombies { get; init; } = 1;
    public double InitialZombieRatio { get; init; } = 0.25;
    public int MaximumInitialZombies { get; init; }
    public int ZombieHealth { get; init; } = 18000;
    public int MotherZombieHealth { get; init; } = 30000;
    public float ZombieSpeed { get; init; } = 1.25f;
    public int ZombieLives { get; init; }
    public double ZombieRespawnDelaySeconds { get; init; } = 1;
    public int ZombieHealOnInfect { get; init; } = 300;
    public double CorpseInfectionDelaySeconds { get; init; } = 5;
    public float CorpseRespawnZOffset { get; init; } = 16;
    public bool CorpseMarkerEnabled { get; init; } = true;
    public string CorpseMarkerModel { get; init; } = string.Empty;
    public string CorpseMarkerAnimation { get; init; } = "idle";
    public float CorpseMarkerZOffset { get; init; } = 16;
    public float CorpseMarkerScale { get; init; } = 1;
    public byte CorpseMarkerAlpha { get; init; } = 120;
    public byte CorpseMarkerRed { get; init; } = 255;
    public byte CorpseMarkerGreen { get; init; } = 64;
    public byte CorpseMarkerBlue { get; init; } = 64;
    public int CorpseMarkerGlowRange { get; init; } = 1800;
    public int CorpseMarkerGlowType { get; init; } = 3;
    public bool KnockbackEnabled { get; init; } = true;
    public bool DisableHitSlowdown { get; init; } = true;
    public float KnockbackBaseForce { get; init; }
    public float KnockbackDamageScale { get; init; } = 10;
    public float KnockbackVerticalBoost { get; init; }
    public float KnockbackMaxHorizontalSpeed { get; init; } = 1200;
    public bool ManualRoundAccounting { get; init; } = true;
    public float ZombieKnifeLightDamage { get; init; } = 60;
    public float ZombieKnifeHeavyDamage { get; init; } = 120;
    public float ZombieKnifeHeavyDamageThreshold { get; init; } = 60;
    public int HumanHealth { get; init; } = 100;
    public float HumanSpeed { get; init; } = 1;
    public int HumanArmor { get; init; } = 100;
    public int ZombieArmor { get; init; } = 3000;
    public bool SpawnFullArmor { get; init; } = true;
    public bool SpawnHelmet { get; init; } = true;
    public bool InfiniteHumanAmmo { get; init; } = true;
    public bool DisableFallDamage { get; init; } = true;
    public double FallSoundSuppressSeconds { get; init; } = 1;
    public float FallSoundVelocityThreshold { get; init; } = 500;
    public bool DebugFallSoundMessages { get; init; }
    public bool DisableDamageShake { get; init; } = true;
    public bool BlockStandardBuyCommands { get; init; } = true;
    public bool BlockZombieBuy { get; init; } = true;
    public string[] BlockedBuyItems { get; init; } = ["flashbang", "smokegrenade"];
    public bool BlockHumanWeaponDrop { get; init; } = true;
    public bool ShowDropBlockedMessage { get; init; } = true;
    public bool BlockPlayerModelCommandsForZombies { get; init; } = true;
    public bool ShowWeaponHelpOnHumanSpawn { get; init; } = true;
    public string WeaponHelpMessage { get; init; }
        = "{prefix} 人类用 !guns 看枪械指令，例：!ak、!m4、!awp、!de；所有人可用 !fdy 买满甲。";
    public double ApplyZombieModelDelaySeconds { get; init; } = 0.25;
    public string[] ZombieModels { get; init; } = ["characters/models/senae/bikini.vmdl"];
    public string[] MotherZombieModels { get; init; } = [];

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(WeaponHelpMessage);
        ArgumentNullException.ThrowIfNull(BlockedBuyItems);
        ArgumentNullException.ThrowIfNull(ZombieModels);
        ArgumentNullException.ThrowIfNull(MotherZombieModels);
        ArgumentException.ThrowIfNullOrWhiteSpace(CorpseMarkerAnimation);

        RequireRange(MinimumPlayers, 2, 64, nameof(MinimumPlayers));
        RequireRange(FirstInfectionDelaySeconds, 1, 120, nameof(FirstInfectionDelaySeconds));
        RequireRange(RoundDurationSeconds, 30, 3600, nameof(RoundDurationSeconds));
        RequireRange(PostRoundDelaySeconds, 0.5, 30, nameof(PostRoundDelaySeconds));
        RequireRange(MinimumInitialZombies, 1, 63, nameof(MinimumInitialZombies));
        RequireRange(InitialZombieRatio, 0.01, 1, nameof(InitialZombieRatio));
        RequireRange(MaximumInitialZombies, 0, 63, nameof(MaximumInitialZombies));
        RequireRange(ZombieHealth, 1, 100000, nameof(ZombieHealth));
        RequireRange(MotherZombieHealth, ZombieHealth, 100000, nameof(MotherZombieHealth));
        RequireRange(ZombieSpeed, 0.1f, 3, nameof(ZombieSpeed));
        RequireRange(ZombieLives, 0, 100, nameof(ZombieLives));
        RequireRange(ZombieRespawnDelaySeconds, 0.1, 30, nameof(ZombieRespawnDelaySeconds));
        RequireRange(CorpseInfectionDelaySeconds, 0.1, 30, nameof(CorpseInfectionDelaySeconds));
        RequireRange(KnockbackDamageScale, 0, 100, nameof(KnockbackDamageScale));
        RequireRange(KnockbackMaxHorizontalSpeed, 0, 3000, nameof(KnockbackMaxHorizontalSpeed));
        RequireRange(ZombieKnifeLightDamage, 1, 1000, nameof(ZombieKnifeLightDamage));
        RequireRange(ZombieKnifeHeavyDamage, 1, 1000, nameof(ZombieKnifeHeavyDamage));
        RequireRange(HumanHealth, 1, 10000, nameof(HumanHealth));
        RequireRange(HumanArmor, 0, 5000, nameof(HumanArmor));
        RequireRange(ZombieArmor, 0, 10000, nameof(ZombieArmor));
        RequireRange(FallSoundSuppressSeconds, 0.05, 1.5, nameof(FallSoundSuppressSeconds));
        RequireRange(FallSoundVelocityThreshold, 200, 2000, nameof(FallSoundVelocityThreshold));

        if (ZombieModels.Any(string.IsNullOrWhiteSpace)
            || MotherZombieModels.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException("Zombie model paths cannot be blank.");
        }
    }

    public IEnumerable<string> ConfiguredModels()
        => ZombieModels
            .Concat(MotherZombieModels)
            .Append(CorpseMarkerModel)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static void RequireRange<T>(T value, T minimum, T maximum, string name)
        where T : INumber<T>
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidDataException($"{name} must be between {minimum} and {maximum}.");
        }
    }
}
