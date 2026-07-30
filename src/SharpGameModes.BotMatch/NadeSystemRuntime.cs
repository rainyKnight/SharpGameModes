using Microsoft.Extensions.Logging;
using SharpGameModes.Contracts;
using Sharp.Shared;
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameEvents;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace SharpGameModes.BotMatch;

/// <summary>
/// Pure ModSharp port of CS2-Bot-Improver NadeSystem 1.1.7.
/// The hot path uses a 200-unit lineup grid and one materialized player list
/// every four frames instead of scanning the entity table per bot/lineup.
/// </summary>
internal sealed class NadeSystemRuntime : IDisposable
{
    private const float EyeHeight = 64f;
    private const float SoundInfoRadiusSquared = 100f * 100f;
    private const float SoundHearRadiusSquared = 1000f * 1000f;
    private const float FootstepSpeedSquared = 150f * 150f;

    private static readonly Dictionary<string, float> ThrowSchedule =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["de_dust2_T"] = 13f,
            ["de_dust2_CT"] = 13f,
            ["de_ancient_T"] = 14f,
            ["de_ancient_CT"] = 14f,
            ["de_inferno_T"] = 15.5f,
            ["de_inferno_CT"] = 15.5f,
            ["de_mirage_T"] = 21f,
            ["de_mirage_CT"] = 21f,
            ["de_nuke_T"] = 14f,
            ["de_nuke_CT"] = 14f,
            ["de_anubis_T"] = 14f,
            ["de_anubis_CT"] = 14f,
            ["de_train_T"] = 17f,
            ["de_train_CT"] = 17f,
            ["de_vertigo_T"] = 11f,
            ["de_vertigo_CT"] = 11f,
            ["de_overpass_T"] = 20f,
            ["de_overpass_CT"] = 20f,
            ["de_cache_T"] = 15.5f,
            ["de_cache_CT"] = 15.5f,
        };

    private static readonly Dictionary<string, float> CooldownSeconds =
        new(StringComparer.Ordinal)
        {
            ["smoke"] = 19f,
            ["flash"] = 4f,
            ["he"] = 5f,
            ["molotov"] = 10f,
            ["decoy"] = 600f,
        };

    private static readonly Dictionary<string, int> CostT =
        new(StringComparer.Ordinal)
        {
            ["flash"] = 200,
            ["smoke"] = 300,
            ["he"] = 300,
            ["molotov"] = 400,
            ["decoy"] = 0,
        };

    private static readonly Dictionary<string, int> CostCT =
        new(StringComparer.Ordinal)
        {
            ["flash"] = 200,
            ["smoke"] = 300,
            ["he"] = 300,
            ["molotov"] = 500,
            ["decoy"] = 0,
        };

    private readonly IModSharp _modSharp;
    private readonly IClientManager _clients;
    private readonly IConVarManager _conVars;
    private readonly IEntityManager _entities;
    private readonly IPhysicsQueryManager _traces;
    private readonly ISchemaManager _schema;
    private readonly ILogger _logger;
    private readonly NadeProjectileFactory _factory;
    private readonly Random _random = new();
    private readonly Dictionary<string, float> _cooldowns =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _probabilityCooldowns =
        new(StringComparer.Ordinal);
    private readonly HashSet<int> _replayBots = [];
    private readonly HashSet<int> _smokeCooldownBots = [];
    private readonly Dictionary<int, int> _roundSpend = [];
    private readonly HashSet<int> _poorBots = [];
    private readonly Dictionary<int, NadeRoundCounter> _teamRoundCounts = [];
    private readonly Dictionary<int, NadeRoundCounter> _botRoundCounts = [];
    private readonly Dictionary<int, float> _molotovDamageStart = [];
    private readonly Dictionary<CStrikeTeam, float> _molotovEscapeCooldown = [];
    private readonly Dictionary<CStrikeTeam, float> _retaliationCooldown = [];
    private readonly Dictionary<CStrikeTeam, int> _earlySmokeCount = [];
    private readonly Dictionary<int, HashSet<string>> _flashZones = [];
    private readonly Dictionary<int, FlashRatioWindow> _flashRatioWindows = [];
    private readonly Dictionary<int, float> _flashImmunity = [];
    private readonly Dictionary<int, List<SoundPoint>> _soundPoints = [];
    private readonly Dictionary<int, float> _lastFireTime = [];
    private NadeLineupCatalog? _catalog;
    private BotNadeMode _mode = BotNadeMode.Normal;
    private string _lineupDirectory = string.Empty;
    private string _mapName = string.Empty;
    private bool _active;
    private bool _frameHookInstalled;
    private bool _roundOver;
    private bool _bombPlanted;
    private bool _defuseSmokeUsed;
    private bool _defuseFlashUsed;
    private bool _plantSmokeUsed;
    private float _freezeEndTime;
    private int _generation;
    private int _hasBeenControlledOffset;
    private int _spottedStateOffset;
    private int _spottedMaskOffset;
    private int _blindStartOffset;
    private int _blindUntilOffset;
    private long _scans;
    private long _candidates;
    private long _replays;
    private long _rayTraces;
    private long _errors;

    public NadeSystemRuntime(
        ISharedSystem shared,
        IClientManager clients,
        ILogger logger)
    {
        _modSharp = shared.GetModSharp();
        _clients = clients;
        _conVars = shared.GetConVarManager();
        _entities = shared.GetEntityManager();
        _traces = shared.GetPhysicsQueryManager();
        _schema = shared.GetSchemaManager();
        _logger = logger;
        _factory = new NadeProjectileFactory(shared, logger);
    }

    public string CurrentMode => NadeSystemPolicy.FormatMode(_mode);

    public bool Activate(string mode, string lineupDirectory)
    {
        if (_active)
        {
            return true;
        }

        try
        {
            if (!NadeSystemPolicy.TryParseMode(mode, out _mode))
            {
                throw new InvalidDataException($"Invalid NadeSystem mode '{mode}'.");
            }

            _lineupDirectory = lineupDirectory;
            _hasBeenControlledOffset = Offset(
                "CCSPlayerController",
                "m_bHasBeenControlledByPlayerThisRound");
            _spottedStateOffset = OffsetFromAny(
                "m_entitySpottedState",
                "CCSPlayerPawn",
                "CCSPlayerPawnBase",
                "CBasePlayerPawn",
                "CBaseEntity");
            _spottedMaskOffset = Offset(
                "EntitySpottedState_t",
                "m_bSpottedByMask");
            _blindStartOffset = Offset("CCSPlayerPawnBase", "m_blindStartTime");
            _blindUntilOffset = Offset("CCSPlayerPawnBase", "m_blindUntilTime");
            if (_mode != BotNadeMode.Off && !_factory.Activate())
            {
                return false;
            }

            _active = true;
            _generation++;
            ReloadForCurrentMap();
            ResetRoundState();
            _modSharp.InstallGameFrameHook(OnGameFramePre, null, prePriority: 10);
            _frameHookInstalled = true;
            _logger.LogInformation(
                "Pure ModSharp NadeSystem 1.1.7 enabled in {Mode} mode with {Count} {Map} lineups.",
                CurrentMode,
                _catalog?.Lineups.Count ?? 0,
                _mapName);
            return true;
        }
        catch (Exception exception)
        {
            _active = false;
            RemoveFrameHook();
            _factory.Deactivate();
            ClearState();
            _logger.LogError(
                exception,
                "Failed to enable pure ModSharp NadeSystem.");
            return false;
        }
    }

    public void Deactivate()
    {
        if (!_active && !_frameHookInstalled)
        {
            return;
        }

        _active = false;
        _generation++;
        RemoveFrameHook();
        _factory.Deactivate();
        ClearState();
        _logger.LogInformation(
            "Pure ModSharp NadeSystem disabled. Scans {Scans}, candidates {Candidates}, replays {Replays}, traces {Traces}, errors {Errors}.",
            Interlocked.Read(ref _scans),
            Interlocked.Read(ref _candidates),
            Interlocked.Read(ref _replays),
            Interlocked.Read(ref _rayTraces),
            Interlocked.Read(ref _errors));
    }

    public bool TrySetMode(string? value)
    {
        if (!NadeSystemPolicy.TryParseMode(value, out var mode))
        {
            return false;
        }

        if (_mode == mode)
        {
            return true;
        }

        _mode = mode;
        if (_active)
        {
            if (_mode == BotNadeMode.Off)
            {
                _factory.Deactivate();
            }
            else if (!_factory.Activate())
            {
                _mode = BotNadeMode.Off;
                return false;
            }

            ResetRoundState();
        }

        _logger.LogInformation("NadeSystem mode changed to {Mode}.", CurrentMode);
        return true;
    }

    public bool TryDiagnosticSpawn(
        string? grenadeType,
        out string result)
    {
        var normalized = NadeSystemPolicy.NormalizeType(
            grenadeType ?? string.Empty);
        if (!_active
            || _mode == BotNadeMode.Off
            || normalized is not ("flash" or "smoke" or "he" or "molotov"))
        {
            result =
                "NadeSystem must be active and type must be flash, smoke, he or molotov.";
            return false;
        }

        var bot = SnapshotPlayers().FirstOrDefault(
            player => player.IsManagedBot
                && player.IsAlive
                && player.Pawn is not null);
        if (bot.Pawn is null)
        {
            result = "No live managed bot is available.";
            return false;
        }

        var eye = bot.Pawn.GetEyePosition();
        var yaw = bot.Pawn.GetEyeAngles().Y * MathF.PI / 180f;
        var velocity = new Vector(
            MathF.Cos(yaw) * 450f,
            MathF.Sin(yaw) * 450f,
            300f);
        ScheduleSpawn(
            bot.Slot,
            bot.Team,
            normalized,
            eye,
            velocity,
            "diagnostic",
            countReplay: true);
        result =
            $"Scheduled {normalized} diagnostic spawn for bot slot {bot.Slot}.";
        return true;
    }

    public void ReloadForCurrentMap()
    {
        if (!_active)
        {
            return;
        }

        _mapName = _modSharp.GetGlobals().MapName;
        _catalog = NadeLineupCatalog.Load(_lineupDirectory, _mapName);
        _logger.LogInformation(
            "NadeSystem loaded {Count} indexed lineups for {Map}.",
            _catalog.Lineups.Count,
            _mapName);
    }

    public void Release(IGameClient client)
    {
        var slot = client.Slot.AsPrimitive();
        _replayBots.Remove(slot);
        _smokeCooldownBots.Remove(slot);
        _poorBots.Remove(slot);
        _roundSpend.Remove(slot);
        _botRoundCounts.Remove(slot);
        _molotovDamageStart.Remove(slot);
        _flashZones.Remove(slot);
        _flashRatioWindows.Remove(slot);
        _flashImmunity.Remove(slot);
        _soundPoints.Remove(slot);
        _lastFireTime.Remove(slot);
    }

    public void HandleGameEvent(IGameEvent gameEvent)
    {
        if (!_active)
        {
            return;
        }

        try
        {
            switch (gameEvent.Name)
            {
                case "round_start":
                    ResetRoundState();
                    SnapshotPoorBots();
                    break;
                case "round_freeze_end":
                    _freezeEndTime = Now;
                    break;
                case "round_end":
                    _roundOver = true;
                    break;
                case "player_death":
                    RemoveSoundTrail(gameEvent.GetPlayerController("userid"));
                    break;
                case "weapon_fire":
                    HandleWeaponFire(gameEvent);
                    break;
                case "weapon_reload":
                case "weapon_zoom":
                case "grenade_thrown":
                case "player_jump":
                    RecordSoundPoint(gameEvent.GetPlayerController("userid"));
                    break;
                case "bomb_begindefuse":
                    HandleBombBeginDefuse(gameEvent);
                    break;
                case "bomb_beginplant":
                    HandleBombBeginPlant(gameEvent);
                    break;
                case "bomb_planted":
                    _bombPlanted = true;
                    break;
                case "bomb_defused":
                case "bomb_exploded":
                    _bombPlanted = false;
                    break;
                case "player_hurt" when gameEvent is IEventPlayerHurt hurt:
                    HandlePlayerHurt(hurt);
                    break;
                case "player_blind":
                    HandlePlayerBlind(gameEvent);
                    break;
            }
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _errors);
            _logger.LogWarning(
                exception,
                "NadeSystem event handler failed for {Event}.",
                gameEvent.Name);
        }
    }

    public void Dispose() => Deactivate();

    private void OnGameFramePre(bool simulating, bool firstTick, bool lastTick)
    {
        if (!_active || !simulating)
        {
            return;
        }

        try
        {
            var tick = _modSharp.GetGlobals().TickCount;
            if ((tick & 3) != 0)
            {
                return;
            }

            var players = SnapshotPlayers();
            UpdateSoundTrails(players, recordFootsteps: true);
            if (_mode != BotNadeMode.Off)
            {
                CheckBotZones(players);
            }

            if ((tick & 255) == 0)
            {
                PruneTimedState();
            }
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _errors);
            _logger.LogWarning(exception, "NadeSystem frame processing failed.");
        }
    }

    private void CheckBotZones(PlayerSnapshot[] players)
    {
        if (_catalog is null
            || _catalog.Lineups.Count == 0
            || _roundOver
            || _modSharp.GetGameRules().IsFreezePeriod)
        {
            return;
        }

        Interlocked.Increment(ref _scans);
        var hasEnemyT = players.Any(
            player => player.IsAlive && player.Team == CStrikeTeam.CT);
        var hasEnemyCT = players.Any(
            player => player.IsAlive && player.Team == CStrikeTeam.TE);

        foreach (var bot in players)
        {
            if (!bot.IsManagedBot
                || !bot.IsAlive
                || bot.Pawn is null
                || ReadBool(
                    bot.Controller.GetAbsPtr(),
                    _hasBeenControlledOffset)
                || _replayBots.Contains(bot.Slot)
                || (bot.Team == CStrikeTeam.TE ? !hasEnemyT : !hasEnemyCT))
            {
                continue;
            }

            var position = bot.Pawn.GetAbsOrigin();
            foreach (var lineup in _catalog.Query(position.X, position.Y)
                         .OrderBy(lineup => lineup.Order))
            {
                Interlocked.Increment(ref _candidates);
                if (!IsInsideZone(position, lineup))
                {
                    continue;
                }

                var grenadeType = lineup.GrenadeType;
                if (grenadeType == "decoy")
                {
                    if (!IsOnCooldown(lineup.Id))
                    {
                        RegisterCooldown(lineup.Id, grenadeType);
                        ScheduleSpawn(bot, lineup, countReplay: false);
                    }

                    break;
                }

                if (IsOnCooldown(lineup.Id)
                    || (grenadeType is "he" or "molotov" or "flash"
                        && IsOnProbabilityCooldown(lineup.Id))
                    || (grenadeType == "smoke"
                        && _smokeCooldownBots.Contains(bot.Slot))
                    || (grenadeType == "smoke"
                        && HasActiveSmokeNear(lineup.LandingPosition, 100f)))
                {
                    continue;
                }

                var directionCheck = _mode is BotNadeMode.Less
                    or BotNadeMode.Normal
                    or BotNadeMode.More
                    ? grenadeType is "smoke" or "flash"
                    : grenadeType == "smoke";
                if (directionCheck
                    && !NadeSystemPolicy.FacesThrowDirection(
                        bot.Pawn.GetEyeAngles().Y,
                        lineup.ProjectileVelocity.X,
                        lineup.ProjectileVelocity.Y))
                {
                    continue;
                }

                if (grenadeType == "flash"
                    && !EnterFlashZone(bot.Slot, lineup.Id))
                {
                    continue;
                }

                if (_mode == BotNadeMode.Max)
                {
                    if (grenadeType == "flash"
                        && CountBlindableEnemies(bot, lineup, players).Blindable == 0)
                    {
                        break;
                    }

                    if (grenadeType is "he" or "molotov"
                        && (FiredRecently(bot.Slot, 1f)
                            || !HasEnemyNearLanding(
                                bot,
                                lineup,
                                players,
                                300f)
                            || (grenadeType == "molotov"
                                && HasActiveSmokeNear(
                                    lineup.LandingPosition,
                                    200f))))
                    {
                        break;
                    }

                    TryReplay(bot, lineup, players);
                    break;
                }

                if (grenadeType == "flash")
                {
                    if (_flashRatioWindows.TryGetValue(
                            bot.Slot,
                            out var window)
                        && Now < window.ExpiresAt
                        && window.Ratio < 1f
                        && _random.NextDouble() >= window.Ratio)
                    {
                        break;
                    }

                    var ratioCounts = CountBlindableEnemies(
                        bot,
                        lineup,
                        players);
                    var ratio = NadeSystemPolicy.GetFlashRatioThreshold(
                        ratioCounts.Blindable,
                        ratioCounts.Total);
                    if (ratio <= 0f)
                    {
                        break;
                    }

                    _flashRatioWindows[bot.Slot] =
                        new FlashRatioWindow(Now + 12f, ratio);
                }

                TryConditionalReplay(bot, lineup, players);
                break;
            }

            PruneFlashZones(bot);
        }
    }

    private void TryConditionalReplay(
        PlayerSnapshot bot,
        NadeLineup lineup,
        PlayerSnapshot[] players)
    {
        if (!PassesSituationalCheck(bot, lineup, players))
        {
            if (lineup.GrenadeType == "smoke")
            {
                var generation = _generation;
                _smokeCooldownBots.Add(bot.Slot);
                _modSharp.PushTimer(
                    () =>
                    {
                        if (_active && generation == _generation)
                        {
                            _smokeCooldownBots.Remove(bot.Slot);
                        }
                    },
                    1,
                    GameTimerFlags.StopOnMapEnd);
            }

            return;
        }

        TryReplay(bot, lineup, players);
    }

    private bool PassesSituationalCheck(
        PlayerSnapshot bot,
        NadeLineup lineup,
        PlayerSnapshot[] players)
    {
        var grenadeType = lineup.GrenadeType;
        if (grenadeType is "he" or "molotov")
        {
            if (FiredRecently(bot.Slot, 1f))
            {
                return false;
            }

            var nearbyEnemies = players
                .Where(
                    player => player.IsAlive
                        && player.Team != bot.Team
                        && player.Team is CStrikeTeam.TE or CStrikeTeam.CT
                        && player.Pawn is not null
                        && DistanceSquared(
                            player.Pawn.GetAbsOrigin(),
                            lineup.LandingPosition)
                        <= 200f * 200f)
                .ToArray();
            if (nearbyEnemies.Length == 0)
            {
                return false;
            }

            if (!nearbyEnemies.Any(enemy => HasInformationOn(enemy, bot)))
            {
                var probability =
                    NadeSystemPolicy.GetNoInformationProbability(
                        _mode,
                        grenadeType);
                if (_random.NextDouble() >= probability)
                {
                    RegisterProbabilityCooldown(lineup.Id);
                    return false;
                }
            }

            if (grenadeType == "molotov"
                && HasActiveSmokeNear(lineup.LandingPosition, 200f))
            {
                return false;
            }
        }

        if (grenadeType == "flash")
        {
            if (!PassesTeamAndScheduleCheck(bot, lineup))
            {
                return false;
            }

            var blindable = GetBlindableEnemies(bot, lineup, players);
            if (blindable.Length == 0)
            {
                return false;
            }

            if (!blindable.Any(enemy => HasInformationOn(enemy, bot)))
            {
                var probability =
                    NadeSystemPolicy.GetNoInformationProbability(
                        _mode,
                        grenadeType);
                if (_random.NextDouble() >= probability)
                {
                    RegisterProbabilityCooldown(lineup.Id);
                    return false;
                }
            }
        }

        if (grenadeType == "smoke")
        {
            if (!PassesTeamAndScheduleCheck(bot, lineup)
                || HasActiveSmokeNear(lineup.LandingPosition, 250f))
            {
                return false;
            }

            if (_mode is BotNadeMode.Less or BotNadeMode.Normal
                && _freezeEndTime > 0f
                && Now - _freezeEndTime < 5f
                && _earlySmokeCount.GetValueOrDefault(bot.Team) >= 1)
            {
                return false;
            }

            if (!HasEnemyNearLanding(bot, lineup, players, 2200f)
                || (_bombPlanted
                    && !HasEnemyNearLanding(
                        bot,
                        lineup,
                        players,
                        1000f)))
            {
                return false;
            }

            var alive = players.Where(
                    player => player.IsAlive
                        && player.Team is CStrikeTeam.TE or CStrikeTeam.CT)
                .ToArray();
            var totalFriends = alive.Count(player => player.Team == bot.Team);
            var totalEnemies = alive.Length - totalFriends;
            if (totalFriends == 0 || totalEnemies == 0 || bot.Pawn is null)
            {
                return false;
            }

            var botPosition = bot.Pawn.GetAbsOrigin();
            var nearbyFriends = 0;
            var nearbyEnemies = 0;
            foreach (var player in alive)
            {
                if (player.Pawn is null
                    || DistanceSquared(
                        botPosition,
                        player.Pawn.GetAbsOrigin())
                    > 800f * 800f)
                {
                    continue;
                }

                if (player.Team == bot.Team)
                {
                    nearbyFriends++;
                }
                else
                {
                    nearbyEnemies++;
                }
            }

            var threshold = ((float)nearbyFriends / totalFriends * 0.5f)
                + ((float)nearbyEnemies / totalEnemies * 0.5f);
            if (threshold < 1f && _random.NextDouble() >= threshold)
            {
                return false;
            }
        }

        return true;
    }

    private void TryReplay(
        PlayerSnapshot bot,
        NadeLineup lineup,
        PlayerSnapshot[] players)
    {
        if (_mode == BotNadeMode.Off
            || bot.Pawn is null
            || ReadBool(
                bot.Controller.GetAbsPtr(),
                _hasBeenControlledOffset))
        {
            return;
        }

        var grenadeType = lineup.GrenadeType;
        var flashLimit = GetConVarInt(
            "ammo_grenade_limit_flashbang",
            2);
        if (_mode == BotNadeMode.Less
            && !NadeSystemPolicy.LessModeAllows(
                _botRoundCounts.GetValueOrDefault(bot.Slot),
                grenadeType,
                flashLimit))
        {
            return;
        }

        if (_mode == BotNadeMode.Normal)
        {
            var teamCount = _teamRoundCounts.GetValueOrDefault((int)bot.Team);
            var teamSize = Math.Max(
                1,
                players.Count(
                    player => player.IsManagedBot
                        && player.Team == bot.Team));
            if (grenadeType is "flash" or "he" or "molotov"
                && teamCount.Flash + teamCount.HE + teamCount.Molotov
                >= 3 * teamSize)
            {
                return;
            }

            if (grenadeType == "flash")
            {
                if (teamCount.Flash >= flashLimit * teamSize)
                {
                    return;
                }
            }
            else
            {
                var used = grenadeType switch
                {
                    "smoke" => teamCount.Smoke,
                    "he" => teamCount.HE,
                    "molotov" => teamCount.Molotov,
                    _ => int.MaxValue,
                };
                if (used >= teamSize)
                {
                    return;
                }
            }
        }

        if (!TryCharge(bot, grenadeType))
        {
            return;
        }

        _replayBots.Add(bot.Slot);
        RegisterCooldown(lineup.Id, grenadeType);
        IncrementTeamCount(bot.Team, grenadeType);
        if (_mode == BotNadeMode.Less)
        {
            IncrementBotCount(bot.Slot, grenadeType);
        }

        if (_mode is BotNadeMode.Less or BotNadeMode.Normal
            && grenadeType == "smoke"
            && _freezeEndTime > 0f
            && Now - _freezeEndTime < 5f)
        {
            _earlySmokeCount[bot.Team] =
                _earlySmokeCount.GetValueOrDefault(bot.Team) + 1;
        }

        ScheduleSpawn(bot, lineup, countReplay: true);
        var generation = _generation;
        _modSharp.PushTimer(
            () =>
            {
                if (_active && generation == _generation)
                {
                    _replayBots.Remove(bot.Slot);
                }
            },
            1,
            GameTimerFlags.StopOnMapEnd);
    }

    private bool TryCharge(PlayerSnapshot bot, string grenadeType)
    {
        var money = bot.Controller.GetInGameMoneyService();
        var costs = bot.Team == CStrikeTeam.CT ? CostCT : CostT;
        if (money is null
            || !costs.TryGetValue(grenadeType, out var cost)
            || money.Account < cost)
        {
            return false;
        }

        var alreadySpent = _roundSpend.GetValueOrDefault(bot.Slot);
        var cap = NadeSystemPolicy.GetRoundSpendCap(
            IsPistolRound(),
            _poorBots.Contains(bot.Slot),
            bot.Team == CStrikeTeam.CT);
        if (alreadySpent < cap)
        {
            money.Account -= cost;
            _roundSpend[bot.Slot] = alreadySpent + cost;
        }

        return true;
    }

    private void ScheduleSpawn(
        PlayerSnapshot bot,
        NadeLineup lineup,
        bool countReplay)
    {
        ScheduleSpawn(
            bot.Slot,
            bot.Team,
            lineup.GrenadeType,
            ToVector(lineup.ProjectilePosition),
            ToVector(lineup.ProjectileVelocity),
            lineup.Id,
            countReplay);
    }

    private void ScheduleSpawn(
        int slot,
        CStrikeTeam team,
        string grenadeType,
        Vector origin,
        Vector velocity,
        string source,
        bool countReplay)
    {
        var generation = _generation;
        _modSharp.InvokeFrameAction(
            () =>
            {
                if (!_active
                    || generation != _generation
                    || FindManagedBot(slot) is not { Pawn: not null } bot)
                {
                    return;
                }

                var projectile = _factory.Spawn(
                    bot.Pawn,
                    team,
                    grenadeType,
                    origin,
                    velocity);
                if (projectile is null)
                {
                    return;
                }

                if (grenadeType == "flash")
                {
                    GrantTeamFlashImmunity(team);
                }
                else if (grenadeType == "decoy")
                {
                    StartDecoyLoop(slot, team, projectile);
                }

                if (countReplay)
                {
                    Interlocked.Increment(ref _replays);
                }

                _logger.LogInformation(
                    "NadeSystem replayed {Type} from {Source} for bot slot {Slot} at ({X:F0},{Y:F0},{Z:F0}), velocity ({VX:F1},{VY:F1},{VZ:F1}).",
                    grenadeType,
                    source.Length > 8 ? source[..8] : source,
                    slot,
                    origin.X,
                    origin.Y,
                    origin.Z,
                    velocity.X,
                    velocity.Y,
                    velocity.Z);
            });
    }

    private void HandleWeaponFire(IGameEvent gameEvent)
    {
        var controller = gameEvent.GetPlayerController("userid");
        if (controller is null)
        {
            return;
        }

        _lastFireTime[controller.PlayerSlot.AsPrimitive()] = Now;
        RecordSoundPoint(controller);
    }

    private void RecordSoundPoint(IPlayerController? controller)
        => RecordSoundPoint(controller, SnapshotPlayers());

    private void RecordSoundPoint(
        IPlayerController? controller,
        PlayerSnapshot[] players)
    {
        if (controller is null
            || GetActiveLivePawn(controller) is not { } pawn)
        {
            return;
        }

        var origin = pawn.GetAbsOrigin();
        var team = controller.Team;
        if (!players.Any(
                enemy => enemy.IsAlive
                    && enemy.Team != team
                    && enemy.Team is CStrikeTeam.TE or CStrikeTeam.CT
                    && enemy.Pawn is not null
                    && DistanceSquared(
                        enemy.Pawn.GetAbsOrigin(),
                        origin)
                    <= SoundHearRadiusSquared))
        {
            return;
        }

        var slot = controller.PlayerSlot.AsPrimitive();
        if (!_soundPoints.TryGetValue(slot, out var trail))
        {
            trail = [];
            _soundPoints[slot] = trail;
        }

        if (trail.Count > 0
            && DistanceSquared(trail[^1], origin) < 1f)
        {
            return;
        }

        trail.Add(new SoundPoint(origin.X, origin.Y, origin.Z));
    }

    private void UpdateSoundTrails(
        PlayerSnapshot[] players,
        bool recordFootsteps)
    {
        foreach (var player in players)
        {
            if (!player.IsAlive || player.Pawn is null)
            {
                _soundPoints.Remove(player.Slot);
                continue;
            }

            var origin = player.Pawn.GetAbsOrigin();
            if (_soundPoints.TryGetValue(player.Slot, out var trail))
            {
                trail.RemoveAll(
                    point => DistanceSquared(point, origin)
                        > SoundInfoRadiusSquared);
            }

            if (!recordFootsteps)
            {
                continue;
            }

            var velocity = player.Pawn.GetAbsVelocity();
            if (velocity.X * velocity.X + velocity.Y * velocity.Y
                > FootstepSpeedSquared)
            {
                RecordSoundPoint(player.Controller, players);
            }
        }
    }

    private void RemoveSoundTrail(IPlayerController? controller)
    {
        if (controller is not null)
        {
            _soundPoints.Remove(controller.PlayerSlot.AsPrimitive());
        }
    }

    private bool HasInformationOn(
        PlayerSnapshot observer,
        PlayerSnapshot target)
        => _soundPoints.TryGetValue(target.Slot, out var trail)
            && trail.Count > 0
            || EnemySeesTarget(observer, target);

    private bool EnemySeesTarget(
        PlayerSnapshot observer,
        PlayerSnapshot target)
    {
        if (observer.Pawn is null || target.Pawn is null)
        {
            return false;
        }

        try
        {
            var slot = observer.Slot;
            if (slot is >= 0 and < 64)
            {
                var maskPointer = target.Pawn.GetAbsPtr()
                    + _spottedStateOffset
                    + _spottedMaskOffset;
                var word = slot / 32;
                var bit = slot % 32;
                if ((ReadUInt(maskPointer, word * sizeof(uint))
                        & (1u << bit))
                    != 0)
                {
                    return true;
                }
            }
        }
        catch
        {
            // Fall through to the upstream geometric fallback.
        }

        return GeometricVision(observer.Pawn, target.Pawn);
    }

    private bool GeometricVision(IPlayerPawn observer, IPlayerPawn target)
    {
        var eye = observer.GetEyePosition();
        var targetEye = target.GetEyePosition();
        if (!IsWithinFlashFov(
                observer.GetEyeAngles(),
                eye,
                targetEye))
        {
            return false;
        }

        return HasWorldLineOfSight(eye, targetEye);
    }

    private PlayerSnapshot[] GetBlindableEnemies(
        PlayerSnapshot bot,
        NadeLineup lineup,
        PlayerSnapshot[] players)
    {
        var landing = ToVector(lineup.LandingPosition);
        return players.Where(
                enemy => enemy.IsAlive
                    && enemy.Team != bot.Team
                    && enemy.Team is CStrikeTeam.TE or CStrikeTeam.CT
                    && enemy.Pawn is not null
                    && IsWithinFlashFov(
                        enemy.Pawn.GetEyeAngles(),
                        enemy.Pawn.GetEyePosition(),
                        landing)
                    && HasWorldLineOfSight(
                        landing,
                        enemy.Pawn.GetEyePosition()))
            .ToArray();
    }

    private (int Blindable, int Total) CountBlindableEnemies(
        PlayerSnapshot bot,
        NadeLineup lineup,
        PlayerSnapshot[] players)
    {
        var total = players.Count(
            enemy => enemy.IsAlive
                && enemy.Team != bot.Team
                && enemy.Team is CStrikeTeam.TE or CStrikeTeam.CT);
        return (GetBlindableEnemies(bot, lineup, players).Length, total);
    }

    private bool HasWorldLineOfSight(Vector start, Vector end)
    {
        try
        {
            Interlocked.Increment(ref _rayTraces);
            var trace = _traces.TraceLineNoPlayers(
                start,
                end,
                UsefulInteractionLayers.BrushOnly,
                CollisionGroupType.Default,
                TraceQueryFlag.Static);
            return !trace.StartInSolid && trace.Fraction >= 0.99f;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsWithinFlashFov(
        Vector eyeAngles,
        Vector eye,
        Vector target)
    {
        var dx = target.X - eye.X;
        var dy = target.Y - eye.Y;
        var dz = target.Z - eye.Z;
        if (dx * dx + dy * dy + dz * dz > 1300f * 1300f)
        {
            return false;
        }

        var yawRadians = eyeAngles.Y * MathF.PI / 180f;
        var pitchRadians = -eyeAngles.X * MathF.PI / 180f;
        var forwardX = MathF.Cos(pitchRadians) * MathF.Cos(yawRadians);
        var forwardY = MathF.Cos(pitchRadians) * MathF.Sin(yawRadians);
        var forwardZ = MathF.Sin(pitchRadians);
        var targetYaw = MathF.Atan2(dy, dx);
        var eyeYaw = MathF.Atan2(forwardY, forwardX);
        var deltaYaw = MathF.Abs(
            MathF.Atan2(
                MathF.Sin(targetYaw - eyeYaw),
                MathF.Cos(targetYaw - eyeYaw)));
        var targetPitch = MathF.Atan2(dz, MathF.Sqrt(dx * dx + dy * dy));
        var eyePitch = MathF.Atan2(
            forwardZ,
            MathF.Sqrt(forwardX * forwardX + forwardY * forwardY));
        return deltaYaw <= 0.927f
            && MathF.Abs(targetPitch - eyePitch) <= MathF.PI / 4f;
    }

    private bool PassesTeamAndScheduleCheck(
        PlayerSnapshot bot,
        NadeLineup lineup)
    {
        if (lineup.TeamTag.Length == 0)
        {
            return true;
        }

        var teamTag = bot.Team == CStrikeTeam.CT ? "CT" : "T";
        if (!lineup.TeamTag.Equals(teamTag, StringComparison.Ordinal))
        {
            return false;
        }

        if (!ThrowSchedule.TryGetValue(
                $"{_mapName}_{lineup.TeamTag}",
                out var maximumSeconds))
        {
            return true;
        }

        return _freezeEndTime > 0f
            && Now - _freezeEndTime <= maximumSeconds;
    }

    private bool HasEnemyNearLanding(
        PlayerSnapshot bot,
        NadeLineup lineup,
        PlayerSnapshot[] players,
        float radius)
        => players.Any(
            enemy => enemy.IsAlive
                && enemy.Team != bot.Team
                && enemy.Team is CStrikeTeam.TE or CStrikeTeam.CT
                && enemy.Pawn is not null
                && DistanceSquared(
                    enemy.Pawn.GetAbsOrigin(),
                    lineup.LandingPosition)
                <= radius * radius);

    private bool HasActiveSmokeNear(NadeVector landing, float radius)
    {
        if (_catalog is null)
        {
            return false;
        }

        var now = Now;
        var radiusSquared = radius * radius;
        foreach (var (id, expiry) in _cooldowns)
        {
            if (expiry <= now
                || _catalog.Find(id) is not { GrenadeType: "smoke" } smoke
                || DistanceSquared(landing, smoke.LandingPosition)
                >= radiusSquared)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private bool FiredRecently(int slot, float seconds)
        => _lastFireTime.TryGetValue(slot, out var last)
            && Now - last < seconds;

    private void HandleBombBeginDefuse(IGameEvent gameEvent)
    {
        var controller = gameEvent.GetPlayerController("userid");
        RecordSoundPoint(controller);
        if (!TryGetManagedBot(controller, out var bot)
            || bot.Pawn is null)
        {
            return;
        }

        var position = bot.Pawn.GetAbsOrigin();
        var spawn = new Vector(position.X, position.Y, position.Z + 5f);
        if (!_defuseSmokeUsed
            && (bot.Pawn.GetItemService()?.HasDefuser == true
                || _random.NextDouble() < 0.33))
        {
            _defuseSmokeUsed = true;
            TrySpawnInstant(bot, "smoke", spawn, new Vector());
        }

        if (!_defuseFlashUsed && _random.NextDouble() < 0.20)
        {
            _defuseFlashUsed = true;
            _flashImmunity[bot.Slot] = Now + 2f;
            TrySpawnInstant(
                bot,
                "flash",
                spawn,
                new Vector(0f, 0f, -800f));
        }
    }

    private void HandleBombBeginPlant(IGameEvent gameEvent)
    {
        var controller = gameEvent.GetPlayerController("userid");
        RecordSoundPoint(controller);
        if (_plantSmokeUsed
            || _random.NextDouble() >= 0.33
            || !TryGetManagedBot(controller, out var bot)
            || bot.Pawn is null)
        {
            return;
        }

        var position = bot.Pawn.GetAbsOrigin();
        _plantSmokeUsed = true;
        TrySpawnInstant(
            bot,
            "smoke",
            new Vector(position.X, position.Y, position.Z + 5f),
            new Vector());
    }

    private void TrySpawnInstant(
        PlayerSnapshot bot,
        string grenadeType,
        Vector spawn,
        Vector velocity)
    {
        if (_mode == BotNadeMode.Off
            || bot.Pawn is null
            || ReadBool(
                bot.Controller.GetAbsPtr(),
                _hasBeenControlledOffset)
            || !SnapshotPlayers().Any(
                player => player.IsAlive
                    && player.Team != bot.Team
                    && player.Team is CStrikeTeam.TE or CStrikeTeam.CT)
            || !TryCharge(bot, grenadeType))
        {
            return;
        }

        ScheduleSpawn(
            bot.Slot,
            bot.Team,
            grenadeType,
            spawn,
            velocity,
            $"instant-{grenadeType}",
            countReplay: true);
    }

    private void HandlePlayerHurt(IEventPlayerHurt hurt)
    {
        HandleMolotovEscape(hurt);
        HandleRetaliation(hurt);
    }

    private void HandleMolotovEscape(IEventPlayerHurt hurt)
    {
        if (_mode == BotNadeMode.Off
            || !TryGetManagedBot(hurt.VictimController, out var victim)
            || victim.Pawn is null)
        {
            return;
        }

        var weapon = hurt.Weapon ?? string.Empty;
        var molotovDamage = weapon.Contains(
                "inferno",
                StringComparison.OrdinalIgnoreCase)
            || weapon.Contains(
                "molotov",
                StringComparison.OrdinalIgnoreCase)
            || weapon.Contains(
                "incgrenade",
                StringComparison.OrdinalIgnoreCase);
        if (!molotovDamage)
        {
            _molotovDamageStart.Remove(victim.Slot);
            return;
        }

        var now = Now;
        if (_molotovEscapeCooldown.TryGetValue(
                victim.Team,
                out var cooldown)
            && now < cooldown)
        {
            return;
        }

        if (!_molotovDamageStart.TryGetValue(
                victim.Slot,
                out var start))
        {
            _molotovDamageStart[victim.Slot] = now;
            return;
        }

        if (now - start < 0.3f)
        {
            return;
        }

        _molotovDamageStart.Remove(victim.Slot);
        _molotovEscapeCooldown[victim.Team] = now + 20f;
        var position = victim.Pawn.GetAbsOrigin();
        TrySpawnInstant(
            victim,
            "smoke",
            new Vector(position.X, position.Y, position.Z + 5f),
            new Vector());
    }

    private void HandleRetaliation(IEventPlayerHurt hurt)
    {
        if (_mode == BotNadeMode.Off
            || _roundOver
            || _catalog is null
            || !TryGetManagedBot(hurt.VictimController, out var victim)
            || victim.Pawn is null
            || FiredRecently(victim.Slot, 1f)
            || hurt.KillerController is not { IsValidEntity: true } attacker
            || BotIdentityRegistry.IsBot(
                attacker.IsFakeClient,
                attacker.GetGameClient()?.Slot.AsPrimitive() ?? -1)
            || attacker.Team == victim.Team
            || GetActiveLivePawn(attacker) is not { } attackerPawn)
        {
            return;
        }

        var weapon = hurt.Weapon ?? string.Empty;
        if (!weapon.Contains(
                "hegrenade",
                StringComparison.OrdinalIgnoreCase)
            && !weapon.Contains(
                "molotov",
                StringComparison.OrdinalIgnoreCase)
            && !weapon.Contains(
                "incgrenade",
                StringComparison.OrdinalIgnoreCase)
            && !weapon.Contains(
                "inferno",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_mode is BotNadeMode.Less
            or BotNadeMode.Normal
            or BotNadeMode.More
            && _retaliationCooldown.TryGetValue(
                victim.Team,
                out var cooldown)
            && Now < cooldown)
        {
            return;
        }

        var victimPosition = victim.Pawn.GetAbsOrigin();
        var players = SnapshotPlayers();
        var limit = _mode is BotNadeMode.Less
            or BotNadeMode.Normal
            or BotNadeMode.More
            ? Math.Max(
                1,
                players.Count(
                    player => player.IsAlive
                        && player.Team == victim.Team
                        && player.Pawn is not null
                        && DistanceSquared(
                            victimPosition,
                            player.Pawn.GetAbsOrigin())
                        <= 800f * 800f))
            : int.MaxValue;
        var attackerPosition = attackerPawn.GetAbsOrigin();
        var candidates = _catalog.Lineups
            .Where(
                lineup => lineup.GrenadeType is "he" or "molotov"
                    && DistanceSquared(
                        attackerPosition,
                        lineup.LandingPosition)
                    <= 200f * 200f
                    && !IsOnCooldown(lineup.Id))
            .OrderByDescending(
                lineup => NadeSystemPolicy.FacesThrowDirection(
                    victim.Pawn.GetEyeAngles().Y,
                    lineup.ProjectileVelocity.X,
                    lineup.ProjectileVelocity.Y))
            .ThenBy(
                lineup => DistanceSquared(
                    victimPosition,
                    lineup.ProjectilePosition))
            .ToArray();

        var spawned = 0;
        foreach (var lineup in candidates)
        {
            if (spawned >= limit)
            {
                break;
            }

            if (_mode == BotNadeMode.Less
                && !NadeSystemPolicy.LessModeAllows(
                    _botRoundCounts.GetValueOrDefault(victim.Slot),
                    lineup.GrenadeType,
                    GetConVarInt(
                        "ammo_grenade_limit_flashbang",
                        2)))
            {
                continue;
            }

            if (!TryCharge(victim, lineup.GrenadeType))
            {
                continue;
            }

            RegisterCooldown(lineup.Id, lineup.GrenadeType);
            ScheduleSpawn(victim, lineup, countReplay: true);
            if (_mode == BotNadeMode.Normal)
            {
                IncrementTeamCount(victim.Team, lineup.GrenadeType);
            }
            else if (_mode == BotNadeMode.Less)
            {
                IncrementBotCount(victim.Slot, lineup.GrenadeType);
            }

            spawned++;
        }

        if (spawned > 0
            && _mode is BotNadeMode.Less
                or BotNadeMode.Normal
                or BotNadeMode.More)
        {
            _retaliationCooldown[victim.Team] = Now + 7f;
        }
    }

    private void HandlePlayerBlind(IGameEvent gameEvent)
    {
        var controller = gameEvent.GetPlayerController("userid");
        if (!TryGetManagedBot(controller, out var victim)
            || victim.Pawn is null
            || !_flashImmunity.TryGetValue(
                victim.Slot,
                out var immunity)
            || Now > immunity)
        {
            return;
        }

        if (gameEvent.Editable)
        {
            gameEvent.SetFloat("blind_duration", 0f);
        }

        victim.Pawn.FlashDuration = 0f;
        victim.Pawn.FlashMaxAlpha = 0f;
        WriteFloat(
            victim.Pawn.GetAbsPtr(),
            _blindStartOffset,
            0f);
        WriteFloat(
            victim.Pawn.GetAbsPtr(),
            _blindUntilOffset,
            0f);
    }

    private void GrantTeamFlashImmunity(CStrikeTeam team)
    {
        var until = Now + 2f;
        foreach (var player in SnapshotPlayers())
        {
            if (player.IsManagedBot && player.Team == team)
            {
                _flashImmunity[player.Slot] = until;
            }
        }
    }

    private void StartDecoyLoop(
        int botSlot,
        CStrikeTeam team,
        IBaseGrenadeProjectile projectile)
    {
        var generation = _generation;
        var projectileIndex = projectile.Index;
        _modSharp.PushTimer(
            () =>
            {
                if (!_active
                    || generation != _generation
                    || _entities.FindEntityByIndex<IBaseGrenadeProjectile>(
                        projectileIndex) is not
                    {
                        IsValidEntity: true,
                    } current)
                {
                    return;
                }

                var position = current.GetAbsOrigin();
                var velocity = current.GetAbsVelocity();
                var speedSquared = velocity.X * velocity.X
                    + velocity.Y * velocity.Y
                    + velocity.Z * velocity.Z;
                current.Kill();
                if (speedSquared < 25f
                    || FindManagedBot(botSlot) is not { Pawn: not null } bot)
                {
                    return;
                }

                var next = _factory.Spawn(
                    bot.Pawn,
                    team,
                    "decoy",
                    position,
                    velocity);
                if (next is not null)
                {
                    StartDecoyLoop(botSlot, team, next);
                }
            },
            1,
            GameTimerFlags.StopOnMapEnd);
    }

    private PlayerSnapshot[] SnapshotPlayers()
        => _clients.GetGameClients(inGame: true)
            .Where(client => client is { IsValid: true, IsHltv: false })
            .Select(
                client =>
                {
                    var controller = client.GetPlayerController();
                    var slot = client.Slot.AsPrimitive();
                    var pawn = controller is null
                        ? null
                        : GetActiveLivePawn(controller);
                    return controller is null
                        ? default
                        : new PlayerSnapshot(
                            slot,
                            controller,
                            pawn,
                            controller.Team,
                            pawn is { IsAlive: true } && pawn.Health > 0,
                            BotIdentityRegistry.IsBot(
                                client.IsFakeClient,
                                slot));
                })
            .Where(player => player.Controller is not null)
            .ToArray();

    private PlayerSnapshot? FindManagedBot(int slot)
        => SnapshotPlayers().FirstOrDefault(
            player => player.Slot == slot
                && player.IsManagedBot
                && player.IsAlive);

    private bool TryGetManagedBot(
        IPlayerController? controller,
        out PlayerSnapshot bot)
    {
        bot = default;
        if (controller is not { IsValidEntity: true })
        {
            return false;
        }

        var slot = controller.PlayerSlot.AsPrimitive();
        var client = controller.GetGameClient();
        var pawn = GetActiveLivePawn(controller);
        if (client is null
            || pawn is null
            || !BotIdentityRegistry.IsBot(
                client.IsFakeClient,
                slot)
            || ReadBool(
                controller.GetAbsPtr(),
                _hasBeenControlledOffset))
        {
            return false;
        }

        bot = new PlayerSnapshot(
            slot,
            controller,
            pawn,
            controller.Team,
            IsAlive: true,
            IsManagedBot: true);
        return true;
    }

    private static IPlayerPawn? GetActiveLivePawn(
        IPlayerController controller)
    {
        var pawn = controller.GetPawn()?.AsPlayerPawn();
        return pawn is { IsValidEntity: true, IsAlive: true }
            && pawn.Health > 0
            ? pawn
            : null;
    }

    private bool EnterFlashZone(int slot, string lineupId)
    {
        if (!_flashZones.TryGetValue(slot, out var zones))
        {
            zones = new HashSet<string>(StringComparer.Ordinal);
            _flashZones[slot] = zones;
        }

        return zones.Add(lineupId);
    }

    private void PruneFlashZones(PlayerSnapshot bot)
    {
        if (_catalog is null
            || bot.Pawn is null
            || !_flashZones.TryGetValue(bot.Slot, out var zones))
        {
            return;
        }

        var position = bot.Pawn.GetAbsOrigin();
        zones.RemoveWhere(
            id => _catalog.Find(id) is not { GrenadeType: "flash" } lineup
                || !IsInsideZone(position, lineup));
        if (zones.Count == 0)
        {
            _flashZones.Remove(bot.Slot);
        }
    }

    private static bool IsInsideZone(Vector position, NadeLineup lineup)
    {
        var radius = lineup.GrenadeType == "decoy"
            ? 200f
            : lineup.ZoneRadius;
        var dx = position.X - lineup.ProjectilePosition.X;
        var dy = position.Y - lineup.ProjectilePosition.Y;
        var dz = position.Z + EyeHeight - lineup.ProjectilePosition.Z;
        return dx * dx + dy * dy <= radius * radius
            && MathF.Abs(dz) <= 85f;
    }

    private void IncrementTeamCount(
        CStrikeTeam team,
        string grenadeType)
        => _teamRoundCounts[(int)team] = _teamRoundCounts
            .GetValueOrDefault((int)team)
            .Increment(grenadeType);

    private void IncrementBotCount(int slot, string grenadeType)
        => _botRoundCounts[slot] = _botRoundCounts
            .GetValueOrDefault(slot)
            .Increment(grenadeType);

    private bool IsOnCooldown(string id)
        => _cooldowns.TryGetValue(id, out var expiry)
            && expiry > Now;

    private void RegisterCooldown(string id, string grenadeType)
        => _cooldowns[id] = Now
            + CooldownSeconds.GetValueOrDefault(grenadeType, 10f);

    private bool IsOnProbabilityCooldown(string id)
        => _probabilityCooldowns.TryGetValue(id, out var expiry)
            && expiry > Now;

    private void RegisterProbabilityCooldown(string id)
        => _probabilityCooldowns[id] = Now + 3f;

    private void PruneTimedState()
    {
        // Library disconnect can deactivate modules after ModSharp has already
        // made Globals unavailable. ClearState still empties every collection,
        // so an expiry scan is useful only while the runtime is active.
        if (!_active)
        {
            return;
        }

        var now = Now;
        foreach (var key in _cooldowns
                     .Where(pair => pair.Value <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _cooldowns.Remove(key);
        }

        foreach (var key in _probabilityCooldowns
                     .Where(pair => pair.Value <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _probabilityCooldowns.Remove(key);
        }

        foreach (var key in _flashImmunity
                     .Where(pair => pair.Value <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _flashImmunity.Remove(key);
        }

        foreach (var key in _flashRatioWindows
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _flashRatioWindows.Remove(key);
        }
    }

    private void SnapshotPoorBots()
    {
        _poorBots.Clear();
        if (IsPistolRound())
        {
            return;
        }

        foreach (var bot in SnapshotPlayers())
        {
            if (bot.IsManagedBot
                && bot.Controller.GetInGameMoneyService()?.Account < 2800)
            {
                _poorBots.Add(bot.Slot);
            }
        }
    }

    private bool IsPistolRound()
    {
        try
        {
            var played = _modSharp.GetGameRules().TotalRoundsPlayed;
            var maxRounds = GetConVarInt("mp_maxrounds", 24);
            if (maxRounds <= 0)
            {
                maxRounds = 24;
            }

            return played == 0 || played == maxRounds / 2;
        }
        catch
        {
            return false;
        }
    }

    private int GetConVarInt(string name, int fallback)
    {
        try
        {
            return (_conVars.FindConVar(name)
                    ?? _conVars.FindConVar(name, useIterator: true))
                ?.GetInt32()
                ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private void ResetRoundState()
    {
        _roundOver = false;
        _bombPlanted = false;
        _freezeEndTime = 0f;
        _defuseSmokeUsed = false;
        _defuseFlashUsed = false;
        _plantSmokeUsed = false;
        _teamRoundCounts.Clear();
        _botRoundCounts.Clear();
        _cooldowns.Clear();
        _replayBots.Clear();
        _smokeCooldownBots.Clear();
        _roundSpend.Clear();
        _molotovDamageStart.Clear();
        _molotovEscapeCooldown.Clear();
        _retaliationCooldown.Clear();
        _earlySmokeCount.Clear();
        _flashZones.Clear();
        _flashRatioWindows.Clear();
        _flashImmunity.Clear();
        _soundPoints.Clear();
        _lastFireTime.Clear();
        PruneTimedState();
    }

    private void ClearState()
    {
        _catalog = null;
        _mapName = string.Empty;
        _lineupDirectory = string.Empty;
        ResetRoundState();
        _poorBots.Clear();
        _probabilityCooldowns.Clear();
    }

    private void RemoveFrameHook()
    {
        if (!_frameHookInstalled)
        {
            return;
        }

        _modSharp.RemoveGameFrameHook(OnGameFramePre, null);
        _frameHookInstalled = false;
    }

    private int Offset(string className, string fieldName)
    {
        var offset = _schema.GetNetVarOffset(className, fieldName);
        if (offset <= 0)
        {
            throw new InvalidDataException(
                $"Schema field {className}::{fieldName} resolved to invalid offset {offset}.");
        }

        return offset;
    }

    private int OffsetFromAny(
        string fieldName,
        params string[] classNames)
    {
        var failures = new List<string>();
        foreach (var className in classNames)
        {
            try
            {
                var offset = Offset(className, fieldName);
                _logger.LogInformation(
                    "NadeSystem resolved {Field} on schema class {Class} at 0x{Offset:X}.",
                    fieldName,
                    className,
                    offset);
                return offset;
            }
            catch (ArgumentException exception)
            {
                failures.Add(exception.Message);
            }
        }

        throw new InvalidDataException(
            $"Schema field {fieldName} was not found on candidate classes: {string.Join("; ", failures)}.");
    }

    private float Now => _modSharp.GetGlobals().CurTime;

    private static Vector ToVector(NadeVector source)
        => new(source.X, source.Y, source.Z);

    private static float DistanceSquared(Vector left, Vector right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        var dz = left.Z - right.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static float DistanceSquared(Vector left, NadeVector right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        var dz = left.Z - right.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static float DistanceSquared(
        NadeVector left,
        NadeVector right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        var dz = left.Z - right.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static float DistanceSquared(
        SoundPoint left,
        Vector right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        var dz = left.Z - right.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static unsafe bool ReadBool(nint pointer, int offset)
        => *(byte*)(pointer + offset) != 0;

    private static unsafe uint ReadUInt(nint pointer, int offset)
        => *(uint*)(pointer + offset);

    private static unsafe void WriteFloat(
        nint pointer,
        int offset,
        float value)
        => *(float*)(pointer + offset) = value;

    private readonly record struct PlayerSnapshot(
        int Slot,
        IPlayerController Controller,
        IPlayerPawn? Pawn,
        CStrikeTeam Team,
        bool IsAlive,
        bool IsManagedBot);

    private readonly record struct SoundPoint(float X, float Y, float Z);
    private readonly record struct FlashRatioWindow(
        float ExpiresAt,
        float Ratio);
}
