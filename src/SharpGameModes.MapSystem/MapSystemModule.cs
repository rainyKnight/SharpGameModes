using System.Text.Json;
using System.Text.RegularExpressions;
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

namespace SharpGameModes.MapSystem;

public sealed class MapSystemModule : IModSharpModule, IGameListener, IClientListener, IEventListener
{
    private static readonly Regex SafeMapName = new("^[A-Za-z0-9_./-]+$", RegexOptions.CultureInvariant);
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
    private readonly ILogger<MapSystemModule> _logger;
    private readonly string _sharpPath;
    private readonly string _poolDirectory;
    private readonly string _serverConfigPath;
    private readonly string _mapSystemConfigPath;
    private readonly Random _random = new();
    private readonly Dictionary<ulong, string> _nominations = [];
    private readonly RtvTracker _rtv = new();
    private readonly MapPanelController _panels;
    private ServerConfig _serverConfig = new();
    private MapSystemConfig _config = new();
    private MapSystemState _state = new();
    private MapCatalog? _catalog;
    private string? _statePath;
    private string? _runtimeMapName;
    private MapVoteSession? _vote;
    private DateTimeOffset? _voteEndsAt;
    private IModSharpModuleInterface<IModeContext>? _modeContext;
    private DateTimeOffset _rtvAvailableAt;
    private bool _automaticVoteStarted;
    private bool _changeAfterVoteCompletes;
    private bool _immediateRtvArmed;
    private bool _mapChangeScheduled;
    private bool _mapChangeStarted;
    private bool _timedAutoChangeScheduled;
    private bool _stopping;
    private int _lifecycleGeneration;

    public MapSystemModule(
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
        _logger = sharedSystem.GetLoggerFactory().CreateLogger<MapSystemModule>();
        _sharpPath = sharpPath;
        _poolDirectory = Path.Combine(sharpPath, "configs", "sharp-gamemodes", "map-pools");
        _serverConfigPath = Path.Combine(sharpPath, "configs", "sharp-gamemodes", "server.jsonc");
        _mapSystemConfigPath = Path.Combine(sharpPath, "configs", "sharp-gamemodes", "map-system.jsonc");
        _panels = new MapPanelController(
            HandlePanelSelection,
            () => _vote?.GetCounts() ?? new Dictionary<string, int>(),
            steamId => _vote?.GetVote(steamId));
    }

    public string DisplayName => "SharpGameModes Map System";
    public string DisplayAuthor => "SharpGameModes Contributors";
    public int ListenerVersion => IGameListener.ApiVersion;
    public int ListenerPriority => 50;

    public bool Init()
    {
        if (!TryLoadConfiguration())
        {
            return false;
        }

        _modSharp.InstallGameListener(this);
        _clients.InstallClientListener(this);
        InstallCommands();
        _hooks.PlayerRunCommand.InstallHookPre(_panels.OnPlayerRunCommand, ListenerPriority);
        _events.InstallEventListener(this);
        _events.HookEvent("round_start");
        _events.HookEvent("player_team");
        _events.HookEvent("player_death");
        _events.HookEvent("cs_win_panel_match");
        return true;
    }

    public void OnAllModulesLoaded()
        => RefreshModeContext();

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
        ResetMapRuntime();
        // ModSharp reloads changed modules before this map-lifecycle callback.
        _state.CurrentMapStartedAtUtc = DateTimeOffset.UtcNow;
        PublishCurrentMap("game-init");
    }

    public void OnServerSpawn()
        => ApplyCurrentModeConfig();

    public void OnRoundRestart()
        => ApplyCurrentModeConfig();

    private void ApplyCurrentModeConfig()
    {
        var mode = CurrentMode();
        if (mode != ModeId.Classic
            && mode != ModeId.TeamDeathmatch
            && mode != ModeId.Zombie
            && mode != ModeId.BotMatch)
        {
            return;
        }

        _modSharp.ServerCommand("exec sharp-gamemodes/baseline.cfg");
        _modSharp.ServerCommand(mode == ModeId.Classic
            ? "exec sharp-gamemodes/classic.cfg"
            : mode == ModeId.TeamDeathmatch
                ? "exec sharp-gamemodes/tdm.cfg"
                : mode == ModeId.Zombie
                    ? "exec sharp-gamemodes/zombie.cfg"
                    : "exec sharp-gamemodes/botmatch.cfg");
        if (mode == ModeId.Classic)
        {
            _modSharp.ServerCommand("exec sharp-gamemodes/classic-bots.cfg");
        }
        else if (mode != ModeId.BotMatch)
        {
            _modSharp.ServerCommand("exec sharp-gamemodes/no-bots.cfg");
        }

        ApplyWarmupRespawnPolicy();
    }

    public void OnClientPutInServer(IGameClient client)
    {
        ScheduleWarmupRespawn(client, 0.8);

        if (_vote is null || !IsHuman(client))
        {
            return;
        }

        var generation = _lifecycleGeneration;
        _modSharp.PushTimer(
            () =>
            {
                if (!_stopping && generation == _lifecycleGeneration && _vote is not null && IsHuman(client))
                {
                    OpenVotePanel(client);
                }
            },
            1,
            GameTimerFlags.StopOnMapEnd);
    }

    public void OnClientDisconnecting(IGameClient client, NetworkDisconnectionReason reason)
    {
        var steamId = client.SteamId.AsPrimitive();
        _vote?.Revoke(steamId);
        _nominations.Remove(steamId);
        _rtv.Remove(steamId);
        _panels.Forget(client);
    }

    public ECommandAction OnClientSayCommand(
        IGameClient client,
        bool teamOnly,
        bool isCommand,
        string commandName,
        string message)
    {
        if (!IsHuman(client) || string.IsNullOrWhiteSpace(message))
        {
            return ECommandAction.Skipped;
        }

        var text = message.Trim().Trim('"');
        var normalized = text.TrimStart('!', '！', '/').Trim();

        if (isCommand)
        {
            return ECommandAction.Skipped;
        }

        if (int.TryParse(normalized, out var visibleNumber)
            && _panels.HandleChatNumber(client, visibleNumber))
        {
            return ECommandAction.Handled;
        }

        var separator = normalized.IndexOf(' ');
        var alias = separator < 0 ? normalized : normalized[..separator];
        var argument = separator < 0 ? string.Empty : normalized[(separator + 1)..].Trim();
        switch (alias.ToLowerInvariant())
        {
            case "rtv":
                AttemptRtv(client);
                return ECommandAction.Handled;
            case "yd":
            case "nominate":
                AttemptNomination(client, argument);
                return ECommandAction.Handled;
            case "ydc":
                CancelNomination(client);
                return ECommandAction.Handled;
            case "revote":
                ReopenVote(client);
                return ECommandAction.Handled;
            case "nextmap":
                PrintNextMap(client);
                return ECommandAction.Handled;
            case "maps":
                OpenMapList(client);
                return ECommandAction.Handled;
            default:
                return ECommandAction.Skipped;
        }
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
                case "player_team" when gameEvent is IEventPlayerTeam team:
                    ScheduleWarmupRespawn(team.Controller, 0.25);
                    break;
                case "player_death" when gameEvent is IEventPlayerDeath death:
                    ScheduleWarmupRespawn(death.VictimController, 0.8);
                    break;
                case "cs_win_panel_match":
                    OnMatchEnd();
                    break;
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to process map-system event {EventName}.", gameEvent.Name);
        }
    }

    public void Shutdown()
    {
        _stopping = true;
        _lifecycleGeneration++;
        SaveState();
        _events.RemoveEventListener(this);
        _hooks.PlayerRunCommand.RemoveHookPre(_panels.OnPlayerRunCommand);
        RemoveCommands();
        _clients.RemoveClientListener(this);
        _modSharp.RemoveGameListener(this);
        _modeContext = null;
        _vote = null;
        _voteEndsAt = null;
        _nominations.Clear();
        _rtv.Clear();
        _panels.CloseAll();
    }

    private bool TryLoadConfiguration()
    {
        try
        {
            _serverConfig = Deserialize<ServerConfig>(_serverConfigPath, "Server config");
            _serverConfig.Validate();
            _config = Deserialize<MapSystemConfig>(_mapSystemConfigPath, "Map-system config");
            _config.Validate();

            var catalogs = new List<MapCatalog>();
            foreach (var mode in _serverConfig.GetEnabledModeIds())
            {
                var path = Path.Combine(_poolDirectory, $"{mode.Value}.jsonc");
                var catalog = MapCatalog.Load(path);
                if (catalog.Entries.Any(entry => entry.Mode != mode))
                {
                    throw new InvalidDataException($"Map pool '{path}' does not declare mode '{mode}'.");
                }

                catalogs.Add(catalog);
                _logger.LogInformation("Loaded {Count} {Mode} map entries from {Path}.", catalog.Entries.Count, mode, path);
            }

            _catalog = MapCatalog.Combine(catalogs);
            _statePath = Path.IsPathRooted(_config.StatePath)
                ? _config.StatePath
                : Path.Combine(_sharpPath, _config.StatePath);
            LoadState();
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or ArgumentException)
        {
            _logger.LogError(exception, "Failed to initialize SharpGameModes map configuration.");
            return false;
        }
    }

    private static T Deserialize<T>(string path, string label)
        where T : class
        => JsonSerializer.Deserialize<T>(File.ReadAllText(path), SerializerOptions)
            ?? throw new InvalidDataException($"{label} is empty.");

    private void LoadState()
    {
        if (_statePath is null || !File.Exists(_statePath))
        {
            _state = new MapSystemState();
            return;
        }

        try
        {
            _state = JsonSerializer.Deserialize<MapSystemState>(File.ReadAllText(_statePath), SerializerOptions)
                ?? new MapSystemState();
            if (_state.SchemaVersion != 1)
            {
                throw new InvalidDataException($"Unsupported map state schema_version {_state.SchemaVersion}.");
            }

            _state.RecentEntryIds ??= [];
            _state.RecentEntryIds = _state.RecentEntryIds
                .Where(id => _catalog?.ResolveEntryId(id) is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(_config.Vote.RememberPlayedMaps)
                .ToList();
            if (_state.CurrentEntryId is not null && _catalog?.ResolveEntryId(_state.CurrentEntryId) is null)
            {
                _state.CurrentEntryId = null;
                _state.CurrentMapStartedAtUtc = null;
            }

            if (_state.NextEntryId is not null && _catalog?.ResolveEntryId(_state.NextEntryId) is null)
            {
                _state.NextEntryId = null;
            }

            if (_state.PendingActivationEntryId is not null
                && _catalog?.ResolveEntryId(_state.PendingActivationEntryId) is null)
            {
                _state.PendingActivationEntryId = null;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            _logger.LogWarning(exception, "Ignoring invalid map-system state at {Path}.", _statePath);
            _state = new MapSystemState();
        }
    }

    private void SaveState()
    {
        if (_statePath is null)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_statePath)
                ?? throw new InvalidDataException("Map-system state path has no parent directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = $"{_statePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_state, SerializerOptions));
            File.Move(temporaryPath, _statePath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.LogWarning(exception, "Failed to persist map-system state to {Path}.", _statePath);
        }
    }

    private void RefreshModeContext()
    {
        _modeContext = _shared.GetSharpModuleManager()
            .GetOptionalSharpModuleInterface<IModeContext>(IModeContext.Identity);
    }

    private void PublishCurrentMap(string source)
    {
        var context = _modeContext?.Instance;
        var mapName = _modSharp.GetMapName();
        if (context is null || string.IsNullOrWhiteSpace(mapName) || _catalog is null)
        {
            return;
        }

        var previous = _state.CurrentEntryId is null ? null : _catalog.ResolveEntryId(_state.CurrentEntryId);
        var pending = _state.PendingActivationEntryId is null
            ? null
            : _catalog.ResolveEntryId(_state.PendingActivationEntryId);
        MapPoolEntry? entry;
        var activatedPending = pending is not null && MapNamesMatch(pending.MapName, mapName);
        if (activatedPending)
        {
            entry = pending;
            _state.PendingActivationEntryId = null;
            _state.NextEntryId = null;
        }
        else if (previous is not null && MapNamesMatch(previous.MapName, mapName))
        {
            entry = previous;
        }
        else
        {
            if (previous is not null || _state.PendingActivationEntryId is not null)
            {
                _state.NextEntryId = null;
                _state.PendingActivationEntryId = null;
            }

            entry = _catalog.ResolvePhysicalMap(mapName, _serverConfig.DefaultModeId)
                ?? _catalog.ResolvePhysicalMap(mapName);
        }

        var selection = entry?.ToSelection()
            ?? new MapSelection(
                $"{_serverConfig.DefaultModeId.Value}:{mapName.ToLowerInvariant()}",
                _serverConfig.DefaultModeId,
                mapName,
                mapName,
                false,
                null);
        var mapChanged = !string.Equals(_state.CurrentEntryId, selection.EntryId, StringComparison.OrdinalIgnoreCase);
        _state.CurrentEntryId = selection.EntryId;
        if (mapChanged || _state.CurrentMapStartedAtUtc is null)
        {
            _state.CurrentMapStartedAtUtc = DateTimeOffset.UtcNow;
        }

        RememberCurrentEntry(selection.EntryId);
        SaveState();
        _runtimeMapName = mapName;
        _rtvAvailableAt = _state.CurrentMapStartedAtUtc.Value.AddSeconds(_config.Rtv.InitialDelaySeconds);
        _automaticVoteStarted = _state.NextEntryId is not null;

        var snapshot = context.Activate(selection, source);
        _logger.LogInformation(
            "Activated entry {EntryId}, mode {Mode}, map {Map} at generation {Generation}.",
            selection.EntryId,
            snapshot.Selection.Mode,
            snapshot.Selection.MapName,
            snapshot.Generation);
        ScheduleTimedAutoChange(selection.Mode);
    }

    private void RememberCurrentEntry(string entryId)
    {
        _state.RecentEntryIds.RemoveAll(id => id.Equals(entryId, StringComparison.OrdinalIgnoreCase));
        if (_config.Vote.RememberPlayedMaps > 0)
        {
            _state.RecentEntryIds.Insert(0, entryId);
        }

        while (_state.RecentEntryIds.Count > _config.Vote.RememberPlayedMaps)
        {
            _state.RecentEntryIds.RemoveAt(_state.RecentEntryIds.Count - 1);
        }
    }

    private void ResetMapRuntime()
    {
        _lifecycleGeneration++;
        _vote = null;
        _voteEndsAt = null;
        _nominations.Clear();
        _rtv.Clear();
        _panels.CloseAll();
        _automaticVoteStarted = false;
        _changeAfterVoteCompletes = false;
        _immediateRtvArmed = false;
        _mapChangeScheduled = false;
        _mapChangeStarted = false;
        _timedAutoChangeScheduled = false;
    }

    private void ScheduleTimedAutoChange(ModeId mode)
    {
        if (_timedAutoChangeScheduled || _state.CurrentMapStartedAtUtc is null)
        {
            return;
        }

        var rule = _config.GetAutoChangeRule(mode);
        if (rule is not { Enabled: true }
            || !rule.AutoChangeMode.Equals("timed", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _timedAutoChangeScheduled = true;
        var elapsedSeconds = Math.Max(0, (DateTimeOffset.UtcNow - _state.CurrentMapStartedAtUtc.Value).TotalSeconds);
        var voteDelay = Math.Max(1, (rule.VoteStartMinutes * 60) - elapsedSeconds);
        var changeDelay = Math.Max(1, (rule.ChangeAfterMinutes * 60) - elapsedSeconds);
        var generation = _lifecycleGeneration;

        _modSharp.PushTimer(
            () =>
            {
                if (_stopping || generation != _lifecycleGeneration || _mapChangeStarted
                    || _automaticVoteStarted || _state.NextEntryId is not null
                    || CurrentMode() != mode || _modSharp.GetGameRules().IsWarmupPeriod)
                {
                    return;
                }

                _automaticVoteStarted = true;
                Broadcast("地图计时已到，开启下一张地图投票；投票结果将在本图结束后执行。");
                StartMapVote("计时自动触发");
            },
            voteDelay,
            GameTimerFlags.StopOnMapEnd);

        _modSharp.PushTimer(
            () =>
            {
                if (_stopping || generation != _lifecycleGeneration || _mapChangeStarted || CurrentMode() != mode)
                {
                    return;
                }

                if (_vote is not null)
                {
                    _changeAfterVoteCompletes = true;
                    Broadcast("地图时间已到，等待当前投票完成后换图。");
                    return;
                }

                EnsureNextMap();
                ScheduleMapChange("计时自动换图");
            },
            changeDelay,
            GameTimerFlags.StopOnMapEnd);
    }

    private void OnRoundStart()
    {
        ApplyWarmupRespawnPolicy();

        if (!_config.Enabled || _mapChangeStarted)
        {
            return;
        }

        var rules = _modSharp.GetGameRules();
        if (rules.IsWarmupPeriod || CurrentMode() is not { } mode)
        {
            return;
        }

        var rule = _config.GetAutoChangeRule(mode);
        if (rule is not { Enabled: true }
            || (!rule.AutoChangeMode.Equals("rounds", StringComparison.OrdinalIgnoreCase)
                && !rule.AutoChangeMode.Equals("rounds_sum", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var completedRounds = rules.TotalRoundsPlayed;
        var roundNumber = completedRounds + 1;
        var action = RoundAutoChangePolicy.Evaluate(
            completedRounds,
            rule.VoteStartRound,
            rule.AutoChangeMode.Equals("rounds_sum", StringComparison.OrdinalIgnoreCase)
                ? rule.ChangeAfterRound
                : 0,
            _vote is not null || _state.NextEntryId is not null || _automaticVoteStarted);
        if (action == RoundAutoChangeAction.ChangeMap)
        {
            if (_vote is not null)
            {
                _changeAfterVoteCompletes = true;
                Broadcast($"已完成 {completedRounds} 回合，等待当前地图投票完成后换图。");
                return;
            }

            EnsureNextMap();
            ScheduleMapChange($"完成 {completedRounds} 回合后自动换图");
            return;
        }

        if (action != RoundAutoChangeAction.StartVote)
        {
            return;
        }

        _automaticVoteStarted = true;
        Broadcast($"第 {roundNumber} 回合开始，开启下一张地图投票；结果将在整场结束后执行。");
        StartMapVote("自动触发");
    }

    private void ApplyWarmupRespawnPolicy()
    {
        if (CurrentMode() is not { } mode
            || (mode != ModeId.Classic && mode != ModeId.TeamDeathmatch))
        {
            return;
        }

        var warmup = IsWarmup();
        var enabled = warmup ? 1 : 0;
        _modSharp.ServerCommand($"mp_respawn_on_death_ct {enabled}");
        _modSharp.ServerCommand($"mp_respawn_on_death_t {enabled}");
        if (!warmup)
        {
            return;
        }

        foreach (var client in _clients.GetGameClients(inGame: true).Where(IsHuman))
        {
            ScheduleWarmupRespawn(client, 0.2);
        }
    }

    private void ScheduleWarmupRespawn(IGameClient client, double delaySeconds)
    {
        if (!IsHuman(client))
        {
            return;
        }

        var generation = _lifecycleGeneration;
        _modSharp.PushTimer(
            () =>
            {
                if (!_stopping && generation == _lifecycleGeneration && IsHuman(client))
                {
                    EnsureWarmupRespawn(client.GetPlayerController());
                }
            },
            delaySeconds,
            GameTimerFlags.StopOnMapEnd);
    }

    private void ScheduleWarmupRespawn(IPlayerController? controller, double delaySeconds)
    {
        if (controller?.GetGameClient() is { } client)
        {
            ScheduleWarmupRespawn(client, delaySeconds);
        }
    }

    private void EnsureWarmupRespawn(IPlayerController? controller)
    {
        if (CurrentMode() is not { } mode
            || (mode != ModeId.Classic && mode != ModeId.TeamDeathmatch)
            || !IsWarmup()
            || controller?.Team is not (CStrikeTeam.CT or CStrikeTeam.TE)
            || controller.GetPlayerPawn() is { IsAlive: true, Health: > 0 })
        {
            return;
        }

        controller.Respawn();
    }

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

    private void OnMatchEnd()
    {
        if (!_config.Enabled || _mapChangeStarted)
        {
            return;
        }

        if (_vote is not null)
        {
            _changeAfterVoteCompletes = true;
            Broadcast("比赛已经结束，等待地图投票完成。");
            return;
        }

        EnsureNextMap();
        ScheduleMapChange("正常比赛结束");
    }

    private void StartMapVote(string reason)
    {
        if (_vote is not null || _state.NextEntryId is not null || _mapChangeStarted || _catalog is null)
        {
            return;
        }

        var candidates = MapCandidateSelector.Select(
            GetAvailableEntries(),
            _state.CurrentEntryId,
            _state.RecentEntryIds,
            _nominations.Values,
            _config.Vote.MapsInVote,
            _random);
        if (candidates.Count == 0)
        {
            Broadcast("没有可用的候选地图。");
            return;
        }

        var vote = new MapVoteSession(candidates);
        _vote = vote;
        _panels.CloseAll(MapPanelMode.Nomination);
        var voteEndsAt = DateTimeOffset.UtcNow.AddSeconds(_config.Vote.DurationSeconds);
        _voteEndsAt = voteEndsAt;
        foreach (var client in _clients.GetGameClients(inGame: true).Where(IsHuman))
        {
            _panels.OpenMaps(client, $"选择下一张地图 - {reason}", candidates, MapPanelMode.Vote, voteEndsAt);
        }

        var generation = _lifecycleGeneration;
        _modSharp.PushTimer(
            () =>
            {
                if (!_stopping && generation == _lifecycleGeneration && ReferenceEquals(_vote, vote))
                {
                    FinishMapVote(vote);
                }
            },
            _config.Vote.DurationSeconds,
            GameTimerFlags.StopOnMapEnd);
    }

    private void FinishMapVote(MapVoteSession vote)
    {
        if (!ReferenceEquals(_vote, vote))
        {
            return;
        }

        var hadVotes = vote.VoteCount > 0;
        var winner = vote.SelectWinner(_random);
        _vote = null;
        _voteEndsAt = null;
        _panels.CloseAll(MapPanelMode.Vote);
        _nominations.Clear();
        SelectNextMap(winner, hadVotes ? "投票结果" : "无人投票，随机选择");
        _rtvAvailableAt = DateTimeOffset.UtcNow.AddSeconds(_config.Rtv.CooldownSecondsAfterVote);
        _rtv.Clear();
        if (_changeAfterVoteCompletes)
        {
            _changeAfterVoteCompletes = false;
            BeginMapChange("比赛结束后的投票结果");
        }
    }

    private void CastVote(IGameClient client, MapPoolEntry entry)
    {
        if (_vote is null)
        {
            Print(client, "当前没有地图投票。");
            return;
        }

        var optionNumber = Array.FindIndex(
            _vote.Candidates.ToArray(),
            candidate => candidate.EntryId.Equals(entry.EntryId, StringComparison.OrdinalIgnoreCase)) + 1;
        if (optionNumber <= 0)
        {
            Print(client, "这个候选项已经失效。");
            return;
        }

        switch (_vote.Cast(client.SteamId.AsPrimitive(), entry.EntryId))
        {
            case MapVoteCastResult.Accepted:
                Broadcast($"{client.Name} 投给了 {optionNumber}. {MapEntryDisplay.Format(entry)}。");
                break;
            case MapVoteCastResult.AlreadyVotedSame:
                Print(client, $"你已经投给了 {MapEntryDisplay.Format(entry)}。");
                break;
            case MapVoteCastResult.MustRevokeFirst:
                Print(client, "你已经投过票；请先使用 revote 撤销，再重新选择。");
                break;
            case MapVoteCastResult.InvalidCandidate:
                Print(client, "这个候选项已经失效。");
                break;
        }
    }

    private void ReopenVote(IGameClient client)
    {
        if (_vote is null)
        {
            Print(client, "当前没有地图投票。");
            return;
        }

        var removed = _vote.Revoke(client.SteamId.AsPrimitive());
        OpenVotePanel(client);
        Print(client, removed ? "已撤销原来的投票，请在面板中重新选择。" : "你当前没有投票，请在面板中选择。");
    }

    private void OpenVotePanel(IGameClient client)
    {
        if (_vote is null)
        {
            return;
        }

        _panels.OpenMaps(
            client,
            "选择下一张地图",
            _vote.Candidates,
            MapPanelMode.Vote,
            _voteEndsAt);
    }

    private void AttemptNomination(IGameClient client, string query)
    {
        if (!_config.Enabled || !_config.Nomination.Enabled || _vote is not null || _state.NextEntryId is not null)
        {
            Print(client, "当前不能提名地图。");
            return;
        }

        var steamId = client.SteamId.AsPrimitive();
        if (_nominations.ContainsKey(steamId))
        {
            Print(client, "你已经预定过地图；如需更换，请先输入 ydc 取消预定。");
            return;
        }

        var entries = GetAvailableEntries()
            .Where(entry => !entry.EntryId.Equals(_state.CurrentEntryId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (query.Length > 0)
        {
            entries = MapSearch.Find(entries, query).ToArray();
            if (entries.Length == 0)
            {
                PrintChat(client, $"找不到可用地图：{query}");
                return;
            }

            if (entries.Length == 1)
            {
                Nominate(client, entries[0]);
                return;
            }
        }

        if (entries.Length == 0)
        {
            Print(client, "没有可预定的地图。");
            return;
        }

        _panels.OpenMaps(
            client,
            query.Length == 0 ? "提名下一张地图" : $"确认提名：{query}",
            entries,
            MapPanelMode.Nomination);
    }

    private void Nominate(IGameClient client, MapPoolEntry entry)
    {
        _nominations[client.SteamId.AsPrimitive()] = entry.EntryId;
        Broadcast($"{client.Name} 提名了 {MapEntryDisplay.Format(entry)}。");
    }

    private void HandlePanelSelection(IGameClient client, MapPoolEntry entry, MapPanelMode mode)
    {
        switch (mode)
        {
            case MapPanelMode.Vote:
                CastVote(client, entry);
                break;
            case MapPanelMode.Nomination:
                if (_vote is not null || _state.NextEntryId is not null)
                {
                    Print(client, "当前不能提名地图。");
                    return;
                }

                if (_nominations.ContainsKey(client.SteamId.AsPrimitive()))
                {
                    Print(client, "你已经预定过地图；如需更换，请先使用 ydc 取消预定。");
                    return;
                }

                _panels.Close(client);
                Nominate(client, entry);
                break;
            case MapPanelMode.Information:
                Print(client, $"{MapEntryDisplay.Format(entry)}，地图文件：{entry.MapName}");
                break;
        }
    }

    private void CancelNomination(IGameClient client)
    {
        var steamId = client.SteamId.AsPrimitive();
        if (!_nominations.Remove(steamId, out var entryId))
        {
            Print(client, "你还没有预定地图，ydc 当前不可用。");
            return;
        }

        var entry = _catalog?.ResolveEntryId(entryId);
        Broadcast($"{client.Name} 已取消预定 {(entry is null ? entryId : MapEntryDisplay.Format(entry))}。");
    }

    private void AttemptRtv(IGameClient client)
    {
        if (!_config.Enabled || !_config.Rtv.Enabled)
        {
            return;
        }

        if (_vote is not null)
        {
            Print(client, "地图投票正在进行。");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < _rtvAvailableAt)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling((_rtvAvailableAt - now).TotalSeconds));
            Print(client, $"RTV 还需等待 {seconds} 秒。");
            return;
        }

        var eligible = GetEligibleClients();
        var eligibleIds = eligible.Select(player => player.SteamId.AsPrimitive()).ToArray();
        var steamId = client.SteamId.AsPrimitive();
        if (!eligibleIds.Contains(steamId))
        {
            Print(client, "只有 CT 或 T 队中的玩家可以发起 RTV。");
            return;
        }

        var progress = _rtv.Register(steamId, eligibleIds, _config.Rtv.RequiredRatio);
        if (!progress.Accepted)
        {
            Print(client, "你已经投过 RTV。");
            return;
        }

        var remaining = Math.Max(0, progress.RequiredVotes - progress.CurrentVotes);
        Broadcast($"{client.Name} 发起 RTV，还需 {remaining} 票（{progress.CurrentVotes}/{progress.RequiredVotes}）。");
        if (!progress.Passed)
        {
            return;
        }

        _rtv.Clear();
        var next = ResolveNextEntry();
        if (next is not null)
        {
            if (!_immediateRtvArmed)
            {
                _immediateRtvArmed = true;
                Broadcast($"RTV 通过，下一张地图已是 {MapEntryDisplay.Format(next)}；再次 RTV 通过才会立即切换。");
                return;
            }

            Broadcast($"第二次 RTV 通过，立即切换到 {MapEntryDisplay.Format(next)}。");
            BeginMapChange("第二次 RTV");
            return;
        }

        Broadcast("RTV 通过，开始选图；结果保留到比赛结束。再次 RTV 可立即执行已选地图。");
        StartMapVote("RTV");
        _immediateRtvArmed = _vote is not null;
    }

    private void PrintNextMap(IGameClient client)
    {
        var next = ResolveNextEntry();
        Print(client, next is null
            ? "尚未选出下一张地图。"
            : $"下一张地图：{MapEntryDisplay.Format(next)} ({next.MapName})");
    }

    private void OpenMapList(IGameClient client)
    {
        var entries = GetAvailableEntries();
        if (entries.Count == 0)
        {
            Print(client, "没有可显示的地图。");
            return;
        }

        _panels.OpenMaps(client, "地图列表", entries, MapPanelMode.Information);
    }

    private void EnsureNextMap()
    {
        if (ResolveNextEntry() is not null || _catalog is null)
        {
            return;
        }

        var selected = MapCandidateSelector.Select(
            GetAvailableEntries(),
            _state.CurrentEntryId,
            _state.RecentEntryIds,
            [],
            1,
            _random).FirstOrDefault();
        if (selected is not null)
        {
            SelectNextMap(selected, "自动选择");
        }
    }

    private void SelectNextMap(MapPoolEntry entry, string reason)
    {
        _state.NextEntryId = entry.EntryId;
        SaveState();
        Broadcast($"下一张地图已选定：{MapEntryDisplay.Format(entry)}（{reason}）。比赛结束后换图；再次 RTV 可立即换图。");
        _logger.LogInformation("Next map selected: {EntryId}, reason={Reason}.", entry.EntryId, reason);
    }

    private void ScheduleMapChange(string reason)
    {
        var next = ResolveNextEntry();
        if (_mapChangeStarted || _mapChangeScheduled || next is null)
        {
            return;
        }

        _mapChangeScheduled = true;
        Broadcast($"{_config.MapChange.DelayAfterMatchSeconds:0.#} 秒后切换到 {MapEntryDisplay.Format(next)}。");
        var generation = _lifecycleGeneration;
        _modSharp.PushTimer(
            () =>
            {
                if (!_stopping && generation == _lifecycleGeneration)
                {
                    _mapChangeScheduled = false;
                    BeginMapChange(reason);
                }
            },
            _config.MapChange.DelayAfterMatchSeconds,
            GameTimerFlags.StopOnMapEnd);
    }

    private void BeginMapChange(string reason)
    {
        var next = ResolveNextEntry();
        if (_mapChangeStarted || next is null)
        {
            return;
        }

        if (!SafeMapName.IsMatch(next.MapName))
        {
            _logger.LogError("Unsafe map name rejected: {MapName}.", next.MapName);
            Broadcast("换图失败：地图名称无效。请检查服务器日志。");
            return;
        }

        _mapChangeStarted = true;
        _state.PendingActivationEntryId = next.EntryId;
        SaveState();
        Broadcast($"正在切换到 {MapEntryDisplay.Format(next)}，请稍候……");
        _logger.LogInformation(
            "Changing to {EntryId}, map={Map}, mode={Mode}, reason={Reason}, workshop={Workshop}, id={WorkshopId}.",
            next.EntryId,
            next.MapName,
            next.Mode,
            reason,
            next.Workshop,
            next.WorkshopId);

        var generation = _lifecycleGeneration;
        var entryId = next.EntryId;
        _modSharp.PushTimer(
            () =>
            {
                if (_stopping || generation != _lifecycleGeneration || !_mapChangeStarted
                    || !string.Equals(_state.PendingActivationEntryId, entryId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (next.Workshop && next.WorkshopId is { } workshopId)
                {
                    _modSharp.ServerCommand($"host_workshop_map {workshopId}");
                }
                else
                {
                    _modSharp.ChangeLevel(next.MapName);
                }
            },
            1,
            GameTimerFlags.StopOnMapEnd);
    }

    private MapPoolEntry? ResolveNextEntry()
    {
        if (_state.NextEntryId is null)
        {
            return null;
        }

        var entry = _catalog?.ResolveEntryId(_state.NextEntryId);
        if (entry is null)
        {
            _state.NextEntryId = null;
            SaveState();
        }

        return entry;
    }

    private IReadOnlyList<MapPoolEntry> GetAvailableEntries()
    {
        var humans = GetEligibleClients().Count;
        return _catalog?.Entries
            .Where(entry => entry.Enabled
                && humans >= entry.MinPlayers
                && humans <= entry.MaxPlayers)
            .ToArray() ?? [];
    }

    private IReadOnlyList<IGameClient> GetEligibleClients()
        => _clients.GetGameClients(inGame: true)
            .Where(IsHuman)
            .Where(client => client.GetPlayerController()?.Team is CStrikeTeam.CT or CStrikeTeam.TE)
            .ToArray();

    private static bool IsHuman(IGameClient client)
        => client.IsValid
            && client.IsInGame
            && !BotIdentityRegistry.IsBot(client.IsFakeClient, client.Slot.AsPrimitive())
            && !client.IsHltv;

    private ModeId? CurrentMode()
        => _modeContext?.Instance?.Current?.Selection.Mode;

    private void InstallCommands()
    {
        _clients.InstallCommandCallback("rtv", OnRtvCommand);
        _clients.InstallCommandCallback("yd", OnNominateCommand);
        _clients.InstallCommandCallback("nominate", OnNominateCommand);
        _clients.InstallCommandCallback("ydc", OnCancelNominationCommand);
        _clients.InstallCommandCallback("revote", OnRevoteCommand);
        _clients.InstallCommandCallback("nextmap", OnNextMapCommand);
        _clients.InstallCommandCallback("maps", OnMapsCommand);
    }

    private void RemoveCommands()
    {
        _clients.RemoveCommandCallback("rtv", OnRtvCommand);
        _clients.RemoveCommandCallback("yd", OnNominateCommand);
        _clients.RemoveCommandCallback("nominate", OnNominateCommand);
        _clients.RemoveCommandCallback("ydc", OnCancelNominationCommand);
        _clients.RemoveCommandCallback("revote", OnRevoteCommand);
        _clients.RemoveCommandCallback("nextmap", OnNextMapCommand);
        _clients.RemoveCommandCallback("maps", OnMapsCommand);
    }

    private ECommandAction OnRtvCommand(IGameClient client, StringCommand command)
    {
        AttemptRtv(client);
        return ECommandAction.Handled;
    }

    private ECommandAction OnNominateCommand(IGameClient client, StringCommand command)
    {
        AttemptNomination(client, command.ArgString.Trim().Trim('"'));
        return ECommandAction.Handled;
    }

    private ECommandAction OnCancelNominationCommand(IGameClient client, StringCommand command)
    {
        CancelNomination(client);
        return ECommandAction.Handled;
    }

    private ECommandAction OnRevoteCommand(IGameClient client, StringCommand command)
    {
        ReopenVote(client);
        return ECommandAction.Handled;
    }

    private ECommandAction OnNextMapCommand(IGameClient client, StringCommand command)
    {
        PrintNextMap(client);
        return ECommandAction.Handled;
    }

    private ECommandAction OnMapsCommand(IGameClient client, StringCommand command)
    {
        OpenMapList(client);
        return ECommandAction.Handled;
    }

    private void Print(IGameClient client, string message)
        => _panels.ShowMessage(client, message);

    private static void PrintChat(IGameClient client, string message)
        => client.Print(HudPrintChannel.Chat, message);

    private void Broadcast(string message)
    {
        foreach (var client in _clients.GetGameClients(inGame: true).Where(IsHuman))
        {
            client.Print(HudPrintChannel.Chat, message);
        }
    }

    private static bool MapNamesMatch(string? left, string? right)
    {
        var normalizedLeft = NormalizeMapName(left);
        var normalizedRight = NormalizeMapName(right);
        if (normalizedLeft.Length == 0 || normalizedRight.Length == 0)
        {
            return false;
        }

        if (normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var shortLeft = normalizedLeft.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? normalizedLeft;
        var shortRight = normalizedRight.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? normalizedRight;
        return shortLeft.Equals(shortRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeMapName(string? mapName)
        => (mapName ?? string.Empty).Trim().Replace('\\', '/').ToLowerInvariant();

}
