using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SharpGameModes.Contracts;
using SharpGameModes.Domain;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameEvents;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace SharpGameModes.TeamDeathmatch;

public sealed class TeamDeathmatchModule : IModSharpModule, IGameListener, IClientListener, IEventListener
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly string[] BuyCommands = ["buy", "autobuy", "rebuy", "buymenu"];
    private static readonly string[] HelpCommands = ["gun", "guns", "wp", "枪"];

    private readonly ISharedSystem _shared;
    private readonly IModSharp _modSharp;
    private readonly IClientManager _clients;
    private readonly IEntityManager _entities;
    private readonly IEventManager _events;
    private readonly ILogger<TeamDeathmatchModule> _logger;
    private readonly string _configPath;
    private readonly Dictionary<ulong, PlayerLoadout> _loadouts = [];
    private readonly Dictionary<ulong, int> _scheduledLoadoutGenerations = [];
    private readonly HashSet<ulong> _buyHelpPromptedThisRound = [];
    private readonly List<string> _registeredWeaponCommands = [];
    private TeamDeathmatchConfig _config = new();
    private TeamDeathmatchScore _score = new(100);
    private IModSharpModuleInterface<IModeContext>? _modeContext;
    private bool _matchEnding;
    private bool _matchEndObserved;
    private bool _roundObjectiveAnnounced;
    private bool _stopping;
    private int _lifecycleGeneration;
    private int _matchTimerGeneration;
    private int _nextLoadoutGeneration;

    public TeamDeathmatchModule(
        ISharedSystem sharedSystem,
        string dllPath,
        string sharpPath,
        Version version,
        IConfiguration coreConfiguration,
        bool hotReload)
    {
        _shared = sharedSystem;
        _modSharp = sharedSystem.GetModSharp();
        _clients = sharedSystem.GetClientManager();
        _entities = sharedSystem.GetEntityManager();
        _events = sharedSystem.GetEventManager();
        _logger = sharedSystem.GetLoggerFactory().CreateLogger<TeamDeathmatchModule>();
        _configPath = Path.Combine(sharpPath, "configs", "sharp-gamemodes", "tdm.jsonc");
    }

    public string DisplayName => "SharpGameModes Team Deathmatch";
    public string DisplayAuthor => "SharpGameModes Contributors";
    public int ListenerVersion => IGameListener.ApiVersion;
    public int ListenerPriority => 20;

    public bool Init()
    {
        try
        {
            _config = JsonSerializer.Deserialize<TeamDeathmatchConfig>(
                File.ReadAllText(_configPath),
                SerializerOptions) ?? throw new InvalidDataException("Team-deathmatch config is empty.");
            _config.Validate();
            _score = new TeamDeathmatchScore(_config.ScoreLimit);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or ArgumentException)
        {
            _logger.LogError(exception, "Failed to load team-deathmatch config from {Path}.", _configPath);
            return false;
        }

        _modSharp.InstallGameListener(this);
        _clients.InstallClientListener(this);
        _events.InstallEventListener(this);
        _events.HookEvent("player_spawn");
        _events.HookEvent("player_death");
        _events.HookEvent("player_team");
        _events.HookEvent("cs_win_panel_match");
        InstallCommands();
        return true;
    }

    public void OnAllModulesLoaded() => RefreshModeContext();

    public void OnLibraryConnected(string name)
    {
        if (name.Equals("SharpGameModes.Core", StringComparison.OrdinalIgnoreCase))
        {
            RefreshModeContext();
        }
    }

    public void OnLibraryDisconnect(string name)
    {
        if (name.Equals("SharpGameModes.Core", StringComparison.OrdinalIgnoreCase))
        {
            _modeContext = null;
        }
    }

    public void OnGameInit()
    {
        _lifecycleGeneration++;
        _score.Reset();
        _matchEnding = false;
        _matchEndObserved = false;
        _roundObjectiveAnnounced = false;
        _matchTimerGeneration++;
        _scheduledLoadoutGenerations.Clear();
        _buyHelpPromptedThisRound.Clear();
    }

    public void OnRoundRestart()
    {
        if (!IsActive())
        {
            return;
        }

        _score.Reset();
        _matchEnding = false;
        _matchEndObserved = false;
        _roundObjectiveAnnounced = false;
        _matchTimerGeneration++;
        _buyHelpPromptedThisRound.Clear();
        SetNativeTeamScores(0, 0);
        _modSharp.ServerCommand(
            $"mp_respawn_immunitytime {_config.RespawnImmunitySeconds.ToString("0.###", CultureInfo.InvariantCulture)}");
        ScheduleAllPlayerLoadouts(0.2, giveArmor: true);

        var generation = _lifecycleGeneration;
        _modSharp.PushTimer(
            () =>
            {
                if (!_stopping && generation == _lifecycleGeneration && IsActive())
                {
                    PrintBuyHelpToAll();
                }
            },
            1,
            GameTimerFlags.StopOnMapEnd);
    }

    public void OnRoundRestarted()
    {
        if (!IsActive() || IsWarmup() || _roundObjectiveAnnounced)
        {
            return;
        }

        _roundObjectiveAnnounced = true;
        var minutes = _config.MatchTimeLimitSeconds / 60;
        Broadcast(
            $"{_config.Prefix} 胜利条件：队伍先完成 {_config.ScoreLimit} 次有效击杀；"
            + $"{minutes:0.##} 分钟到时按比分结算。");

        var lifecycleGeneration = _lifecycleGeneration;
        var timerGeneration = _matchTimerGeneration;
        _modSharp.PushTimer(
            () =>
            {
                if (_stopping
                    || lifecycleGeneration != _lifecycleGeneration
                    || timerGeneration != _matchTimerGeneration
                    || !IsActive()
                    || _matchEnding)
                {
                    return;
                }

                EndMatchAtTimeLimit();
            },
            _config.MatchTimeLimitSeconds,
            GameTimerFlags.StopOnMapEnd);
    }

    public void OnClientPutInServer(IGameClient client)
    {
        if (!IsActive() || !IsHuman(client))
        {
            return;
        }

        ScheduleJoiningClientRespawn(client, 0.7, remainingAttempts: 8);
    }

    public void OnClientDisconnecting(IGameClient client, NetworkDisconnectionReason reason)
    {
        var steamId = client.SteamId.AsPrimitive();
        _scheduledLoadoutGenerations.Remove(steamId);
        _buyHelpPromptedThisRound.Remove(steamId);
    }

    public ECommandAction OnClientSayCommand(
        IGameClient client,
        bool teamOnly,
        bool isCommand,
        string commandName,
        string message)
    {
        if (!IsActive() || !IsHuman(client) || isCommand || string.IsNullOrWhiteSpace(message))
        {
            return ECommandAction.Skipped;
        }

        var text = message.Trim().Trim('"');
        if (text.Length < 2 || text[0] is not ('!' or '！' or '.'))
        {
            return ECommandAction.Skipped;
        }

        var alias = text[1..].Trim();
        if (HelpCommands.Contains(alias, StringComparer.OrdinalIgnoreCase))
        {
            PrintWeaponHelp(client);
            return ECommandAction.Handled;
        }

        if (!TdmWeaponCatalog.TryResolve(alias, out var weapon))
        {
            return ECommandAction.Skipped;
        }

        BuyWeapon(client, weapon);
        return ECommandAction.Handled;
    }

    public void FireGameEvent(IGameEvent gameEvent)
    {
        try
        {
            switch (gameEvent.Name)
            {
                case "player_spawn" when gameEvent is IEventPlayerSpawn spawn:
                    OnPlayerSpawn(spawn.Controller);
                    break;
                case "player_death" when gameEvent is IEventPlayerDeath death:
                    OnPlayerDeath(death);
                    break;
                case "player_team" when gameEvent is IEventPlayerTeam team:
                    OnPlayerTeam(team);
                    break;
                case "cs_win_panel_match":
                    if (IsActive())
                    {
                        _matchEndObserved = true;
                    }

                    break;
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to process team-deathmatch event {EventName}.", gameEvent.Name);
        }
    }

    public void Shutdown()
    {
        _stopping = true;
        _lifecycleGeneration++;
        RemoveCommands();
        _events.RemoveEventListener(this);
        _clients.RemoveClientListener(this);
        _modSharp.RemoveGameListener(this);
        _modeContext = null;
        _scheduledLoadoutGenerations.Clear();
        _buyHelpPromptedThisRound.Clear();
        _loadouts.Clear();
    }

    private void OnPlayerSpawn(IPlayerController? controller)
    {
        if (!IsActiveHuman(controller))
        {
            return;
        }

        if (controller.GetGameClient() is { } client)
        {
            PrintBuyHelpOnce(client);
        }

        SchedulePlayerLoadout(controller, 0.1, giveArmor: true);
    }

    private void OnPlayerTeam(IEventPlayerTeam gameEvent)
    {
        var controller = gameEvent.Controller;
        if (!IsActive() || _matchEnding || gameEvent.Disconnect || !IsActiveHuman(controller))
        {
            return;
        }

        var generation = _lifecycleGeneration;
        _modSharp.PushTimer(
            () =>
            {
                if (!_stopping && generation == _lifecycleGeneration)
                {
                    RespawnJoinedPlayer(controller);
                }
            },
            0.3,
            GameTimerFlags.StopOnMapEnd);
    }

    private void OnPlayerDeath(IEventPlayerDeath death)
    {
        if (!IsActive() || _matchEnding)
        {
            return;
        }

        var victim = death.VictimController;
        var killer = death.KillerController;
        if (!IsWarmup()
            && IsActiveHuman(killer)
            && IsActiveParticipant(victim)
            && !ReferenceEquals(killer, victim))
        {
            var update = _score.RegisterKill(ToDomainTeam(killer.Team), ToDomainTeam(victim.Team));
            if (update.Counted)
            {
                SetNativeTeamScores(
                    update.TerroristScoreBeforeRoundAward,
                    update.CounterTerroristScoreBeforeRoundAward);
                if (update.Winner is { } winner)
                {
                    EndMatch(winner);
                }
            }
        }

        if (!_matchEnding && IsActiveHuman(victim))
        {
            var generation = _lifecycleGeneration;
            _modSharp.PushTimer(
                () =>
                {
                    if (!_stopping && generation == _lifecycleGeneration)
                    {
                        RespawnPlayer(victim);
                    }
                },
                _config.RespawnDelaySeconds,
                GameTimerFlags.StopOnMapEnd);
        }
    }

    private void EndMatch(TeamAssignment winner, string? announcement = null)
    {
        if (_matchEnding)
        {
            return;
        }

        _matchEnding = true;
        var winnerName = winner == TeamAssignment.CounterTerrorist ? "CT" : "T";
        Broadcast(announcement ?? $"{_config.Prefix} {winnerName} 达到 {_config.ScoreLimit} 分，团队竞技结束。");
        _modSharp.ServerCommand("mp_ignore_round_win_conditions 0");

        var nativeTeam = winner == TeamAssignment.CounterTerrorist ? CStrikeTeam.CT : CStrikeTeam.TE;

        var reason = winner == TeamAssignment.CounterTerrorist
            ? RoundEndReason.CTsWin
            : RoundEndReason.TerroristsWin;
        var generation = _lifecycleGeneration;
        _modSharp.PushTimer(
            () =>
            {
                if (_stopping || generation != _lifecycleGeneration || !IsActive() || !_matchEnding)
                {
                    return;
                }

                if (!EliminateLosingTeam(nativeTeam))
                {
                    _modSharp.GetGameRules().TerminateRound(0.1f, reason);
                }
            },
            0.1,
            GameTimerFlags.StopOnMapEnd);
        _modSharp.PushTimer(
            () =>
            {
                if (!_stopping && generation == _lifecycleGeneration && IsActive() && _matchEnding && !_matchEndObserved)
                {
                    _logger.LogWarning("No match-end panel was observed after the TDM score limit; terminating the round fallback.");
                    _modSharp.GetGameRules().TerminateRound(0.1f, reason);
                }
            },
            _config.MatchEndFallbackDelaySeconds,
            GameTimerFlags.StopOnMapEnd);
    }

    private void EndMatchAtTimeLimit()
    {
        var terroristScore = _score.TerroristScore;
        var counterTerroristScore = _score.CounterTerroristScore;
        if (terroristScore == counterTerroristScore)
        {
            EndMatchAsDraw(terroristScore);
            return;
        }

        var winner = counterTerroristScore > terroristScore
            ? TeamAssignment.CounterTerrorist
            : TeamAssignment.Terrorist;
        SetNativeTeamScores(
            winner == TeamAssignment.Terrorist ? terroristScore - 1 : terroristScore,
            winner == TeamAssignment.CounterTerrorist ? counterTerroristScore - 1 : counterTerroristScore);
        var winnerName = winner == TeamAssignment.CounterTerrorist ? "CT" : "T";
        EndMatch(
            winner,
            $"{_config.Prefix} 时间到，{winnerName} 以 T {terroristScore} : {counterTerroristScore} CT 获胜。");
    }

    private void EndMatchAsDraw(int score)
    {
        if (_matchEnding)
        {
            return;
        }

        _matchEnding = true;
        Broadcast($"{_config.Prefix} 时间到，双方 {score} : {score}，本场平局。");
        _modSharp.ServerCommand("mp_ignore_round_win_conditions 0");
        var generation = _lifecycleGeneration;
        _modSharp.GetGameRules().TerminateRound(0.1f, RoundEndReason.RoundDraw);
        _modSharp.PushTimer(
            () =>
            {
                if (!_stopping
                    && generation == _lifecycleGeneration
                    && IsActive()
                    && _matchEnding
                    && !_matchEndObserved)
                {
                    _logger.LogWarning("No match-end panel was observed after the TDM time-limit draw; retrying.");
                    _modSharp.GetGameRules().TerminateRound(0.1f, RoundEndReason.RoundDraw);
                }
            },
            _config.MatchEndFallbackDelaySeconds,
            GameTimerFlags.StopOnMapEnd);
    }

    private bool EliminateLosingTeam(CStrikeTeam winner)
    {
        var loser = winner == CStrikeTeam.CT ? CStrikeTeam.TE : CStrikeTeam.CT;
        var eliminated = 0;
        foreach (var controller in GetHumanControllers().Where(player => player.Team == loser))
        {
            var pawn = controller.GetPlayerPawn();
            if (pawn is not { IsAlive: true })
            {
                continue;
            }

            pawn.Slay();
            eliminated++;
        }

        return eliminated > 0;
    }

    private void RespawnPlayer(IPlayerController controller)
    {
        if (!IsActiveHuman(controller) || _matchEnding)
        {
            return;
        }

        if (controller.GetPlayerPawn() is { IsAlive: true })
        {
            return;
        }

        controller.Respawn();
        SchedulePlayerLoadout(controller, 0.1, giveArmor: true);
    }

    private void RespawnJoinedPlayer(IPlayerController controller)
    {
        if (!IsActiveHuman(controller) || _matchEnding)
        {
            return;
        }

        if (controller.GetPlayerPawn() is not { IsAlive: true })
        {
            controller.Respawn();
        }

        SchedulePlayerLoadout(controller, 0.1, giveArmor: true);
    }

    private void ScheduleJoiningClientRespawn(
        IGameClient client,
        double delaySeconds,
        int remainingAttempts)
    {
        var generation = _lifecycleGeneration;
        _modSharp.PushTimer(
            () =>
            {
                if (_stopping
                    || generation != _lifecycleGeneration
                    || !IsActive()
                    || !IsHuman(client))
                {
                    return;
                }

                if (client.GetPlayerController() is { } controller
                    && IsPlayingTeam(controller.Team))
                {
                    RespawnJoinedPlayer(controller);
                    return;
                }

                if (remainingAttempts > 0)
                {
                    ScheduleJoiningClientRespawn(client, 0.5, remainingAttempts - 1);
                }
            },
            delaySeconds,
            GameTimerFlags.StopOnMapEnd);
    }

    private void SetNativeTeamScores(int terroristScore, int counterTerroristScore)
    {
        if (_entities.GetGlobalCStrikeTeam(CStrikeTeam.TE) is { } terrorists)
        {
            terrorists.Score = Math.Max(0, terroristScore);
        }

        if (_entities.GetGlobalCStrikeTeam(CStrikeTeam.CT) is { } counterTerrorists)
        {
            counterTerrorists.Score = Math.Max(0, counterTerroristScore);
        }
    }

    private void ScheduleAllPlayerLoadouts(double delay, bool giveArmor)
    {
        foreach (var controller in GetHumanControllers().Where(player => IsPlayingTeam(player.Team)))
        {
            SchedulePlayerLoadout(controller, delay, giveArmor);
        }
    }

    private void SchedulePlayerLoadout(IPlayerController controller, double delay, bool giveArmor)
    {
        if (!IsActiveHuman(controller))
        {
            return;
        }

        var steamId = controller.SteamId.AsPrimitive();
        var generation = ++_nextLoadoutGeneration;
        var lifecycleGeneration = _lifecycleGeneration;
        _scheduledLoadoutGenerations[steamId] = generation;
        _modSharp.PushTimer(
            () =>
            {
                if (_stopping || lifecycleGeneration != _lifecycleGeneration
                    || !_scheduledLoadoutGenerations.TryGetValue(steamId, out var currentGeneration)
                    || currentGeneration != generation)
                {
                    return;
                }

                _scheduledLoadoutGenerations.Remove(steamId);
                ApplyPlayerLoadout(controller, giveArmor);
            },
            delay,
            GameTimerFlags.StopOnMapEnd);
    }

    private void ApplyPlayerLoadout(IPlayerController controller, bool giveArmor)
    {
        if (!IsActiveHuman(controller) || controller.GetPlayerPawn() is not { IsAlive: true } pawn)
        {
            return;
        }

        var loadout = GetLoadout(controller);
        pawn.RemoveAllItems(removeSuit: false);
        if (giveArmor && _config.SpawnFullArmor)
        {
            pawn.ArmorValue = 100;
            if (_config.SpawnHelmet && pawn.GetItemService() is { } itemService)
            {
                itemService.HasHelmet = true;
            }
        }

        pawn.GiveNamedItem(loadout.Primary.EntityName);
        pawn.GiveNamedItem(loadout.Secondary.EntityName);
        if (!string.IsNullOrWhiteSpace(_config.DefaultGrenade))
        {
            pawn.GiveNamedItem(TdmWeaponCatalog.NormalizeEntityName(_config.DefaultGrenade));
        }

        pawn.GiveNamedItem("weapon_knife");
    }

    private void BuyWeapon(IGameClient client, TdmWeapon weapon)
    {
        var controller = client.GetPlayerController();
        if (!IsActiveHuman(controller))
        {
            return;
        }

        var loadout = GetLoadout(controller);
        if (weapon.Slot == TdmWeaponSlot.Primary)
        {
            loadout.Primary = weapon;
        }
        else
        {
            loadout.Secondary = weapon;
        }

        var steamId = controller.SteamId.AsPrimitive();
        _loadouts[steamId] = loadout;
        _scheduledLoadoutGenerations.Remove(steamId);
        ApplyPlayerLoadout(controller, giveArmor: false);
        client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 已选择 {weapon.DisplayName}，之后复活会自动发放。");
    }

    private PlayerLoadout GetLoadout(IPlayerController controller)
    {
        var steamId = controller.SteamId.AsPrimitive();
        if (_loadouts.TryGetValue(steamId, out var loadout))
        {
            return loadout;
        }

        TdmWeaponCatalog.TryResolve(_config.DefaultPrimary, out var primary);
        TdmWeaponCatalog.TryResolve(_config.DefaultSecondary, out var secondary);
        loadout = new PlayerLoadout(primary!, secondary!);
        _loadouts[steamId] = loadout;
        return loadout;
    }

    private void PrintBuyHelpToAll()
    {
        if (!_config.ShowBuyHelpOnRoundStart)
        {
            return;
        }

        foreach (var client in GetHumanClients())
        {
            PrintBuyHelpOnce(client);
        }
    }

    private void PrintBuyHelpOnce(IGameClient client)
    {
        if (!_config.ShowBuyHelpOnRoundStart
            || client.GetPlayerController() is not { } controller
            || !IsPlayingTeam(controller.Team)
            || !_buyHelpPromptedThisRound.Add(client.SteamId.AsPrimitive()))
        {
            return;
        }

        client.Print(
            HudPrintChannel.Chat,
            _config.BuyHelpMessage.Replace(
                "{prefix}",
                _config.Prefix,
                StringComparison.OrdinalIgnoreCase));
    }

    private void PrintWeaponHelp(IGameClient client)
    {
        client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 主武器：!ak !a1 !a4 !ssg !awp !aug !sg553 !mp9 !mp7 !mac10 !negev");
        client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 手枪：!de !fn57 !tec9，也支持 ！ 和 . 前缀。");
    }

    private void InstallCommands()
    {
        foreach (var command in BuyCommands)
        {
            _clients.InstallCommandListener(command, OnBuyCommand);
        }

        foreach (var command in TdmWeaponCatalog.Aliases.Concat(HelpCommands).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _clients.InstallCommandCallback(command, OnWeaponCommand);
            _registeredWeaponCommands.Add(command);
        }
    }

    private void RemoveCommands()
    {
        foreach (var command in BuyCommands)
        {
            _clients.RemoveCommandListener(command, OnBuyCommand);
        }

        foreach (var command in _registeredWeaponCommands)
        {
            _clients.RemoveCommandCallback(command, OnWeaponCommand);
        }

        _registeredWeaponCommands.Clear();
    }

    private ECommandAction OnBuyCommand(IGameClient client, StringCommand command)
    {
        if (!IsActive() || !IsHuman(client))
        {
            return ECommandAction.Skipped;
        }

        client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 团队竞技请用聊天指令买枪，例如 !ak、!de、.awp。");
        return ECommandAction.Stopped;
    }

    private ECommandAction OnWeaponCommand(IGameClient client, StringCommand command)
    {
        if (!IsActive() || !IsHuman(client))
        {
            return ECommandAction.Skipped;
        }

        var alias = command.CommandName;
        if (alias.StartsWith("ms_", StringComparison.OrdinalIgnoreCase))
        {
            alias = alias[3..];
        }
        else if (alias.StartsWith("css_", StringComparison.OrdinalIgnoreCase))
        {
            alias = alias[4..];
        }

        if (HelpCommands.Contains(alias, StringComparer.OrdinalIgnoreCase))
        {
            PrintWeaponHelp(client);
            return ECommandAction.Handled;
        }

        if (TdmWeaponCatalog.TryResolve(alias, out var weapon))
        {
            BuyWeapon(client, weapon);
            return ECommandAction.Handled;
        }

        PrintWeaponHelp(client);
        return ECommandAction.Handled;
    }

    private IEnumerable<IGameClient> GetHumanClients()
        => _clients.GetGameClients(inGame: true).Where(IsHuman);

    private IEnumerable<IPlayerController> GetHumanControllers()
        => GetHumanClients().Select(client => client.GetPlayerController()).OfType<IPlayerController>();

    private void Broadcast(string message)
    {
        foreach (var client in GetHumanClients())
        {
            client.Print(HudPrintChannel.Chat, message);
        }
    }

    private void RefreshModeContext()
    {
        _modeContext = _shared.GetSharpModuleManager()
            .GetOptionalSharpModuleInterface<IModeContext>(IModeContext.Identity);
    }

    private bool IsActive()
        => _config.Enabled && _modeContext?.Instance?.Current?.Selection.Mode == ModeId.TeamDeathmatch;

    private bool IsWarmup()
    {
        try
        {
            return _modSharp.GetGameRules().IsWarmupPeriod;
        }
        catch
        {
            return false;
        }
    }

    private bool IsActiveHuman([NotNullWhen(true)] IPlayerController? controller)
        => IsActiveParticipant(controller)
            && !BotIdentityRegistry.IsBot(
                controller.IsFakeClient,
                controller.PlayerSlot.AsPrimitive())
            && controller.SteamId.AsPrimitive() != 0;

    private bool IsActiveParticipant([NotNullWhen(true)] IPlayerController? controller)
        => IsActive()
            && controller is not null
            && controller.IsValid()
            && controller.IsConnected()
            && !controller.IsHltv
            && IsPlayingTeam(controller.Team);

    private static bool IsHuman(IGameClient client)
        => client.IsValid
            && client.IsInGame
            && !BotIdentityRegistry.IsBot(client.IsFakeClient, client.Slot.AsPrimitive())
            && !client.IsHltv;

    private static bool IsPlayingTeam(CStrikeTeam team)
        => team is CStrikeTeam.CT or CStrikeTeam.TE;

    private static TeamAssignment ToDomainTeam(CStrikeTeam team)
        => team switch
        {
            CStrikeTeam.CT => TeamAssignment.CounterTerrorist,
            CStrikeTeam.TE => TeamAssignment.Terrorist,
            CStrikeTeam.Spectator => TeamAssignment.Spectator,
            _ => TeamAssignment.Unassigned,
        };

    private sealed class PlayerLoadout(TdmWeapon primary, TdmWeapon secondary)
    {
        public TdmWeapon Primary { get; set; } = primary;
        public TdmWeapon Secondary { get; set; } = secondary;
    }
}
