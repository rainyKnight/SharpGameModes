namespace SharpGameModes.TeamDeathmatch;

public sealed class TeamDeathmatchConfig
{
    public bool Enabled { get; init; } = true;
    public string Prefix { get; init; } = "[TDM]";
    public int ScoreLimit { get; init; } = 100;
    public double MatchTimeLimitSeconds { get; init; } = 600;
    public double RespawnDelaySeconds { get; init; } = 1.5;
    public string DefaultPrimary { get; init; } = "ak";
    public string DefaultSecondary { get; init; } = "de";
    public string DefaultGrenade { get; init; } = "hegrenade";
    public bool SpawnFullArmor { get; init; } = true;
    public bool SpawnHelmet { get; init; } = true;
    public double RespawnImmunitySeconds { get; init; } = 5;
    public double MatchEndFallbackDelaySeconds { get; init; } = 4;
    public bool ShowBuyHelpOnRoundStart { get; init; } = true;
    public string BuyHelpMessage { get; init; }
        = "{prefix} 输入 !guns 查看武器指令，例如 !ak、!a1、!awp、!de，也支持 ！ 和 . 前缀。";

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(DefaultPrimary);
        ArgumentException.ThrowIfNullOrWhiteSpace(DefaultSecondary);
        ArgumentNullException.ThrowIfNull(DefaultGrenade);
        ArgumentException.ThrowIfNullOrWhiteSpace(BuyHelpMessage);

        if (ScoreLimit is < 1 or > 1000)
        {
            throw new InvalidDataException("score_limit must be between 1 and 1000.");
        }

        if (!double.IsFinite(MatchTimeLimitSeconds) || MatchTimeLimitSeconds is < 30 or > 7200)
        {
            throw new InvalidDataException("match_time_limit_seconds must be between 30 and 7200.");
        }

        if (!double.IsFinite(RespawnDelaySeconds) || RespawnDelaySeconds is < 0.1 or > 10)
        {
            throw new InvalidDataException("respawn_delay_seconds must be between 0.1 and 10.");
        }

        if (!double.IsFinite(RespawnImmunitySeconds) || RespawnImmunitySeconds is < 0 or > 30)
        {
            throw new InvalidDataException("respawn_immunity_seconds must be between 0 and 30.");
        }

        if (!double.IsFinite(MatchEndFallbackDelaySeconds) || MatchEndFallbackDelaySeconds is < 1 or > 30)
        {
            throw new InvalidDataException("match_end_fallback_delay_seconds must be between 1 and 30.");
        }

        if (!TdmWeaponCatalog.TryResolve(DefaultPrimary, out var primary)
            || primary.Slot != TdmWeaponSlot.Primary)
        {
            throw new InvalidDataException($"default_primary '{DefaultPrimary}' is not a supported primary weapon.");
        }

        if (!TdmWeaponCatalog.TryResolve(DefaultSecondary, out var secondary)
            || secondary.Slot != TdmWeaponSlot.Secondary)
        {
            throw new InvalidDataException($"default_secondary '{DefaultSecondary}' is not a supported secondary weapon.");
        }
    }
}
