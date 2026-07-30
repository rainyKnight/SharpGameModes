namespace SharpGameModes.BotMatch;

public sealed class BotMatchConfig
{
    public int SchemaVersion { get; init; } = 1;
    public bool Enabled { get; init; } = true;
    public string Prefix { get; init; } = "[BotMatch]";
    public int BotQuota { get; init; } = 10;
    public string BotQuotaMode { get; init; } = "fill";
    public bool BalanceBotTeams { get; init; } = true;
    public double TeamBalanceDelaySeconds { get; init; } = 0.75;
    public bool HideBotIdentity { get; init; } = true;
    public bool UseBotInfoNames { get; init; } = true;
    public ulong SyntheticSteamIdBase { get; init; } = 76561199500000000UL;
    public int FakePingMin { get; init; } = 18;
    public int FakePingMax { get; init; } = 72;
    public bool KnifeAfterTeamElimination { get; init; } = true;
    public bool EnableBotAi { get; init; } = true;
    public bool EnableBotState { get; init; } = true;
    public bool EnableBotBuy { get; init; } = true;
    public bool EnableBotCosmetics { get; init; } = true;
    public bool EnableDamageRecap { get; init; } = true;
    public string DamageRecapStyle { get; init; } = "auto";
    public string DifficultyTier { get; init; } = "hltvtop10";
    public string[] PersonaNames { get; init; } =
    [
        "Aster", "Breeze", "Cobalt", "Drizzle", "Echo",
        "Frost", "Glint", "Harbor", "Iris", "Jade",
        "Kite", "Lumen", "Mica", "Nova", "Onyx",
        "Pine", "Quartz", "Rook", "Sol", "Tide",
    ];
    public string AimMode { get; init; } = "mixed";
    public string NadeMode { get; init; } = "less";
    public Dictionary<string, string> ConVars { get; init; } = new(StringComparer.Ordinal)
    {
        ["bot_difficulty"] = "5",
        ["custom_bot_difficulty"] = "5",
        ["sv_auto_adjust_bot_difficulty"] = "false",
        ["bot_allow_grenades"] = "0",
        ["bot_max_visible_smoke_length"] = "50",
        ["bot_allow_rogues"] = "0",
        ["bot_eco_limit"] = "2800",
        ["bot_randombuy"] = "0",
        ["bot_defer_to_human_items"] = "0",
        ["bot_defer_to_human_goals"] = "0",
        ["bot_defense_rush_chance"] = "0",
        ["bot_auto_vacate"] = "1",
        ["bot_auto_follow"] = "0",
        ["bot_join_after_player"] = "0",
        ["bot_stop"] = "0",
        ["bot_freeze"] = "0",
        ["bot_max_vision_distance_override"] = "30000",
        ["bot_coop_idle_max_vision_distance"] = "30000",
        ["nav_pathfind_multithread"] = "true",
        ["nav_test_path_return"] = "true",
        ["nav_test_detour"] = "true",
        ["nav_pathfind_inadmissable_heuristic_factor"] = "2.5",
        ["nav_potentially_visible_dot_tolerance"] = "-1",
        ["sv_shared_team_pvs"] = "true",
        ["ai_use_visibility_cache"] = "2",
        ["nav_max_view_distance"] = "30000",
    };

    public void Validate()
    {
        if (SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported botmatch schema_version {SchemaVersion}.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Prefix);
        if (!BotDifficultyTierPolicy.TryResolve(DifficultyTier, out _))
        {
            throw new InvalidDataException(
                "difficulty_tier must be low, medium, hltvtop10 or high.");
        }

        if (BotQuota is < 0 or > 63)
        {
            throw new InvalidDataException("bot_quota must be between 0 and 63.");
        }

        if (!BotQuotaMode.Equals("fill", StringComparison.OrdinalIgnoreCase)
            && !BotQuotaMode.Equals("normal", StringComparison.OrdinalIgnoreCase)
            && !BotQuotaMode.Equals("match", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("bot_quota_mode must be fill, normal or match.");
        }

        if (TeamBalanceDelaySeconds is < 0.1 or > 10)
        {
            throw new InvalidDataException("team_balance_delay_seconds must be between 0.1 and 10.");
        }

        if (SyntheticSteamIdBase is < 76561197960265728UL or > 76561202255233023UL)
        {
            throw new InvalidDataException("synthetic_steam_id_base must be a valid individual SteamID64.");
        }

        if (FakePingMin is < 1 or > 999 || FakePingMax < FakePingMin || FakePingMax > 999)
        {
            throw new InvalidDataException("fake ping range must satisfy 1 <= min <= max <= 999.");
        }

        if (PersonaNames.Length == 0 || PersonaNames.Length > 64)
        {
            throw new InvalidDataException("persona_names must contain between 1 and 64 names.");
        }

        foreach (var name in PersonaNames)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (name.Length > 31)
            {
                throw new InvalidDataException("persona names must contain at most 31 characters.");
            }
        }

        if (!AimMode.Equals("mixed", StringComparison.OrdinalIgnoreCase)
            && !AimMode.Equals("head", StringComparison.OrdinalIgnoreCase)
            && !AimMode.Equals("body", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("aim_mode must be mixed, head or body.");
        }

        if (!NadeMode.Equals("off", StringComparison.OrdinalIgnoreCase)
            && !NadeMode.Equals("less", StringComparison.OrdinalIgnoreCase)
            && !NadeMode.Equals("normal", StringComparison.OrdinalIgnoreCase)
            && !NadeMode.Equals("more", StringComparison.OrdinalIgnoreCase)
            && !NadeMode.Equals("max", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("nade_mode must be off, less, normal, more or max.");
        }

        if (!RoundDamageRecapPolicy.TryParseStyle(DamageRecapStyle, out _))
        {
            throw new InvalidDataException("damage_recap_style must be auto, classic or pw.");
        }

        foreach (var (name, value) in ConVars)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(value);
            if (name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_')))
            {
                throw new InvalidDataException($"Invalid ConVar name '{name}'.");
            }
        }
    }
}
