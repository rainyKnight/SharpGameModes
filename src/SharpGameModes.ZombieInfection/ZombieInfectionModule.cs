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

namespace SharpGameModes.ZombieInfection;

public sealed partial class ZombieInfectionModule : IModSharpModule, IGameListener, IClientListener, IEventListener
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ISharedSystem _shared;
    private readonly IModSharp _modSharp;
    private readonly IClientManager _clients;
    private readonly IEntityManager _entities;
    private readonly IEventManager _events;
    private readonly IHookManager _hooks;
    private readonly ILogger<ZombieInfectionModule> _logger;
    private readonly string _configPath;
    private readonly Dictionary<int, int> _zombieLives = [];
    private readonly Dictionary<int, string> _savedModels = [];
    private readonly Dictionary<int, HumanLoadout> _humanLoadouts = [];
    private readonly Dictionary<int, DateTimeOffset> _fallSoundSuppressUntil = [];
    private readonly Dictionary<int, CorpseTransform> _pendingCorpseTransforms = [];
    private readonly Dictionary<int, IBaseModelEntity> _corpseMarkers = [];
    private readonly HashSet<int> _motherZombies = [];
    private readonly HashSet<int> _pendingCorpseInfections = [];
    private readonly HashSet<int> _weaponHelpPrompted = [];
    private readonly List<string> _registeredWeaponCommands = [];
    private ZombieInfectionConfig _config = new();
    private IModSharpModuleInterface<IModeContext>? _modeContext;
    private IDisposable? _modeContextSubscription;
    private ZombiePhase _phase = ZombiePhase.Disabled;
    private int _secondsLeft;
    private int _lifecycleGeneration;
    private int _phaseGeneration;
    private bool _stopping;
    private bool _hooksInstalled;
    private bool _modeListenersInstalled;
    private bool _modeRuntimeActive;
    private CStrikeTeam? _pendingWinner;
    private int _pendingWinnerScoreBefore;

    public ZombieInfectionModule(
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
        _hooks = sharedSystem.GetHookManager();
        _logger = sharedSystem.GetLoggerFactory().CreateLogger<ZombieInfectionModule>();
        _configPath = Path.Combine(sharpPath, "configs", "sharp-gamemodes", "zombie.jsonc");
    }

    public string DisplayName => "SharpGameModes Zombie Infection";
    public string DisplayAuthor => "SharpGameModes Contributors";
    public int ListenerVersion => IGameListener.ApiVersion;
    public int ListenerPriority => 20;

    public bool Init()
    {
        try
        {
            _config = JsonSerializer.Deserialize<ZombieInfectionConfig>(
                File.ReadAllText(_configPath), SerializerOptions)
                ?? throw new InvalidDataException("Zombie config is empty.");
            _config.Validate();
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or ArgumentException)
        {
            _logger.LogError(exception, "Failed to load zombie infection config from {Path}.", _configPath);
            return false;
        }

        _modSharp.InstallGameListener(this);
        foreach (var eventName in new[] { "round_start", "round_end", "player_spawn", "player_death", "player_team", "weapon_fire" })
        {
            _events.HookEvent(eventName);
        }

        InstallCommands();
        return true;
    }

    public void OnAllModulesLoaded()
    {
        RefreshModeContext();
        Schedule(ActivateCurrentModeAfterLoad, 0.25);
    }

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
            _modeContextSubscription?.Dispose();
            _modeContextSubscription = null;
            _modeContext = null;
            SetModeRuntimeEnabled(false);
        }
    }

    public void OnGameInit()
    {
        _lifecycleGeneration++;
        DisableRuntime();
    }

    public void OnGamePreShutdown()
    {
        _lifecycleGeneration++;
        DisableRuntime();
    }

    public void OnGameShutdown()
    {
        ClearRoundState(removeCorpseEntities: false);
    }

    public void OnResourcePrecache()
    {
        foreach (var model in _config.ConfiguredModels())
        {
            _modSharp.PrecacheResource(model);
        }
    }

    public void OnRoundRestart()
    {
        if (!IsActive())
        {
            DisableRuntime();
            return;
        }

        _phase = ZombiePhase.Waiting;
        if (IsWarmup())
        {
            var generation = _lifecycleGeneration;
            Schedule(() => EnsureWarmupHumans(generation), 0.2);
        }
    }

    public void OnClientPutInServer(IGameClient client)
    {
        if (!IsEligible(client) || !IsActive())
        {
            return;
        }

        var generation = _lifecycleGeneration;
        Schedule(() => HandleJoiningClient(client, generation), 0.7);
    }

    public void OnClientDisconnecting(IGameClient client, NetworkDisconnectionReason reason)
    {
        if (client.GetPlayerController() is { } controller)
        {
            ClearPlayerState(PlayerKey(controller));
        }

        if (IsActive() && _phase == ZombiePhase.Active)
        {
            Schedule(CheckRoundEnd, 0.2);
        }
    }

    public ECommandAction OnClientSayCommand(
        IGameClient client,
        bool teamOnly,
        bool isCommand,
        string commandName,
        string message)
    {
        if (!IsActive() || !IsEligible(client) || isCommand || string.IsNullOrWhiteSpace(message))
        {
            return ECommandAction.Skipped;
        }

        var text = message.Trim().Trim('"');
        if (text.Length < 2 || text[0] is not ('!' or '！' or '.' or '/'))
        {
            return ECommandAction.Skipped;
        }

        var alias = text[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (IsArmorAlias(alias))
        {
            GiveFullArmor(client);
            return ECommandAction.Handled;
        }

        if (IsHelpAlias(alias))
        {
            PrintWeaponHelp(client);
            return ECommandAction.Handled;
        }

        if (!ZombieWeaponCatalog.TryResolve(alias, out var weapon))
        {
            return ECommandAction.Skipped;
        }

        GiveHumanWeapon(client, weapon);
        return ECommandAction.Handled;
    }

    public void FireGameEvent(IGameEvent gameEvent)
    {
        try
        {
            switch (gameEvent.Name)
            {
                case "round_start":
                    OnRoundStart();
                    break;
                case "round_end":
                    OnRoundEnd();
                    break;
                case "player_spawn" when gameEvent is IEventPlayerSpawn spawn:
                    OnPlayerSpawn(spawn.Controller);
                    break;
                case "player_death" when gameEvent is IEventPlayerDeath death:
                    OnPlayerDeath(death);
                    break;
                case "player_team" when gameEvent is IEventPlayerTeam team:
                    OnPlayerTeam(team);
                    break;
                case "weapon_fire" when gameEvent is IEventWeaponFired fired:
                    OnWeaponFired(fired);
                    break;
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to process zombie event {EventName}.", gameEvent.Name);
        }
    }

    public void Shutdown()
    {
        _stopping = true;
        _lifecycleGeneration++;
        _modeContextSubscription?.Dispose();
        _modeContextSubscription = null;
        _modeContext = null;
        SetModeRuntimeEnabled(false);

        RemoveCommands();
        _modSharp.RemoveGameListener(this);
    }

    private void OnRoundStart()
    {
        if (!IsActive())
        {
            DisableRuntime();
            return;
        }

        _lifecycleGeneration++;
        _phaseGeneration++;
        ResetRoundState();
        _phase = ZombiePhase.Waiting;
        if (IsWarmup())
        {
            EnsureWarmupHumans(_lifecycleGeneration);
            return;
        }

        StartZombieRound("round-start");
    }

    private void OnRoundEnd()
    {
        if (!IsActive())
        {
            return;
        }

        _phaseGeneration++;
        _phase = ZombiePhase.Ended;
        Schedule(EnsurePendingRoundAccounting, 0.05);
    }

    private void OnPlayerSpawn(IPlayerController? controller)
    {
        if (!IsEligible(controller) || !IsActive())
        {
            return;
        }

        var generation = _lifecycleGeneration;
        Schedule(
            () =>
            {
                if (!IsCurrent(generation) || !IsEligible(controller))
                {
                    return;
                }

                if (IsWarmup())
                {
                    EnsureWarmupHuman(controller);
                }
                else if (controller.Team == CStrikeTeam.TE)
                {
                    ApplyZombieSpawn(controller);
                }
                else if (_phase == ZombiePhase.Active)
                {
                    JoinActiveRoundAsZombie(controller, announce: false);
                }
                else
                {
                    ApplyHumanSpawn(controller);
                }
            },
            0.15);
    }

    private void OnPlayerTeam(IEventPlayerTeam gameEvent)
    {
        if (gameEvent.Disconnect || !IsEligible(gameEvent.Controller) || !IsActive())
        {
            return;
        }

        var controller = gameEvent.Controller;
        var generation = _lifecycleGeneration;
        Schedule(
            () =>
            {
                if (!IsCurrent(generation) || !IsEligible(controller))
                {
                    return;
                }

                if (IsWarmup())
                {
                    EnsureWarmupHuman(controller);
                }
                else if (_phase == ZombiePhase.Active && controller.Team != CStrikeTeam.TE)
                {
                    JoinActiveRoundAsZombie(controller, announce: false);
                }
                else if (_phase is ZombiePhase.Waiting or ZombiePhase.Countdown && controller.Team != CStrikeTeam.CT)
                {
                    RespawnAndApplyHuman(controller);
                }
            },
            0.35);
    }

    private void OnPlayerDeath(IEventPlayerDeath gameEvent)
    {
        if (!IsActive() || _phase != ZombiePhase.Active || !IsEligible(gameEvent.VictimController))
        {
            return;
        }

        var victim = gameEvent.VictimController;
        var attacker = gameEvent.KillerController;
        var deferEndCheck = false;
        if (victim.Team == CStrikeTeam.CT)
        {
            if (IsEligible(attacker) && attacker.Team == CStrikeTeam.TE && IsKnifeName(gameEvent.Weapon))
            {
                var pawn = gameEvent.VictimPawn;
                ScheduleCorpseInfection(
                    victim,
                    attacker,
                    pawn is null ? null : new Vector(pawn.GetAbsOrigin()),
                    pawn is null ? null : new Vector(pawn.GetAbsAngles()));
                deferEndCheck = true;
            }
            else
            {
                var generation = _lifecycleGeneration;
                Schedule(
                    () =>
                    {
                        if (IsCurrent(generation) && _phase == ZombiePhase.Active
                            && IsEligible(victim) && victim.Team == CStrikeTeam.CT)
                        {
                            RespawnAndApplyHuman(victim);
                        }
                    },
                    1);
            }
        }
        else if (victim.Team == CStrikeTeam.TE)
        {
            HandleZombieDeath(victim);
        }

        if (!deferEndCheck)
        {
            Schedule(CheckRoundEnd, 0.5);
        }
    }

    private void OnWeaponFired(IEventWeaponFired gameEvent)
    {
        if (!_config.InfiniteHumanAmmo || !IsActive() || _phase != ZombiePhase.Active
            || !IsEligible(gameEvent.Controller) || gameEvent.Controller.Team != CStrikeTeam.CT)
        {
            return;
        }

        var controller = gameEvent.Controller;
        Schedule(() => RefillHumanReserveAmmo(controller), 0.05);
    }

    private void StartZombieRound(string reason)
    {
        if (!IsActive())
        {
            return;
        }

        if (IsWarmup())
        {
            EnsureWarmupHumans(_lifecycleGeneration);
            return;
        }

        var players = GetEligibleControllers().ToList();
        if (players.Count < _config.MinimumPlayers)
        {
            StartWaitingForPlayers(players.Count);
            return;
        }

        foreach (var player in players)
        {
            RespawnAndApplyHuman(player);
        }

        _phase = ZombiePhase.Countdown;
        _secondsLeft = _config.FirstInfectionDelaySeconds;
        Broadcast($"{_config.Prefix} 感染将在 {_secondsLeft} 秒后开始。");
        var phaseGeneration = ++_phaseGeneration;
        ScheduleCountdownTick(phaseGeneration);
        _logger.LogInformation(
            "Zombie round started by {Reason}: players={Players}, countdown={Countdown}.",
            reason,
            players.Count,
            _secondsLeft);
    }

    private void StartWaitingForPlayers(int currentPlayers)
    {
        _phase = ZombiePhase.Waiting;
        _secondsLeft = 0;
        Broadcast($"{_config.Prefix} 等待玩家：{currentPlayers}/{_config.MinimumPlayers}。");
        var phaseGeneration = ++_phaseGeneration;
        ScheduleWaitingTick(phaseGeneration);
    }

    private void ScheduleWaitingTick(int phaseGeneration)
    {
        Schedule(
            () =>
            {
                if (_phase != ZombiePhase.Waiting || phaseGeneration != _phaseGeneration || !IsActive())
                {
                    return;
                }

                if (IsWarmup())
                {
                    EnsureWarmupHumans(_lifecycleGeneration);
                    PrintCenterToAll("<font color='#ffdf7e'>热身中，僵尸模式将在正式回合开始</font>");
                    ScheduleWaitingTick(phaseGeneration);
                    return;
                }

                var count = GetEligibleControllers().Count();
                if (count >= _config.MinimumPlayers)
                {
                    StartZombieRound("waiting-complete");
                }
                else
                {
                    PrintCenterToAll($"<font color='#ffdf7e'>等待玩家 {count}/{_config.MinimumPlayers}</font>");
                    ScheduleWaitingTick(phaseGeneration);
                }
            },
            3);
    }

    private void ScheduleCountdownTick(int phaseGeneration)
    {
        Schedule(
            () =>
            {
                if (_phase != ZombiePhase.Countdown || phaseGeneration != _phaseGeneration || !IsActive())
                {
                    return;
                }

                _secondsLeft--;
                if (_secondsLeft is > 0 and <= 5)
                {
                    Broadcast($"{_config.Prefix} 感染倒计时：{_secondsLeft}");
                }

                PrintCenterToAll($"<font color='#ffdf7e'>感染倒计时 {Math.Max(0, _secondsLeft)}</font>");
                if (_secondsLeft <= 0)
                {
                    StartInfection();
                }
                else
                {
                    ScheduleCountdownTick(phaseGeneration);
                }
            },
            1);
    }

    private void StartInfection()
    {
        var humans = GetEligibleControllers()
            .Where(player => player.Team == CStrikeTeam.CT && IsAlive(player))
            .OrderBy(_ => Random.Shared.Next())
            .ToList();
        if (humans.Count < _config.MinimumPlayers)
        {
            StartWaitingForPlayers(humans.Count);
            return;
        }

        var zombieCount = ZombieRoundRules.CalculateInitialZombieCount(
            humans.Count,
            _config.MinimumInitialZombies,
            _config.InitialZombieRatio,
            _config.MaximumInitialZombies);
        foreach (var player in humans.Take(zombieCount))
        {
            InfectPlayer(player, null, isMother: true);
        }

        _phase = ZombiePhase.Active;
        _secondsLeft = Math.Max(1, _config.RoundDurationSeconds - _config.FirstInfectionDelaySeconds);
        Broadcast($"{_config.Prefix} 感染开始，幸存者坚持到时间结束！");
        var phaseGeneration = ++_phaseGeneration;
        ScheduleActiveTick(phaseGeneration);
    }

    private void ScheduleActiveTick(int phaseGeneration)
    {
        Schedule(
            () =>
            {
                if (_phase != ZombiePhase.Active || phaseGeneration != _phaseGeneration || !IsActive())
                {
                    return;
                }

                _secondsLeft--;
                PrintCenterToAll(
                    $"<font color='#ffdf7e'>僵尸模式 {FormatTime(_secondsLeft)}</font><br>" +
                    $"人类 {CountAliveHumans()} / 僵尸 {CountActiveZombies()}");
                CheckRoundEnd();
                if (_phase == ZombiePhase.Active)
                {
                    ScheduleActiveTick(phaseGeneration);
                }
            },
            1);
    }

    private void CheckRoundEnd()
    {
        if (!IsActive() || _phase != ZombiePhase.Active)
        {
            return;
        }

        var result = ZombieRoundRules.Evaluate(
            CountAliveHumans(),
            CountActiveZombies(),
            _pendingCorpseInfections.Count,
            _secondsLeft);
        switch (result)
        {
            case ZombieRoundOutcome.HumansWin:
                EndZombieRound(CStrikeTeam.CT, "human-condition");
                break;
            case ZombieRoundOutcome.ZombiesWin:
                EndZombieRound(CStrikeTeam.TE, "all-infected");
                break;
        }
    }

    private void EndZombieRound(CStrikeTeam winner, string reason)
    {
        if (!IsActive() || _phase == ZombiePhase.Ended)
        {
            return;
        }

        _phase = ZombiePhase.Ended;
        _phaseGeneration++;
        var zombiesWin = winner == CStrikeTeam.TE;
        Broadcast(zombiesWin
            ? $"{_config.Prefix} 僵尸胜利，所有人类已被感染。"
            : $"{_config.Prefix} 人类胜利，幸存者撑到了最后。");
        _logger.LogInformation("Zombie round ended: winner={Winner}, reason={Reason}.", winner, reason);

        _pendingWinner = winner;
        _pendingWinnerScoreBefore = _entities.GetGlobalCStrikeTeam(winner)?.Score ?? 0;
        _modSharp.ServerCommand("mp_ignore_round_win_conditions 1");
        _modSharp.GetGameRules().TerminateRound(
            (float)_config.PostRoundDelaySeconds,
            zombiesWin ? RoundEndReason.TerroristsWin : RoundEndReason.CTsWin);
    }

    private void EnsurePendingRoundAccounting()
    {
        if (!_config.ManualRoundAccounting || _pendingWinner is not { } winner)
        {
            _pendingWinner = null;
            return;
        }

        _pendingWinner = null;
        var team = _entities.GetGlobalCStrikeTeam(winner);
        if (team is not null && team.Score <= _pendingWinnerScoreBefore)
        {
            team.Score = _pendingWinnerScoreBefore + 1;
        }
    }

    private void HandleJoiningClient(IGameClient client, int generation)
    {
        if (!IsCurrent(generation) || !IsEligible(client) || client.GetPlayerController() is not { } controller)
        {
            return;
        }

        if (IsWarmup())
        {
            EnsureWarmupHuman(controller);
        }
        else if (_phase == ZombiePhase.Waiting)
        {
            if (GetEligibleControllers().Count() >= _config.MinimumPlayers)
            {
                StartZombieRound("client-join");
            }
        }
        else if (_phase == ZombiePhase.Countdown)
        {
            RespawnAndApplyHuman(controller);
        }
        else if (_phase == ZombiePhase.Active)
        {
            JoinActiveRoundAsZombie(controller, announce: true);
        }
    }

    private void EnsureWarmupHumans(int generation)
    {
        if (!IsCurrent(generation) || !IsActive() || !IsWarmup())
        {
            return;
        }

        _phase = ZombiePhase.Waiting;
        foreach (var controller in GetEligibleControllers())
        {
            EnsureWarmupHuman(controller);
        }
    }

    private void EnsureWarmupHuman(IPlayerController controller)
    {
        if (!IsEligible(controller) || !IsActive() || !IsWarmup())
        {
            return;
        }

        if (controller.Team != CStrikeTeam.CT)
        {
            controller.SwitchTeam(CStrikeTeam.CT);
        }

        if (!IsAlive(controller))
        {
            controller.Respawn();
        }

        Schedule(() => ApplyHumanSpawn(controller), 0.15);
    }

    private void ResetRoundState()
    {
        RestoreSavedModels();
        ClearRoundState(removeCorpseEntities: true);
    }

    private void ClearRoundState(bool removeCorpseEntities)
    {
        if (removeCorpseEntities)
        {
            ClearCorpseMarkers();
        }
        else
        {
            _corpseMarkers.Clear();
        }

        _zombieLives.Clear();
        _humanLoadouts.Clear();
        _fallSoundSuppressUntil.Clear();
        _savedModels.Clear();
        _pendingCorpseTransforms.Clear();
        _motherZombies.Clear();
        _pendingCorpseInfections.Clear();
        _weaponHelpPrompted.Clear();
        _pendingWinner = null;
    }

    private void DisableRuntime()
    {
        _phaseGeneration++;
        _phase = ZombiePhase.Disabled;
        _secondsLeft = 0;
        ResetRoundState();
    }

    private void DisableRuntimeWithoutEntityAccess()
    {
        _phaseGeneration++;
        _phase = ZombiePhase.Disabled;
        _secondsLeft = 0;
        ClearRoundState(removeCorpseEntities: false);
    }

    private void ClearPlayerState(int key)
    {
        _zombieLives.Remove(key);
        _savedModels.Remove(key);
        _humanLoadouts.Remove(key);
        _fallSoundSuppressUntil.Remove(key);
        _pendingCorpseTransforms.Remove(key);
        RemoveCorpseMarker(key);
        _motherZombies.Remove(key);
        _pendingCorpseInfections.Remove(key);
        _weaponHelpPrompted.Remove(key);
    }

    private void RefreshModeContext()
    {
        var next = _shared.GetSharpModuleManager()
            .GetOptionalSharpModuleInterface<IModeContext>(IModeContext.Identity);
        if (ReferenceEquals(_modeContext?.Instance, next?.Instance))
        {
            SetModeRuntimeEnabled(ShouldEnableModeRuntime(next?.Instance?.Current));
            return;
        }

        _modeContextSubscription?.Dispose();
        _modeContextSubscription = null;
        _modeContext = next;
        if (next?.Instance is not { } context)
        {
            SetModeRuntimeEnabled(false);
            return;
        }

        _modeContextSubscription = context.Subscribe(ApplyModeContext);
        SetModeRuntimeEnabled(ShouldEnableModeRuntime(context.Current));
    }

    private void ApplyModeContext(ModeContextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        SetModeRuntimeEnabled(ShouldEnableModeRuntime(snapshot));
    }

    private void SetModeRuntimeEnabled(bool enabled)
    {
        enabled &= !_stopping && _config.Enabled;
        if (enabled == _modeRuntimeActive)
        {
            return;
        }

        if (enabled)
        {
            InstallHooks();
            InstallModeListeners();
            _modeRuntimeActive = true;
            _logger.LogInformation("Zombie mode runtime listeners and high-frequency hooks enabled.");
            return;
        }

        _modeRuntimeActive = false;
        _lifecycleGeneration++;
        RemoveModeListeners();
        RemoveHooks();
        DisableRuntimeWithoutEntityAccess();
        _logger.LogInformation("Zombie mode runtime listeners and high-frequency hooks disabled.");
    }

    private void InstallModeListeners()
    {
        if (_modeListenersInstalled)
        {
            return;
        }

        _clients.InstallClientListener(this);
        _events.InstallEventListener(this);
        _modeListenersInstalled = true;
    }

    private void RemoveModeListeners()
    {
        if (!_modeListenersInstalled)
        {
            return;
        }

        _events.RemoveEventListener(this);
        _clients.RemoveClientListener(this);
        _modeListenersInstalled = false;
    }

    private void ActivateCurrentModeAfterLoad()
    {
        if (!IsActive())
        {
            return;
        }

        _lifecycleGeneration++;
        ResetRoundState();
        _phase = ZombiePhase.Waiting;
        if (IsWarmup())
        {
            EnsureWarmupHumans(_lifecycleGeneration);
        }
        else
        {
            StartZombieRound("module-load");
        }
    }

    private bool IsActive()
        => !_stopping && _modeRuntimeActive;

    private static bool ShouldEnableModeRuntime(ModeContextSnapshot? snapshot)
        => snapshot?.Selection.Mode == ModeId.Zombie;

    private bool IsCurrent(int generation) => !_stopping && generation == _lifecycleGeneration && IsActive();

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

    private IEnumerable<IGameClient> GetEligibleClients()
        => _clients.GetGameClients(inGame: true).Where(IsEligible);

    private IEnumerable<IPlayerController> GetEligibleControllers()
        => GetEligibleClients().Select(client => client.GetPlayerController()).OfType<IPlayerController>();

    private bool IsEligible([NotNullWhen(true)] IGameClient? client)
        => client is { IsValid: true, IsInGame: true }
            && !client.IsHltv
            && (_config.IncludeBotsInRound
                || !BotIdentityRegistry.IsBot(
                    client.IsFakeClient,
                    client.Slot.AsPrimitive()));

    private bool IsEligible([NotNullWhen(true)] IPlayerController? controller)
        => controller is not null
            && controller.IsValid()
            && controller.IsConnected()
            && controller.GetGameClient() is { } client
            && IsEligible(client);

    private static bool IsAlive(IPlayerController controller)
        => controller.GetPlayerPawn() is { IsAlive: true, Health: > 0 };

    private static int PlayerKey(IPlayerController controller) => controller.PlayerSlot.AsPrimitive();

    private int CountAliveHumans()
        => GetEligibleControllers().Count(player => player.Team == CStrikeTeam.CT && IsAlive(player));

    private int CountActiveZombies()
        => GetEligibleControllers().Count(player =>
            player.Team == CStrikeTeam.TE
            && (_config.ZombieLives <= 0 || _zombieLives.GetValueOrDefault(PlayerKey(player), _config.ZombieLives) > 0));

    private void Schedule(Action callback, double delay)
        => _modSharp.PushTimer(
            () =>
            {
                if (!_stopping)
                {
                    callback();
                }
            },
            delay,
            GameTimerFlags.StopOnMapEnd);

    private void Broadcast(string message)
    {
        foreach (var client in GetEligibleClients())
        {
            client.Print(HudPrintChannel.Chat, message);
        }
    }

    private void PrintCenterToAll(string message)
    {
        foreach (var controller in GetEligibleControllers())
        {
            controller.PrintCenterHtml(message);
        }
    }

    private static string FormatTime(int seconds)
    {
        seconds = Math.Max(0, seconds);
        return $"{seconds / 60}:{seconds % 60:00}";
    }

    private enum ZombiePhase
    {
        Disabled,
        Waiting,
        Countdown,
        Active,
        Ended,
    }

    private sealed class HumanLoadout
    {
        public ZombieWeapon? Primary { get; set; }
        public ZombieWeapon? Secondary { get; set; }
    }

    private readonly record struct CorpseTransform(Vector Origin, Vector Angles);
}
