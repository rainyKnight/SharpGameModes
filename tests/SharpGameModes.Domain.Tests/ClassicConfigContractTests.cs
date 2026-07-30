using System.Text.Json;

namespace SharpGameModes.Domain.Tests;

public sealed class ClassicConfigContractTests
{
    [Fact]
    public void ClassicCfg_ProvidesSafeMr8Example()
    {
        var values = ParseCfg(ConfigPath("csgo", "cfg", "sharp-gamemodes", "classic.cfg"));

        Assert.Equal("16", values["mp_maxrounds"]);
        Assert.Equal("9", values["mp_winlimit"]);
        Assert.Equal("45", values["mp_warmuptime"]);
        Assert.Equal("0", values["mp_solid_teammates"]);
        Assert.Equal("1", values["mp_friendlyfire"]);
        Assert.Equal("0", values["ff_damage_reduction_bullets"]);
        Assert.Equal("1", values["ff_damage_reduction_grenade"]);
        Assert.Equal("1", values["ff_damage_reduction_grenade_self"]);
        Assert.Equal("1", values["ff_damage_reduction_other"]);
    }

    [Fact]
    public void ClassicBotPolicy_KeepsOneVersusOneAndLetsHumansReplaceBots()
    {
        var values = ParseCfg(ConfigPath("csgo", "cfg", "sharp-gamemodes", "classic-bots.cfg"));

        Assert.Equal("fill", values["bot_quota_mode"]);
        Assert.Equal("2", values["bot_quota"]);
        Assert.Equal("1", values["bot_auto_vacate"]);
        Assert.Equal("0", values["bot_join_after_player"]);
        Assert.Equal("any", values["bot_join_team"]);
        Assert.Equal("1", values["bot_stop"]);
        Assert.Equal("1", values["bot_freeze"]);
    }

    [Fact]
    public void HumanOnlyModes_ClearInheritedBots()
    {
        var values = ParseCfg(ConfigPath("csgo", "cfg", "sharp-gamemodes", "no-bots.cfg"));

        Assert.Equal("0", values["bot_quota"]);
        Assert.Equal("1", values["bot_stop"]);
        Assert.Equal("1", values["bot_freeze"]);
    }

    [Fact]
    public void BotMatchPolicy_UnfreezesBotsAndKeepsUnsafeHiderDisabled()
    {
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        using var document = JsonDocument.Parse(
            File.ReadAllText(ConfigPath("sharp", "configs", "sharp-gamemodes", "botmatch.jsonc")),
            options);
        var conVars = document.RootElement.GetProperty("con_vars");
        var root = document.RootElement;

        Assert.Equal("0", conVars.GetProperty("bot_stop").GetString());
        Assert.Equal("0", conVars.GetProperty("bot_freeze").GetString());
        Assert.Equal("50", conVars.GetProperty("bot_max_visible_smoke_length").GetString());
        Assert.False(root.GetProperty("hide_bot_identity").GetBoolean());
        Assert.Equal("hltvtop10", root.GetProperty("difficulty_tier").GetString());
        Assert.Equal("less", root.GetProperty("nade_mode").GetString());
        Assert.InRange(root.GetProperty("fake_ping_min").GetInt32(), 1, 999);
        Assert.InRange(root.GetProperty("fake_ping_max").GetInt32(), 1, 999);
        Assert.NotEmpty(root.GetProperty("persona_names").EnumerateArray());
    }

    [Fact]
    public void RuntimeConfig_EnablesMigratedModes()
    {
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        using var document = JsonDocument.Parse(
            File.ReadAllText(ConfigPath("sharp", "configs", "sharp-gamemodes", "server.jsonc")),
            options);
        var enabledModes = document.RootElement.GetProperty("enabled_modes")
            .EnumerateArray()
            .Select(element => element.GetString()!)
            .ToArray();

        Assert.Equal(["classic", "tdm", "zombie", "botmatch"], enabledModes);
    }

    [Fact]
    public void AdminConfig_ContainsRoleExampleButNoRealAdministrators()
    {
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        using var document = JsonDocument.Parse(
            File.ReadAllText(ConfigPath("sharp", "configs", "admins.jsonc")),
            options);
        var root = document.RootElement;
        var rootRole = Assert.Single(root.GetProperty("Roles").EnumerateArray());

        Assert.Empty(root.GetProperty("Admins").EnumerateArray());
        Assert.Equal("root", rootRole.GetProperty("Name").GetString());
        Assert.Equal(
            ["*"],
            rootRole.GetProperty("Permissions").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void ClassicPool_ContainsExpectedOfficialDefusalMaps()
    {
        var catalog = MapCatalog.Load(ConfigPath("sharp", "configs", "sharp-gamemodes", "map-pools", "classic.jsonc"));
        string[] expected = ["de_dust2", "de_mirage", "de_inferno"];

        Assert.All(expected, map => Assert.NotNull(catalog.ResolvePhysicalMap(map)));
        Assert.All(catalog.Entries, entry => Assert.False(entry.Workshop));
    }

    [Fact]
    public void BotMatchPool_ContainsEverySupportedNavigationMapExceptNuke()
    {
        var catalog = MapCatalog.Load(
            ConfigPath("sharp", "configs", "sharp-gamemodes", "map-pools", "botmatch.jsonc"));
        string[] expected =
        [
            "de_mirage",
            "de_inferno",
            "de_anubis",
            "de_ancient",
            "de_dust2",
            "de_overpass",
            "de_vertigo",
            "de_train",
            "de_cache",
            "cs_office",
            "cs_italy",
        ];

        Assert.Equal(expected.Length, catalog.Entries.Count);
        Assert.All(expected, map => Assert.NotNull(catalog.ResolvePhysicalMap(map)));
        Assert.Null(catalog.ResolvePhysicalMap("de_nuke"));
    }

    [Fact]
    public void PlayerDataConfig_ProvidesNeutralExampleAndFormula()
    {
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        using var document = JsonDocument.Parse(
            File.ReadAllText(ConfigPath("sharp", "configs", "sharp-gamemodes", "player-data.jsonc")),
            options);
        var root = document.RootElement;
        var formula = root.GetProperty("rating_formula");

        Assert.True(root.GetProperty("record_player_data").GetBoolean());
        Assert.Equal(100, root.GetProperty("history_limit").GetInt32());
        Assert.Equal(5, root.GetProperty("trade_window_seconds").GetDouble());
        Assert.Empty(root.GetProperty("map_blacklist").EnumerateArray());
        Assert.True(root.GetProperty("print_top_players_to_chat").GetBoolean());
        Assert.Equal(
            "{gold}Player Rating Leaderboard{default}",
            root.GetProperty("top_players_chat_title").GetString());
        Assert.Empty(root.GetProperty("data_write_skip_whitelist").EnumerateArray());
        Assert.Equal(0.0073, formula.GetProperty("kast_coefficient").GetDouble());
        Assert.Equal(2.13, formula.GetProperty("impact_kill_coefficient").GetDouble());
        Assert.Equal(3, formula.GetProperty("max_round_rating").GetDouble());
    }

    [Fact]
    public void AutoTeamConfig_ProvidesConfigurablePrefixWithoutPrivateSteamIds()
    {
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        using var document = JsonDocument.Parse(
            File.ReadAllText(ConfigPath("sharp", "configs", "sharp-gamemodes", "auto-team.jsonc")),
            options);
        var root = document.RootElement;
        var policy = root.GetProperty("low_rating_health_compensation");

        Assert.True(root.GetProperty("balance_health_by_rating").GetBoolean());
        Assert.Equal("[AutoTeam]", root.GetProperty("prefix").GetString());
        Assert.True(root.GetProperty("disable_native_team_balance").GetBoolean());
        Assert.Equal(0, root.GetProperty("allowed_count_deviation").GetInt32());
        Assert.Equal("balance_only", root.GetProperty("round_randomize_mode").GetString());
        Assert.Empty(root.GetProperty("observer_whitelist").EnumerateArray());
        Assert.Equal(0.2, root.GetProperty("round_start_balance_delay_seconds").GetDouble());
        Assert.Empty(root.GetProperty("health_compensation_blacklist").EnumerateArray());
        Assert.Equal(
            "data/sharp-gamemodes/autoteamlock_health_compensation.json",
            root.GetProperty("health_compensation_state_path").GetString());
        Assert.Equal(1.0, policy.GetProperty("target_rating").GetDouble());
        Assert.Equal(1000, policy.GetProperty("max_health").GetInt32());
        Assert.Equal(0.35, policy.GetProperty("learning_rate").GetDouble());
        Assert.Equal(0.3, policy.GetProperty("rating_ema_alpha").GetDouble());
        Assert.Equal(8, policy.GetProperty("minimum_rounds").GetInt32());
        Assert.Equal(0.1, policy.GetProperty("rating_error_deadband").GetDouble());
        Assert.Equal(0.1, policy.GetProperty("max_health_adjustment_ratio").GetDouble());
    }

    [Fact]
    public void ModePoolsOwnAutoTeamRules()
    {
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        using var classicDocument = JsonDocument.Parse(
            File.ReadAllText(ConfigPath("sharp", "configs", "sharp-gamemodes", "map-pools", "classic.jsonc")),
            options);
        using var tdmDocument = JsonDocument.Parse(
            File.ReadAllText(ConfigPath("sharp", "configs", "sharp-gamemodes", "map-pools", "tdm.jsonc")),
            options);
        using var zombieDocument = JsonDocument.Parse(
            File.ReadAllText(ConfigPath("sharp", "configs", "sharp-gamemodes", "map-pools", "zombie.jsonc")),
            options);
        var classic = classicDocument.RootElement.GetProperty("auto_team");
        var tdm = tdmDocument.RootElement.GetProperty("auto_team");
        var zombie = zombieDocument.RootElement.GetProperty("auto_team");

        Assert.True(classic.GetProperty("enabled").GetBoolean());
        Assert.Equal("first_round_then_balance", classic.GetProperty("round_randomize_mode").GetString());
        Assert.True(classic.GetProperty("record_player_data").GetBoolean());
        Assert.True(tdm.GetProperty("enabled").GetBoolean());
        Assert.False(tdm.GetProperty("record_player_data").GetBoolean());
        Assert.False(tdm.GetProperty("print_top_players_to_chat").GetBoolean());
        Assert.False(zombie.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void MapSystemConfig_ProvidesVoteAndRtvExample()
    {
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        using var document = JsonDocument.Parse(
            File.ReadAllText(ConfigPath("sharp", "configs", "sharp-gamemodes", "map-system.jsonc")),
            options);
        var root = document.RootElement;
        var vote = root.GetProperty("vote");
        var rtv = root.GetProperty("rtv");
        var sourceOffer = root.GetProperty("source_offer");
        var classic = root.GetProperty("mode_auto_change_rules").GetProperty("classic");
        var tdm = root.GetProperty("mode_auto_change_rules").GetProperty("tdm");
        var zombie = root.GetProperty("mode_auto_change_rules").GetProperty("zombie");

        Assert.Equal(25, vote.GetProperty("duration_seconds").GetInt32());
        Assert.Equal(5, vote.GetProperty("maps_in_vote").GetInt32());
        Assert.Equal(3, vote.GetProperty("remember_played_maps").GetInt32());
        Assert.Equal(8, root.GetProperty("map_change").GetProperty("delay_after_match_seconds").GetDouble());
        Assert.Equal(30, rtv.GetProperty("initial_delay_seconds").GetInt32());
        Assert.Equal(0.5, rtv.GetProperty("required_ratio").GetDouble());
        Assert.Equal(5, root.GetProperty("nomination").GetProperty("page_size").GetInt32());
        Assert.False(sourceOffer.GetProperty("show_on_join").GetBoolean());
        Assert.Equal("https://github.com/rainyKnight/SharpGameModes", sourceOffer.GetProperty("url").GetString());
        Assert.Contains(
            "source",
            sourceOffer.GetProperty("commands").EnumerateArray().Select(command => command.GetString()));
        Assert.Equal(8, classic.GetProperty("vote_start_round").GetInt32());
        Assert.Equal("rounds", tdm.GetProperty("auto_change_mode").GetString());
        Assert.Equal(1, tdm.GetProperty("vote_start_round").GetInt32());
        Assert.Equal("rounds_sum", zombie.GetProperty("auto_change_mode").GetString());
        Assert.Equal(3, zombie.GetProperty("vote_start_round").GetInt32());
        Assert.Equal(6, zombie.GetProperty("change_after_round").GetInt32());
    }

    [Fact]
    public void TeamDeathmatchConfig_ProvidesRunnableExample()
    {
        var cfg = ParseCfg(ConfigPath("csgo", "cfg", "sharp-gamemodes", "tdm.cfg"));
        Assert.Equal("1", cfg["mp_maxrounds"]);
        Assert.Equal("1", cfg["mp_ignore_round_win_conditions"]);
        Assert.Equal("5", cfg["mp_respawn_immunitytime"]);
        Assert.Equal("0", cfg["mp_friendlyfire"]);
        Assert.Equal("0", cfg["mp_solid_teammates"]);

        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        using var document = JsonDocument.Parse(
            File.ReadAllText(ConfigPath("sharp", "configs", "sharp-gamemodes", "tdm.jsonc")),
            options);
        var root = document.RootElement;

        Assert.True(root.GetProperty("enabled").GetBoolean());
        Assert.Equal("[TDM]", root.GetProperty("prefix").GetString());
        Assert.Contains("{prefix}", root.GetProperty("buy_help_message").GetString());
        Assert.Equal(100, root.GetProperty("score_limit").GetInt32());
        Assert.Equal(600, root.GetProperty("match_time_limit_seconds").GetDouble());
        Assert.Equal(1.5, root.GetProperty("respawn_delay_seconds").GetDouble());
        Assert.Equal("ak", root.GetProperty("default_primary").GetString());
        Assert.Equal("de", root.GetProperty("default_secondary").GetString());
        Assert.Equal("hegrenade", root.GetProperty("default_grenade").GetString());
        Assert.True(root.GetProperty("spawn_full_armor").GetBoolean());
        Assert.True(root.GetProperty("spawn_helmet").GetBoolean());
        Assert.Equal(5, root.GetProperty("respawn_immunity_seconds").GetDouble());
        Assert.Equal(4, root.GetProperty("match_end_fallback_delay_seconds").GetDouble());
    }

    [Fact]
    public void ZombieConfig_ProvidesStockResourceExample()
    {
        var cfg = ParseCfg(ConfigPath("csgo", "cfg", "sharp-gamemodes", "zombie.cfg"));
        Assert.Equal("3", cfg["mp_roundtime"]);
        Assert.Equal("1", cfg["mp_ignore_round_win_conditions"]);
        Assert.Equal("0", cfg["mp_falldamage"]);
        Assert.Equal("1", cfg["sv_enablebunnyhopping"]);
        Assert.Equal("100", cfg["sv_airaccelerate"]);

        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        using var document = JsonDocument.Parse(
            File.ReadAllText(ConfigPath("sharp", "configs", "sharp-gamemodes", "zombie.jsonc")),
            options);
        var root = document.RootElement;

        Assert.Equal(2, root.GetProperty("minimum_players").GetInt32());
        Assert.False(root.GetProperty("include_bots_in_round").GetBoolean());
        Assert.Equal(15, root.GetProperty("first_infection_delay_seconds").GetInt32());
        Assert.Equal(180, root.GetProperty("round_duration_seconds").GetInt32());
        Assert.Equal(18000, root.GetProperty("zombie_health").GetInt32());
        Assert.Equal(30000, root.GetProperty("mother_zombie_health").GetInt32());
        Assert.Equal(1.25, root.GetProperty("zombie_speed").GetDouble());
        Assert.Equal(5, root.GetProperty("corpse_infection_delay_seconds").GetDouble());
        Assert.Equal(60, root.GetProperty("zombie_knife_light_damage").GetDouble());
        Assert.Equal(120, root.GetProperty("zombie_knife_heavy_damage").GetDouble());
        Assert.True(root.GetProperty("knockback_enabled").GetBoolean());
        Assert.Equal(10, root.GetProperty("knockback_damage_scale").GetDouble());
        Assert.Equal(1200, root.GetProperty("knockback_max_horizontal_speed").GetDouble());
        Assert.Equal(1, root.GetProperty("fall_sound_suppress_seconds").GetDouble());
        Assert.Equal(500, root.GetProperty("fall_sound_velocity_threshold").GetDouble());
        Assert.True(root.GetProperty("corpse_marker_enabled").GetBoolean());
        Assert.Equal("idle", root.GetProperty("corpse_marker_animation").GetString());
        Assert.Equal("[Zombie]", root.GetProperty("prefix").GetString());
        Assert.Contains("{prefix}", root.GetProperty("weapon_help_message").GetString());
        Assert.Equal(
            ["characters/models/tm_phoenix/tm_phoenix.vmdl"],
            root.GetProperty("zombie_models").EnumerateArray().Select(item => item.GetString()));

        using var roleSoundDocument = JsonDocument.Parse(
            File.ReadAllText(ConfigPath("sharp", "configs", "sharp-gamemodes", "rolesound.jsonc")),
            options);
        var roleSound = roleSoundDocument.RootElement;
        Assert.False(roleSound.TryGetProperty("SuppressFallDamageSound", out _));
        Assert.False(roleSound.TryGetProperty("FallSoundSuppressSeconds", out _));
        Assert.False(roleSound.TryGetProperty("FallSoundVelocityThreshold", out _));
    }

    [Fact]
    public void CosmeticsConfig_UsesDisabledStockModelExampleAndPreservesSkinCatalog()
    {
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        using var cosmetics = JsonDocument.Parse(
            File.ReadAllText(ConfigPath("sharp", "configs", "sharp-gamemodes", "cosmetics.jsonc")),
            options);
        using var models = JsonDocument.Parse(
            File.ReadAllText(
                ConfigPath("sharp", "configs", "sharp-gamemodes", "player-models.jsonc")),
            options);
        var root = cosmetics.RootElement;
        var modelRoot = models.RootElement;

        Assert.True(root.GetProperty("weapon_skins_enabled").GetBoolean());
        Assert.True(root.GetProperty("knives_enabled").GetBoolean());
        Assert.False(root.TryGetProperty("player_models_enabled", out _));
        Assert.False(root.TryGetProperty("player_model_commands", out _));
        Assert.False(root.TryGetProperty("player_model_catalog_path", out _));
        Assert.Equal("s", root.GetProperty("weapon_skin_commands")[0].GetString());
        Assert.Equal("k", root.GetProperty("knife_commands")[0].GetString());
        Assert.False(modelRoot.GetProperty("Enabled").GetBoolean());
        var modelExamples = modelRoot.GetProperty("Models");
        Assert.Equal(3, modelExamples.EnumerateObject().Count());
        Assert.False(modelExamples.GetProperty("example_both").TryGetProperty("side", out _));

        var skins = WeaponSkinCatalog.Parse(
            File.ReadAllText(ConfigPath("sharp", "data", "sharp-gamemodes", "cosmetics", "skins_en.json")));
        Assert.Equal(55, skins.Weapons.Count);
        Assert.Equal(2033, skins.Weapons.Sum(group => group.Paints.Count));
    }

    [Fact]
    public void RoleSoundConfig_ListsEveryReferenceEventAsAnExample()
    {
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        using var document = JsonDocument.Parse(
            File.ReadAllText(
                ConfigPath("sharp", "configs", "sharp-gamemodes", "rolesound.jsonc")),
            options);
        var root = document.RootElement;
        var expectedEvents = new[]
        {
            "death",
            "hurt",
            "kill",
            "radio.cheer",
            "radio.followme",
            "radio.generic",
            "radio.holdpos",
            "radio.negative",
            "radio.roger",
            "radio.thanks",
            "reload",
            "round_end",
            "round_start",
            "spell",
            "throw",
        };

        static string[] PropertyNames(JsonElement element)
            => element.EnumerateObject()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

        Assert.False(root.GetProperty("Enabled").GetBoolean());
        Assert.Equal(expectedEvents, PropertyNames(root.GetProperty("SoundEventNames")));
        Assert.Equal(expectedEvents, PropertyNames(root.GetProperty("EventKeywords")));
        Assert.Equal(
            expectedEvents,
            PropertyNames(
                root.GetProperty("VoiceProfiles")
                    .GetProperty("example_voice")
                    .GetProperty("Events")));
        Assert.Equal(6, root.GetProperty("RadioCommandToKey").EnumerateObject().Count());
        Assert.Equal(6, root.GetProperty("RadioSlotToKey").EnumerateObject().Count());
    }

    [Fact]
    public void CoreConfig_UsesDiscoverableMenuControls()
    {
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        using var document = JsonDocument.Parse(
            File.ReadAllText(ConfigPath("sharp", "configs", "core.json")),
            options);
        var bindings = document.RootElement
            .GetProperty("MenuManager")
            .GetProperty("KeyBindings");

        Assert.Equal("Forward", bindings.GetProperty("MoveUpCursor").GetProperty("Button").GetString());
        Assert.Equal("Back", bindings.GetProperty("MoveDownCursor").GetProperty("Button").GetString());
        Assert.Equal("Use", bindings.GetProperty("Confirm").GetProperty("Button").GetString());
        Assert.Equal("Reload", bindings.GetProperty("GoBack").GetProperty("Button").GetString());
        Assert.Equal("Scoreboard", bindings.GetProperty("Exit").GetProperty("Button").GetString());
    }

    private static Dictionary<string, string> ParseCfg(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Split("//", 2, StringSplitOptions.None)[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 2)
            {
                result[fields[0]] = fields[1].Trim().Trim('"');
            }
        }

        return result;
    }

    private static string ConfigPath(params string[] parts)
        => Path.Combine([AppContext.BaseDirectory, "Config", .. parts]);
}
