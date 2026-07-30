using System.Collections.Immutable;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SharpGameModes.Contracts;
using SharpGameModes.Domain;
using Sharp.Modules.AdminManager.Shared;
using Sharp.Modules.TargetingManager.Shared;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameEvents;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace SharpGameModes.AutoTeam;

public sealed class AutoTeamModule : IModSharpModule, IGameListener, IClientListener, IEventListener
{
    private const string ModuleIdentity = "SharpGameModes.AutoTeam";
    private const string ForceTeamPermission = "admin:team";
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    private readonly ISharedSystem _shared;
    private readonly IModSharp _modSharp;
    private readonly IClientManager _clients;
    private readonly IEventManager _events;
    private readonly IHookManager _hooks;
    private readonly ILogger<AutoTeamModule> _logger;
    private readonly string _configPath;
    private readonly string _sharpPath;
    private AutoTeamConfig _config = new();
    private EffectiveAutoTeamRule _currentRule = AutoTeamRuleResolver.Resolve(
        new AutoTeamConfig().ToRuleDefaults(),
        null,
        playerDataAllowed: true,
        "default");
    private MapSelection? _currentSelection;
    private IModSharpModuleInterface<IModeContext>? _modeContext;
    private IModSharpModuleInterface<IPlayerRatingProvider>? _ratingProvider;
    private IModSharpModuleInterface<IPlayerMatchResultSource>? _matchResultSource;
    private IModSharpModuleInterface<IAdminManager>? _adminManager;
    private IModSharpModuleInterface<ITargetingManager>? _targetingManager;
    private IAdminManager? _adminCommandsRegisteredWith;
    private IDisposable? _modeContextSubscription;
    private IDisposable? _matchResultSubscription;
    private IReadOnlyCollection<string>? _lastBalancedRoster;
    private readonly Dictionary<string, TeamAssignment> _lastBalancedTeams = [];
    private readonly Dictionary<ulong, int> _initialHealthBySteamId = [];
    private readonly Dictionary<ulong, HealthAssignment> _healthAssignments = [];
    private readonly Dictionary<ulong, HealthCompensationState> _healthCompensationStates = [];
    private string? _healthCompensationStatePath;
    private bool _healthEventInstalled;
    private bool _hasAppliedInitialBalance;
    private bool _stopping;
    private int _balanceGeneration;
    private readonly Random _random = new();

    public AutoTeamModule(
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
        _events = sharedSystem.GetEventManager();
        _hooks = sharedSystem.GetHookManager();
        _logger = sharedSystem.GetLoggerFactory().CreateLogger<AutoTeamModule>();
        _sharpPath = sharpPath;
        _configPath = Path.Combine(sharpPath, "configs", "sharp-gamemodes", "auto-team.jsonc");
    }

    public string DisplayName => "SharpGameModes Auto Team";
    public string DisplayAuthor => "SharpGameModes Contributors";
    public int ListenerVersion => IGameListener.ApiVersion;
    public int ListenerPriority => 25;

    public bool Init()
    {
        try
        {
            _config = JsonSerializer.Deserialize<AutoTeamConfig>(File.ReadAllText(_configPath), SerializerOptions)
                ?? throw new InvalidDataException("Auto-team config is empty.");
            _config.Validate();
            _healthCompensationStatePath = Path.IsPathRooted(_config.HealthCompensationStatePath)
                ? _config.HealthCompensationStatePath
                : Path.Combine(_sharpPath, _config.HealthCompensationStatePath);
            LoadHealthCompensationState();
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or ArgumentException)
        {
            _logger.LogError(exception, "Failed to load auto-team config from {Path}.", _configPath);
            return false;
        }

        _modSharp.InstallGameListener(this);
        _clients.InstallClientListener(this);
        _hooks.HandleCommandJoinTeam.InstallHookPre(OnHandleCommandJoinTeam, ListenerPriority);
        _events.InstallEventListener(this);
        _events.HookEvent("player_spawn");
        _healthEventInstalled = true;

        return true;
    }

    public void OnAllModulesLoaded()
    {
        RefreshModeContext();
        RefreshPlayerDataInterfaces();
        RefreshAdminInterfaces();
        if (_config.UsePlayerDataForBalance && _ratingProvider?.Instance is null)
        {
            _logger.LogWarning("SharpGameModes.PlayerData is unavailable; team balancing will use default_rating.");
        }
    }

    public void OnLibraryConnected(string name)
    {
        if (name.Equals("SharpGameModes.Core", StringComparison.OrdinalIgnoreCase))
        {
            RefreshModeContext();
        }
        else if (name.Equals("SharpGameModes.PlayerData", StringComparison.OrdinalIgnoreCase))
        {
            RefreshPlayerDataInterfaces();
        }
        else if (IsLibrary(name, "AdminManager")
            || IsLibrary(name, "TargetingManager")
            || IsLibrary(name, "CommandCenter"))
        {
            if (IsLibrary(name, "CommandCenter"))
            {
                _adminCommandsRegisteredWith = null;
            }

            RefreshAdminInterfaces();
        }
    }

    public void OnLibraryDisconnect(string name)
    {
        if (name.Equals("SharpGameModes.Core", StringComparison.OrdinalIgnoreCase))
        {
            _modeContextSubscription?.Dispose();
            _modeContextSubscription = null;
            _modeContext = null;
            _currentSelection = null;
            RefreshCurrentRule();
        }
        else if (name.Equals("SharpGameModes.PlayerData", StringComparison.OrdinalIgnoreCase))
        {
            _matchResultSubscription?.Dispose();
            _matchResultSubscription = null;
            _matchResultSource = null;
            _ratingProvider = null;
            RefreshCurrentRule();
        }
        else if (IsLibrary(name, "AdminManager"))
        {
            _adminManager = null;
            _adminCommandsRegisteredWith = null;
        }
        else if (IsLibrary(name, "TargetingManager"))
        {
            _targetingManager = null;
        }
        else if (IsLibrary(name, "CommandCenter"))
        {
            _adminCommandsRegisteredWith = null;
        }
    }

    public void OnClientPutInServer(IGameClient client)
    {
        if (IsBot(client) || client.IsHltv)
        {
            return;
        }

        var steamId = client.SteamId.AsPrimitive();
        _modSharp.PushTimer(
            () =>
            {
                if (_config.ObserverWhitelistIds.Contains(steamId))
                {
                    MoveWhitelistedClientToSpectator(client, steamId);
                }
                else
                {
                    AssignUnassignedClient(client, skipWarmup: true);
                }
            },
            0.5,
            GameTimerFlags.StopOnMapEnd);
    }

    public void OnGameInit()
    {
        _balanceGeneration++;
        _lastBalancedRoster = null;
        _lastBalancedTeams.Clear();
        _initialHealthBySteamId.Clear();
        _healthAssignments.Clear();
        _hasAppliedInitialBalance = false;
        RefreshCurrentRule();
    }

    public void FireGameEvent(IGameEvent gameEvent)
    {
        if (gameEvent.Name != "player_spawn"
            || !_currentRule.BalanceHealthByRating
            || !IsAutoTeamModeActive()
            || _modSharp.GetGameRules().IsWarmupPeriod)
        {
            return;
        }

        var controller = gameEvent is IEventPlayerSpawn spawn
            ? spawn.Controller
            : gameEvent.GetPlayerController("userid");
        if (!IsActiveHuman(controller))
        {
            return;
        }

        var steamId = controller.SteamId.AsPrimitive();
        _modSharp.PushTimer(
            () =>
            {
                if (IsActiveHuman(controller)
                    && controller.SteamId.AsPrimitive() == steamId
                    && IsAutoTeamModeActive()
                    && !_modSharp.GetGameRules().IsWarmupPeriod)
                {
                    ApplyRatingBalancedHealth(controller);
                }
            },
            0.1,
            GameTimerFlags.StopOnMapEnd);
    }

    public void OnRoundRestart()
    {
        if (!_currentRule.BalanceOnRoundStart
            || !IsAutoTeamModeActive()
            || _modSharp.GetGameRules().IsWarmupPeriod)
        {
            return;
        }

        ApplyNativeTeamBalanceControl("round-restart");
        var preserveEngineTeamSwitch = _modSharp.GetGameRules().SwitchingTeamsAtRoundReset;
        var generation = ++_balanceGeneration;
        _modSharp.PushTimer(
            () =>
            {
                if (!_stopping && generation == _balanceGeneration && IsAutoTeamModeActive())
                {
                    AssignAllUnassignedClients();
                    BalanceCurrentRoster(preserveEngineTeamSwitch);
                }
            },
            _currentRule.RoundStartBalanceDelaySeconds,
            GameTimerFlags.StopOnMapEnd);
    }

    private void BalanceCurrentRoster(bool preserveEngineTeamSwitch = false)
    {
        var controllers = GetHumanControllers()
            .Where(controller => controller.Team is CStrikeTeam.CT or CStrikeTeam.TE)
            .ToArray();
        if (controllers.Length < 2)
        {
            return;
        }

        var roster = controllers
            .Select(GetPlayerId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var rosterUnchanged = _lastBalancedRoster is not null
            && roster.ToHashSet(StringComparer.Ordinal).SetEquals(_lastBalancedRoster);
        var completeTeamSwitch = IsCompleteTeamSwitch(controllers, roster);
        if (_currentRule.RoundRandomizeMode != RoundRandomizeMode.EveryRound
            && (completeTeamSwitch || preserveEngineTeamSwitch && rosterUnchanged))
        {
            _lastBalancedRoster = roster;
            RememberCurrentTeams(controllers);
            _logger.LogInformation(
                "Preserved the engine's half-time team switch for {Count} human players.",
                controllers.Length);
            return;
        }

        var candidates = controllers.Select(controller =>
            new PlayerBalanceCandidate(
                GetPlayerId(controller),
                GetRating(controller),
                ToDomainTeam(controller.Team)))
            .ToArray();
        TeamBalancePlan plan;
        string strategy;
        if (_currentRule.RoundRandomizeMode == RoundRandomizeMode.EveryRound)
        {
            plan = CreateRandomPlan(candidates);
            strategy = "every-round randomization";
        }
        else if (_currentRule.RoundRandomizeMode == RoundRandomizeMode.FirstRoundThenBalance
            && !_hasAppliedInitialBalance)
        {
            plan = TeamBalancer.CreatePlan(
                candidates,
                _currentRule.CounterTerroristRatio,
                _currentRule.TerroristRatio);
            _hasAppliedInitialBalance = true;
            strategy = "initial rating balance";
        }
        else
        {
            plan = TeamBalancer.CreateMinimumMovementPlan(
                candidates,
                _currentRule.CounterTerroristRatio,
                _currentRule.TerroristRatio,
                _currentRule.AllowedCountDeviation);
            strategy = "minimum-movement correction";
        }

        var moved = 0;
        foreach (var controller in controllers)
        {
            if (!plan.Assignments.TryGetValue(GetPlayerId(controller), out var assignment))
            {
                continue;
            }

            var target = assignment == TeamAssignment.CounterTerrorist ? CStrikeTeam.CT : CStrikeTeam.TE;
            if (controller.Team != target)
            {
                controller.SwitchTeam(target);
                moved++;
            }
        }

        _lastBalancedRoster = roster;
        RememberCurrentTeams(controllers);

        _logger.LogInformation(
            "Applied {Strategy} to {Count} human players; moved {Moved}: CT {CtCount}/{CtRating:F2}, T {TCount}/{TRating:F2}.",
            strategy,
            controllers.Length,
            moved,
            plan.CounterTerroristCount,
            plan.CounterTerroristRating,
            plan.TerroristCount,
            plan.TerroristRating);
    }

    private TeamBalancePlan CreateRandomPlan(IReadOnlyList<PlayerBalanceCandidate> candidates)
    {
        var shuffled = candidates.ToArray();
        _random.Shuffle(shuffled);
        var currentCt = candidates.Count(
            candidate => candidate.CurrentTeam == TeamAssignment.CounterTerrorist);
        var targetCt = TeamBalancer.CalculateTargetCounterTerroristCount(
            candidates.Count,
            currentCt,
            _currentRule.CounterTerroristRatio,
            _currentRule.TerroristRatio);
        var assignments = new Dictionary<string, TeamAssignment>(StringComparer.Ordinal);
        for (var index = 0; index < shuffled.Length; index++)
        {
            assignments[shuffled[index].Id] = index < targetCt
                ? TeamAssignment.CounterTerrorist
                : TeamAssignment.Terrorist;
        }

        if (targetCt > 0
            && targetCt < shuffled.Length
            && candidates.All(candidate => assignments[candidate.Id] == candidate.CurrentTeam))
        {
            assignments[shuffled[0].Id] = TeamAssignment.Terrorist;
            assignments[shuffled[targetCt].Id] = TeamAssignment.CounterTerrorist;
        }

        var ctRating = candidates
            .Where(candidate => assignments[candidate.Id] == TeamAssignment.CounterTerrorist)
            .Sum(candidate => candidate.Rating);
        var tRating = candidates
            .Where(candidate => assignments[candidate.Id] == TeamAssignment.Terrorist)
            .Sum(candidate => candidate.Rating);
        return new TeamBalancePlan(assignments, ctRating, tRating);
    }

    public void Shutdown()
    {
        _stopping = true;
        _balanceGeneration++;
        _modeContextSubscription?.Dispose();
        _modeContextSubscription = null;
        _matchResultSubscription?.Dispose();
        _matchResultSubscription = null;
        if (_healthEventInstalled)
        {
            _events.RemoveEventListener(this);
            _healthEventInstalled = false;
        }

        SaveHealthCompensationState();
        _hooks.HandleCommandJoinTeam.RemoveHookPre(OnHandleCommandJoinTeam);
        _clients.RemoveClientListener(this);
        _modSharp.RemoveGameListener(this);
        _modeContext = null;
        _currentSelection = null;
        _ratingProvider = null;
        _matchResultSource = null;
        _adminManager = null;
        _targetingManager = null;
        _adminCommandsRegisteredWith = null;
        _lastBalancedRoster = null;
        _lastBalancedTeams.Clear();
        _initialHealthBySteamId.Clear();
        _healthAssignments.Clear();
        _healthCompensationStates.Clear();
        _hasAppliedInitialBalance = false;
    }

    private HookReturnValue<bool> OnHandleCommandJoinTeam(
        IHandleCommandJoinTeamHookParams parameters,
        HookReturnValue<bool> result)
    {
        var client = parameters.Client;
        if (!_currentRule.LockTeamSelect || !IsAutoTeamModeActive() || IsBot(client) || client.IsHltv)
        {
            return result;
        }

        var steamId = parameters.Controller.SteamId.AsPrimitive();
        var spectatorRequest = parameters.Team == (int)CStrikeTeam.Spectator;
        if (_config.ObserverWhitelistIds.Contains(steamId))
        {
            _modSharp.InvokeFrameAction(() => MoveWhitelistedClientToSpectator(client, steamId));
            return new HookReturnValue<bool>(EHookAction.SkipCallReturnOverride, false);
        }

        if (spectatorRequest)
        {
            client.Print(
                HudPrintChannel.Chat,
                FormatChatMessage("玩家不能自行进入观察者，请联系管理员调整。"));
            return new HookReturnValue<bool>(EHookAction.SkipCallReturnOverride, false);
        }

        // Preserve native warmup movement between active teams. ATL only locks the live match.
        if (_modSharp.GetGameRules().IsWarmupPeriod)
        {
            return result;
        }

        if (parameters.Controller.Team == CStrikeTeam.UnAssigned && _currentRule.AutoAssignOnJoin)
        {
            _modSharp.InvokeFrameAction(() => AssignUnassignedClient(client, skipWarmup: false));
        }

        // Existing spectators are deliberately preserved; team selection stays locked.
        return new HookReturnValue<bool>(EHookAction.SkipCallReturnOverride, false);
    }

    private void AssignUnassignedClient(IGameClient client, bool skipWarmup)
    {
        if (!client.IsValid
            || !client.IsInGame
            || !_currentRule.AutoAssignOnJoin
            || !IsAutoTeamModeActive()
            || skipWarmup && _modSharp.GetGameRules().IsWarmupPeriod)
        {
            return;
        }

        var controller = client.GetPlayerController();
        if (controller is null
            || controller.Team != CStrikeTeam.UnAssigned
            || _config.ObserverWhitelistIds.Contains(controller.SteamId.AsPrimitive()))
        {
            return;
        }

        var humans = GetHumanControllers().ToArray();
        var ctCount = humans.Count(player => player.Team == CStrikeTeam.CT);
        var tCount = humans.Count(player => player.Team == CStrikeTeam.TE);
        var ctLoad = ctCount / (double)_currentRule.CounterTerroristRatio;
        var tLoad = tCount / (double)_currentRule.TerroristRatio;
        controller.ChangeTeam(ctLoad <= tLoad ? CStrikeTeam.CT : CStrikeTeam.TE);
    }

    private void AssignAllUnassignedClients()
    {
        if (!_currentRule.AutoAssignOnJoin)
        {
            return;
        }

        foreach (var client in _clients.GetGameClients(inGame: true)
            .Where(client => !IsBot(client) && !client.IsHltv))
        {
            AssignUnassignedClient(client, skipWarmup: false);
        }
    }

    private void MoveWhitelistedClientToSpectator(IGameClient client, ulong expectedSteamId)
    {
        if (!client.IsValid
            || !client.IsInGame
            || client.SteamId.AsPrimitive() != expectedSteamId)
        {
            return;
        }

        var controller = client.GetPlayerController();
        if (controller is not null && controller.Team != CStrikeTeam.Spectator)
        {
            controller.ChangeTeam(CStrikeTeam.Spectator);
        }
    }

    private IEnumerable<IPlayerController> GetHumanControllers()
        => _clients.GetGameClients(inGame: true)
            .Where(client => !IsBot(client) && !client.IsHltv)
            .Select(client => client.GetPlayerController())
            .OfType<IPlayerController>();

    private double GetRating(IPlayerController controller)
        => GetRating(controller, out _);

    private double GetRating(IPlayerController controller, out bool overridden)
    {
        var playerId = GetPlayerId(controller);
        if (_config.RatingOverrides.TryGetValue(playerId, out var overriddenRating))
        {
            overridden = true;
            return overriddenRating;
        }

        overridden = false;
        if (_currentRule.UsePlayerDataForBalance
            && _ratingProvider?.Instance is { } provider
            && provider.TryGetRating(controller.SteamId.AsPrimitive(), out var storedRating)
            && storedRating is { HistoryCount: > 0, Rating: > 0 }
            && double.IsFinite(storedRating.Rating))
        {
            return storedRating.Rating;
        }

        return _config.DefaultRating;
    }

    private static string GetPlayerId(IPlayerController controller)
        => controller.SteamId.AsPrimitive().ToString(CultureInfo.InvariantCulture);

    private static TeamAssignment ToDomainTeam(CStrikeTeam team)
        => team switch
        {
            CStrikeTeam.CT => TeamAssignment.CounterTerrorist,
            CStrikeTeam.TE => TeamAssignment.Terrorist,
            CStrikeTeam.Spectator => TeamAssignment.Spectator,
            _ => TeamAssignment.Unassigned,
        };

    private void RefreshModeContext()
    {
        var next = _shared.GetSharpModuleManager()
            .GetOptionalSharpModuleInterface<IModeContext>(IModeContext.Identity);
        if (ReferenceEquals(_modeContext?.Instance, next?.Instance))
        {
            if (next?.Instance?.Current is { } current)
            {
                ApplyModeContext(current);
            }

            return;
        }

        _modeContextSubscription?.Dispose();
        _modeContextSubscription = null;
        _modeContext = next;
        if (next?.Instance is not { } context)
        {
            _currentSelection = null;
            RefreshCurrentRule();
            return;
        }

        _modeContextSubscription = context.Subscribe(ApplyModeContext);
        if (context.Current is { } snapshot)
        {
            ApplyModeContext(snapshot);
        }
    }

    private void RefreshPlayerDataInterfaces()
    {
        _matchResultSubscription?.Dispose();
        _matchResultSubscription = null;
        _ratingProvider = _shared.GetSharpModuleManager()
            .GetOptionalSharpModuleInterface<IPlayerRatingProvider>(IPlayerRatingProvider.Identity);
        _matchResultSource = _shared.GetSharpModuleManager()
            .GetOptionalSharpModuleInterface<IPlayerMatchResultSource>(IPlayerMatchResultSource.Identity);
        if (_matchResultSource?.Instance is { } source)
        {
            _matchResultSubscription = source.Subscribe(ApplyCompletedMatchResults);
        }

        RefreshCurrentRule();
    }

    private void ApplyModeContext(ModeContextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_currentSelection == snapshot.Selection)
        {
            return;
        }

        _currentSelection = snapshot.Selection;
        RefreshCurrentRule();
    }

    private void RefreshCurrentRule()
    {
        var previous = _currentRule;
        var selection = _currentSelection;
        var playerDataAllowed = selection is null
            || _ratingProvider?.Instance?.IsMapAllowed(selection.MapName) != false;
        _currentRule = AutoTeamRuleResolver.Resolve(
            _config.ToRuleDefaults(),
            selection?.AutoTeam,
            playerDataAllowed,
            selection?.EntryId ?? "no-context");
        if (selection is null)
        {
            _currentRule = _currentRule with { Enabled = false };
        }

        if (previous == _currentRule)
        {
            return;
        }

        _balanceGeneration++;
        _lastBalancedRoster = null;
        _lastBalancedTeams.Clear();
        _initialHealthBySteamId.Clear();
        _healthAssignments.Clear();
        _hasAppliedInitialBalance = false;
        ApplyNativeTeamBalanceControl("mode-context");
        _logger.LogInformation(
            "Activated auto-team rule {Rule}: enabled={Enabled}, CT:T={CtRatio}:{TRatio}, deviation={Deviation}, strategy={Strategy}.",
            _currentRule.RuleName,
            _currentRule.Enabled,
            _currentRule.CounterTerroristRatio,
            _currentRule.TerroristRatio,
            _currentRule.AllowedCountDeviation,
            _currentRule.RoundRandomizeMode);
    }

    private void RefreshAdminInterfaces()
    {
        _adminManager = _shared.GetSharpModuleManager()
            .GetOptionalSharpModuleInterface<IAdminManager>(IAdminManager.Identity);
        _targetingManager = _shared.GetSharpModuleManager()
            .GetOptionalSharpModuleInterface<ITargetingManager>(ITargetingManager.Identity);

        var adminManager = _adminManager?.Instance;
        if (adminManager is null)
        {
            _logger.LogWarning("Official AdminManager is unavailable; SharpGameModes force-team aliases are not registered.");
            return;
        }

        if (ReferenceEquals(_adminCommandsRegisteredWith, adminManager))
        {
            return;
        }

        try
        {
            var registry = adminManager.GetCommandRegistry(ModuleIdentity);
            ImmutableArray<string> permissions = [ForceTeamPermission];
            registry.RegisterPermissions(permissions);
            registry.RegisterAdminCommand("fct", OnForceCtCommand, permissions);
            registry.RegisterAdminCommand("ft", OnForceTCommand, permissions);
            registry.RegisterAdminCommand("f", OnForceCommand, permissions);
            registry.RegisterAdminCommand("autoteam_reload", OnReloadCommand, permissions);
            registry.RegisterAdminCommand("autoteam_status", OnStatusCommand, permissions);
            _adminCommandsRegisteredWith = adminManager;
            _logger.LogInformation("Registered force-team aliases through official AdminManager permission {Permission}.", ForceTeamPermission);
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogWarning(exception, "Could not register force-team aliases because CommandCenter is unavailable.");
        }
    }

    private void OnForceCtCommand(IGameClient? issuer, StringCommand command)
        => ForceTargetsToTeam(issuer, command, CStrikeTeam.CT);

    private void OnForceTCommand(IGameClient? issuer, StringCommand command)
        => ForceTargetsToTeam(issuer, command, CStrikeTeam.TE);

    private void OnForceCommand(IGameClient? issuer, StringCommand command)
    {
        if (command.ArgCount < 2)
        {
            Reply(issuer, command, "用法：ms_f <玩家|@all|@ct|@t|@spec> <ct|t|spec>");
            return;
        }

        if (!TryParseTeam(command.GetArg(2), out var team))
        {
            Reply(issuer, command, "队伍只能是 ct、t 或 spec。");
            return;
        }

        ForceTargetsToTeam(issuer, command, team);
    }

    private void OnReloadCommand(IGameClient? issuer, StringCommand command)
    {
        try
        {
            var replacement = JsonSerializer.Deserialize<AutoTeamConfig>(
                File.ReadAllText(_configPath),
                SerializerOptions) ?? throw new InvalidDataException("Auto-team config is empty.");
            replacement.Validate();

            SaveHealthCompensationState();
            _config = replacement;
            _healthCompensationStatePath = Path.IsPathRooted(_config.HealthCompensationStatePath)
                ? _config.HealthCompensationStatePath
                : Path.Combine(_sharpPath, _config.HealthCompensationStatePath);
            LoadHealthCompensationState();
            try
            {
                _ratingProvider?.Instance?.Reload();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Auto-team config reloaded, but the player rating cache could not be refreshed.");
            }

            RefreshCurrentRule();

            foreach (var client in _clients.GetGameClients(inGame: true)
                .Where(client => !IsBot(client) && !client.IsHltv))
            {
                var steamId = client.SteamId.AsPrimitive();
                if (_config.ObserverWhitelistIds.Contains(steamId))
                {
                    MoveWhitelistedClientToSpectator(client, steamId);
                }
            }

            Reply(
                issuer,
                command,
                $"配置已重载：规则={_currentRule.RuleName}，CT:T={_currentRule.CounterTerroristRatio}:{_currentRule.TerroristRatio}，模式={_currentRule.RoundRandomizeMode}。");
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or InvalidDataException or ArgumentException)
        {
            _logger.LogError(exception, "Failed to reload auto-team config from {Path}.", _configPath);
            Reply(issuer, command, $"配置重载失败，继续使用旧配置：{exception.Message}");
        }
    }

    private void OnStatusCommand(IGameClient? issuer, StringCommand command)
    {
        var selection = _currentSelection;
        Reply(
            issuer,
            command,
            $"地图={selection?.MapName ?? "未同步"}，模式={selection?.Mode.Value ?? "未同步"}，规则={_currentRule.RuleName}，启用={_currentRule.Enabled}，CT:T={_currentRule.CounterTerroristRatio}:{_currentRule.TerroristRatio}，允许偏差={_currentRule.AllowedCountDeviation}，分队={_currentRule.RoundRandomizeMode}。");
    }

    private void ForceTargetsToTeam(IGameClient? issuer, StringCommand command, CStrikeTeam team)
    {
        if (command.ArgCount < 1)
        {
            Reply(issuer, command, $"用法：ms_{command.CommandName} <玩家|@all|@ct|@t|@spec>");
            return;
        }

        var targeting = _targetingManager?.Instance;
        if (targeting is null)
        {
            Reply(issuer, command, "官方 TargetingManager 未加载，无法解析目标玩家。");
            return;
        }

        var targets = targeting.GetByTarget(issuer, command.GetArg(1))
            .Where(client => client.IsValid && client.IsInGame)
            .DistinctBy(client => client.SteamId)
            .ToArray();
        if (targets.Length == 0)
        {
            Reply(issuer, command, "没有找到目标玩家，或目标不唯一。");
            return;
        }

        var changed = 0;
        foreach (var target in targets)
        {
            var controller = target.GetPlayerController();
            if (controller is null)
            {
                continue;
            }

            if (team == CStrikeTeam.Spectator)
            {
                controller.ChangeTeam(team);
            }
            else
            {
                controller.SwitchTeam(team);
            }

            if (!IsBot(target) && !target.IsHltv)
            {
                target.Print(
                    HudPrintChannel.Chat,
                    FormatChatMessage($"管理员已将你调整到 {TeamName(team)}。"));
            }

            changed++;
        }

        _lastBalancedRoster = null;
        _lastBalancedTeams.Clear();
        Reply(issuer, command, $"已将 {changed} 名玩家调整到 {TeamName(team)}。");
    }

    private bool IsCompleteTeamSwitch(
        IReadOnlyCollection<IPlayerController> controllers,
        IReadOnlyCollection<string> roster)
    {
        if (_lastBalancedRoster is null
            || !_lastBalancedTeams.Any()
            || !roster.ToHashSet(StringComparer.Ordinal).SetEquals(_lastBalancedRoster))
        {
            return false;
        }

        foreach (var controller in controllers)
        {
            if (!_lastBalancedTeams.TryGetValue(GetPlayerId(controller), out var previous)
                || previous switch
                {
                    TeamAssignment.CounterTerrorist => controller.Team != CStrikeTeam.TE,
                    TeamAssignment.Terrorist => controller.Team != CStrikeTeam.CT,
                    _ => true,
                })
            {
                return false;
            }
        }

        return controllers.Count > 0;
    }

    private void RememberCurrentTeams(IEnumerable<IPlayerController> controllers)
    {
        _lastBalancedTeams.Clear();
        foreach (var controller in controllers)
        {
            _lastBalancedTeams[GetPlayerId(controller)] = ToDomainTeam(controller.Team);
        }
    }

    private void Reply(IGameClient? issuer, StringCommand command, string message)
    {
        var formatted = FormatChatMessage(message);
        if (issuer is null)
        {
            _logger.LogInformation("{Message}", formatted);
        }
        else if (command.ChatTrigger)
        {
            issuer.Print(HudPrintChannel.Chat, formatted);
        }
        else
        {
            issuer.ConsolePrint(formatted);
        }
    }

    private static bool TryParseTeam(string value, out CStrikeTeam team)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "ct":
            case "3":
            case "counterterrorist":
            case "counter-terrorist":
                team = CStrikeTeam.CT;
                return true;
            case "t":
            case "2":
            case "terrorist":
                team = CStrikeTeam.TE;
                return true;
            case "spec":
            case "spectator":
            case "obs":
            case "1":
                team = CStrikeTeam.Spectator;
                return true;
            default:
                team = CStrikeTeam.UnAssigned;
                return false;
        }
    }

    private static string TeamName(CStrikeTeam team)
        => team switch
        {
            CStrikeTeam.CT => "CT",
            CStrikeTeam.TE => "T",
            CStrikeTeam.Spectator => "观察者",
            _ => "未分配",
        };

    private string FormatChatMessage(string message)
    {
        var prefix = _config.Prefix?.Trim();
        return string.IsNullOrEmpty(prefix) ? message : $"{prefix} {message}";
    }

    private static bool IsLibrary(string actualName, string shortName)
        => actualName.Equals(shortName, StringComparison.OrdinalIgnoreCase)
            || actualName.EndsWith($".{shortName}", StringComparison.OrdinalIgnoreCase);

    private void ApplyRatingBalancedHealth(IPlayerController controller)
    {
        var pawn = controller.GetPlayerPawn();
        if (pawn is not { IsAlive: true })
        {
            return;
        }

        var health = GetInitialHealth(controller);
        pawn.MaxHealth = health;
        pawn.Health = health;
    }

    private int GetInitialHealth(IPlayerController controller)
    {
        var steamId = controller.SteamId.AsPrimitive();
        if (_initialHealthBySteamId.TryGetValue(steamId, out var cachedHealth))
        {
            return cachedHealth;
        }

        var rating = GetRating(controller, out var ratingOverridden);
        if (_config.HealthCompensationBlacklistIds.Contains(steamId))
        {
            _healthAssignments.Remove(steamId);
            _initialHealthBySteamId[steamId] = 100;
            controller.Print(
                HudPrintChannel.Chat,
                FormatChatMessage($"你的平衡 rating：{rating:F2}，本图初始血量：100。"));
            return 100;
        }

        _healthCompensationStates.TryGetValue(steamId, out var savedState);
        var decision = LowRatingHealthCompensator.Assign(
            rating,
            ratingOverridden,
            savedState,
            _config.LowRatingHealthCompensation.ToDomain());
        if (decision.State is null)
        {
            _healthCompensationStates.Remove(steamId);
        }
        else
        {
            _healthCompensationStates[steamId] = decision.State;
        }

        if (decision.Assignment is null)
        {
            _healthAssignments.Remove(steamId);
        }
        else
        {
            _healthAssignments[steamId] = decision.Assignment;
        }

        _initialHealthBySteamId[steamId] = decision.Health;
        if (decision.StateChanged)
        {
            SaveHealthCompensationState();
        }

        var ratingLabel = decision.Assignment is null ? "平衡 rating" : "基础 rating";
        controller.Print(
            HudPrintChannel.Chat,
            FormatChatMessage(
                $"你的{ratingLabel}：{decision.DisplayRating:F2}，本图初始血量：{decision.Health}。"));
        _logger.LogInformation(
            "Locked initial health for {PlayerName} ({SteamId}) at {Health} from rating {Rating:F3}; adaptive={Adaptive}.",
            controller.PlayerName,
            steamId,
            decision.Health,
            decision.DisplayRating,
            decision.Assignment is not null);
        return decision.Health;
    }

    private void ApplyCompletedMatchResults(IReadOnlyList<PlayerMatchResultSnapshot> results)
    {
        if (!_currentRule.BalanceHealthByRating || results.Count == 0)
        {
            return;
        }

        var policy = _config.LowRatingHealthCompensation.ToDomain();
        var stateChanged = false;
        foreach (var result in results)
        {
            if (!_healthAssignments.TryGetValue(result.SteamId, out var assignment)
                || _config.HealthCompensationBlacklistIds.Contains(result.SteamId))
            {
                continue;
            }

            _healthCompensationStates.TryGetValue(result.SteamId, out var state);
            var decision = LowRatingHealthCompensator.ApplyFeedback(
                assignment,
                state,
                result.Rating,
                result.RoundsPlayed,
                policy);
            if (decision.State is null)
            {
                _healthCompensationStates.Remove(result.SteamId);
            }
            else
            {
                _healthCompensationStates[result.SteamId] = decision.State;
            }

            stateChanged |= decision.Changed;
            _logger.LogInformation(
                "Updated health compensation for {PlayerName} ({SteamId}): match rating {Rating:F3}, next health {NextHealth}.",
                result.PlayerName,
                result.SteamId,
                result.Rating,
                decision.State?.CurrentHealth ?? 100);
        }

        if (stateChanged)
        {
            SaveHealthCompensationState();
        }
    }

    private void LoadHealthCompensationState()
    {
        _healthCompensationStates.Clear();
        if (string.IsNullOrWhiteSpace(_healthCompensationStatePath)
            || !File.Exists(_healthCompensationStatePath))
        {
            return;
        }

        try
        {
            var store = JsonSerializer.Deserialize<HealthCompensationStateStore>(
                File.ReadAllText(_healthCompensationStatePath),
                SerializerOptions) ?? new HealthCompensationStateStore();
            store.Players ??= new Dictionary<string, HealthCompensationState>(StringComparer.Ordinal);
            var policy = _config.LowRatingHealthCompensation.ToDomain();
            foreach (var (steamIdText, state) in store.Players)
            {
                if (!ulong.TryParse(steamIdText, out var steamId))
                {
                    continue;
                }

                try
                {
                    _healthCompensationStates[steamId] = LowRatingHealthCompensator.NormalizeState(state, policy);
                }
                catch (ArgumentException)
                {
                    // Ignore malformed or obsolete player entries without discarding the valid state file.
                }
            }

            _logger.LogInformation(
                "Loaded {Count} health compensation states from {Path}.",
                _healthCompensationStates.Count,
                _healthCompensationStatePath);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            _healthCompensationStates.Clear();
            _logger.LogError(exception, "Failed to load health compensation state from {Path}.", _healthCompensationStatePath);
        }
    }

    private void SaveHealthCompensationState()
    {
        if (string.IsNullOrWhiteSpace(_healthCompensationStatePath))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_healthCompensationStatePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var store = new HealthCompensationStateStore
            {
                LastUpdatedUtc = DateTimeOffset.UtcNow,
                Players = _healthCompensationStates.ToDictionary(
                    entry => entry.Key.ToString(CultureInfo.InvariantCulture),
                    entry => entry.Value,
                    StringComparer.Ordinal),
            };
            var temporaryPath = $"{_healthCompensationStatePath}.tmp";
            File.WriteAllText(temporaryPath, $"{JsonSerializer.Serialize(store, SerializerOptions)}{Environment.NewLine}");
            File.Move(temporaryPath, _healthCompensationStatePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "Failed to save health compensation state to {Path}.", _healthCompensationStatePath);
        }
    }

    private static bool IsActiveHuman([NotNullWhen(true)] IPlayerController? controller)
        => controller is { IsHltv: false }
            && !BotIdentityRegistry.IsBot(
                controller.IsFakeClient,
                controller.PlayerSlot.AsPrimitive())
            && controller.Team is CStrikeTeam.CT or CStrikeTeam.TE
            && controller.SteamId.AsPrimitive() != 0;

    private static bool IsBot(IGameClient client)
        => BotIdentityRegistry.IsBot(client.IsFakeClient, client.Slot.AsPrimitive());

    private bool IsAutoTeamModeActive()
        => _currentSelection is not null && _currentRule.Enabled;

    private void ApplyNativeTeamBalanceControl(string source)
    {
        if (!_currentRule.Enabled || !_currentRule.DisableNativeTeamBalance)
        {
            return;
        }

        _modSharp.ServerCommand("mp_autoteambalance 0");
        _modSharp.ServerCommand("mp_limitteams 0");
        if (_currentRule.CounterTerroristRatio != _currentRule.TerroristRatio)
        {
            _modSharp.ServerCommand("mp_halftime 0");
        }

        _logger.LogDebug("Disabled native team balancing for {Source}.", source);
    }
}
