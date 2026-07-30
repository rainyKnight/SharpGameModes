using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SharpGameModes.Contracts;
using SharpGameModes.Domain;
using SharpGameModes.PlayerData.Storage;
using Sharp.Shared;
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameEvents;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace SharpGameModes.PlayerData;

public sealed class PlayerDataModule :
    IModSharpModule,
    IPlayerRatingProvider,
    IGameListener,
    IEventListener
{
    private static readonly IReadOnlyDictionary<string, string> ChatColorTags
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = ChatColor.White,
            ["white"] = ChatColor.White,
            ["red"] = ChatColor.Red,
            ["gold"] = ChatColor.Gold,
            ["lime"] = ChatColor.Lime,
            ["green"] = ChatColor.Green,
            ["blue"] = ChatColor.Blue,
            ["lightblue"] = ChatColor.Blue,
            ["purple"] = ChatColor.Purple,
            ["lightpurple"] = ChatColor.Purple,
            ["grey"] = ChatColor.Grey,
            ["gray"] = ChatColor.Grey,
            ["silver"] = ChatColor.Silver,
            ["yellow"] = ChatColor.Yellow,
        };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly string[] TrackedEvents =
    [
        "round_start",
        "player_hurt",
        "player_death",
        "round_end",
        "cs_win_panel_match",
        "cs_match_end_restart",
    ];

    private readonly ISharedSystem _shared;
    private readonly IModSharp _modSharp;
    private readonly IClientManager _clients;
    private readonly IEventManager _events;
    private readonly ILogger<PlayerDataModule> _logger;
    private readonly string _configPath;
    private readonly string _sharpPath;
    private readonly object _reloadGate = new();
    private readonly object _writeQueueGate = new();
    private readonly PlayerMatchResultSource _matchResults = new();
    private IReadOnlyDictionary<ulong, PlayerRatingSnapshot> _ratings
        = new Dictionary<ulong, PlayerRatingSnapshot>();
    private PlayerDataConfig _config = new();
    private PlayerRatingRepository? _repository;
    private RatingMatchTracker _tracker = new();
    private IModSharpModuleInterface<IModeContext>? _modeContext;
    private Task _writeQueue = Task.CompletedTask;
    private bool _enabled;
    private bool _listenersInstalled;
    private bool _shuttingDown;

    public PlayerDataModule(
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
        _logger = sharedSystem.GetLoggerFactory().CreateLogger<PlayerDataModule>();
        _sharpPath = sharpPath;
        _configPath = Path.Combine(sharpPath, "configs", "sharp-gamemodes", "player-data.jsonc");
    }

    public string DisplayName => "SharpGameModes Player Data";
    public string DisplayAuthor => "SharpGameModes Contributors";
    public int Count => _ratings.Count;
    public DateTimeOffset LoadedAt { get; private set; }
    public int ListenerVersion => IEventListener.ApiVersion;
    public int ListenerPriority => 20;

    public bool Init()
    {
        try
        {
            _config = JsonSerializer.Deserialize<PlayerDataConfig>(File.ReadAllText(_configPath), SerializerOptions)
                ?? throw new InvalidDataException("Player-data config is empty.");
            _config.Validate();
            _enabled = _config.Enabled;
            if (!_enabled)
            {
                _logger.LogInformation("Player rating database is disabled by configuration.");
                return true;
            }

            var databasePath = Path.IsPathRooted(_config.DatabasePath)
                ? _config.DatabasePath
                : Path.Combine(_sharpPath, _config.DatabasePath);
            _repository = new PlayerRatingRepository(databasePath);
            _tracker = new RatingMatchTracker(_config.TradeWindowSeconds);
            Reload();

            _modSharp.InstallGameListener(this);
            _events.InstallEventListener(this);
            foreach (var eventName in TrackedEvents)
            {
                _events.HookEvent(eventName);
            }

            _listenersInstalled = true;
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to initialize player rating storage from {ConfigPath}.", _configPath);
            return false;
        }
    }

    public void PostInit()
    {
        _shared.GetSharpModuleManager().RegisterSharpModuleInterface<IPlayerRatingProvider>(
            this,
            IPlayerRatingProvider.Identity,
            this);
        _shared.GetSharpModuleManager().RegisterSharpModuleInterface<IPlayerMatchResultSource>(
            this,
            IPlayerMatchResultSource.Identity,
            _matchResults);
    }

    public void OnAllModulesLoaded()
    {
        RefreshModeContext();
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
            _modeContext = null;
            _tracker.DiscardMatch();
        }
    }

    public void OnGameInit()
    {
        DiscardIncompleteMatch("map initialization");
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
                case "player_hurt":
                    OnPlayerHurt(gameEvent);
                    break;
                case "player_death":
                    OnPlayerDeath(gameEvent);
                    break;
                case "round_end":
                    OnRoundEnd(gameEvent);
                    break;
                case "cs_win_panel_match":
                    CompleteMatch();
                    break;
                case "cs_match_end_restart":
                    DiscardIncompleteMatch("match restart");
                    break;
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to process game event {EventName} for player ratings.", gameEvent.Name);
        }
    }

    public bool TryGetRating(ulong steamId, out PlayerRatingSnapshot? rating)
        => _ratings.TryGetValue(steamId, out rating);

    public bool IsMapAllowed(string mapName)
        => _config.IsMapAllowed(mapName);

    public int Reload()
    {
        lock (_reloadGate)
        {
            if (!_enabled || _repository is null)
            {
                _ratings = new Dictionary<ulong, PlayerRatingSnapshot>();
                LoadedAt = DateTimeOffset.UtcNow;
                return 0;
            }

            _repository.EnsureCreated();
            _ratings = _repository.LoadAll();
            LoadedAt = DateTimeOffset.UtcNow;
            _logger.LogInformation(
                "Loaded {Count} player rating records from {DatabasePath}.",
                _ratings.Count,
                _repository.DatabasePath);
            return _ratings.Count;
        }
    }

    public void Shutdown()
    {
        _shuttingDown = true;
        if (_listenersInstalled)
        {
            _events.RemoveEventListener(this);
            _modSharp.RemoveGameListener(this);
            _listenersInstalled = false;
        }

        _tracker.DiscardMatch();
        Task pending;
        lock (_writeQueueGate)
        {
            pending = _writeQueue;
        }

        try
        {
            if (!pending.Wait(TimeSpan.FromSeconds(5)))
            {
                _logger.LogWarning("Timed out waiting for the player rating write queue during shutdown.");
            }
        }
        catch (AggregateException exception)
        {
            _logger.LogError(exception.Flatten(), "Player rating write queue failed during shutdown.");
        }

        _modeContext = null;
        _ratings = new Dictionary<ulong, PlayerRatingSnapshot>();
        _repository = null;
        _matchResults.Dispose();
    }

    private void OnRoundStart()
    {
        if (!ShouldCollectRoundData())
        {
            _tracker.ResetRound();
            return;
        }

        _tracker.StartRound(GetActiveHumanPlayers());
    }

    private void OnPlayerHurt(IGameEvent gameEvent)
    {
        if (!ShouldCollectRoundData())
        {
            return;
        }

        var victimController = gameEvent is IEventPlayerHurt hurt
            ? hurt.VictimController
            : gameEvent.GetPlayerController("userid");
        var attackerController = gameEvent is IEventPlayerHurt typedHurt
            ? typedHurt.KillerController
            : gameEvent.GetPlayerController("attacker");
        var victim = ToTrackedPlayer(victimController);
        var attacker = ToTrackedPlayer(attackerController);
        if (victim is null || attacker is null)
        {
            return;
        }

        var damage = gameEvent is IEventPlayerHurt damageEvent
            ? damageEvent.Damage
            : gameEvent.GetInt("dmg_health");
        _tracker.RegisterDamage(attacker, victim, damage);
    }

    private void OnPlayerDeath(IGameEvent gameEvent)
    {
        if (!ShouldCollectRoundData())
        {
            return;
        }

        var death = gameEvent as IEventPlayerDeath;
        var victim = ToTrackedPlayer(death?.VictimController ?? gameEvent.GetPlayerController("userid"));
        if (victim is null)
        {
            return;
        }

        var attacker = ToTrackedPlayer(death?.KillerController ?? gameEvent.GetPlayerController("attacker"));
        var assister = ToTrackedPlayer(death?.AssisterController ?? gameEvent.GetPlayerController("assister"));
        _tracker.RegisterDeath(
            victim,
            attacker,
            assister,
            death?.Headshot ?? gameEvent.GetBool("headshot"),
            _modSharp.EngineTime());
    }

    private void OnRoundEnd(IGameEvent gameEvent)
    {
        if (_tracker.IsRoundLive && ShouldCollectRoundData())
        {
            var winner = gameEvent is IEventRoundEnd roundEnd
                ? ToTrackedTeam(roundEnd.Winner)
                : ToTrackedTeam(gameEvent.Get<CStrikeTeam>("winner"));
            _tracker.EndRound(winner, GetActiveHumanPlayers());
        }

        _tracker.ResetRound();
    }

    private void CompleteMatch()
    {
        var completed = _tracker.CompleteMatch();
        var recordPlayerData = ShouldRecordPlayerData();
        var printTopPlayers = ShouldPrintTopPlayers();
        if (completed.Count == 0 || (!recordPlayerData && !printTopPlayers))
        {
            return;
        }

        var mapName = _modSharp.GetMapName() ?? string.Empty;
        var recordedAt = DateTimeOffset.UtcNow;
        var formula = _config.RatingFormula.ToDomain();
        var calculated = completed
            .Select(stats =>
            {
                var rating = RatingCalculator.Calculate(stats.ToRatingStatistics(), formula);
                var result = new PlayerMatchResultSnapshot(
                    stats.SteamId,
                    stats.PlayerName,
                    mapName,
                    recordedAt,
                    stats.RoundsPlayed,
                    rating.Rating,
                    rating.Impact,
                    rating.Adr);
                return (Stats: stats, Rating: rating, Result: result);
            })
            .ToArray();
        if (calculated.Length == 0)
        {
            return;
        }

        PrintTopMatchRatings(calculated.Select(entry => entry.Result).ToArray(), printTopPlayers);
        if (!recordPlayerData || _repository is null)
        {
            return;
        }

        var writable = calculated
            .Where(entry => !_config.DataWriteSkipWhitelistIds.Contains(entry.Stats.SteamId))
            .ToArray();
        if (writable.Length == 0)
        {
            _logger.LogInformation("Completed match has no writable player rating records after skip-list filtering.");
            return;
        }

        var results = writable.Select(entry => entry.Result).ToArray();
        foreach (var exception in _matchResults.Publish(results))
        {
            _logger.LogError(exception, "A completed-match subscriber failed.");
        }

        var writes = writable
            .Select(entry => CreateWrite(entry.Stats, mapName, recordedAt, entry.Rating))
            .ToArray();
        QueueWrite(_repository, writes);
        _logger.LogInformation(
            "Queued {Count} completed player rating records for map {MapName}.",
            writes.Length,
            mapName);
    }

    private void PrintTopMatchRatings(
        IReadOnlyList<PlayerMatchResultSnapshot> results,
        bool enabled)
    {
        if (!enabled || results.Count == 0)
        {
            return;
        }

        var top = results
            .OrderByDescending(result => result.Rating)
            .ThenByDescending(result => result.Impact)
            .ThenByDescending(result => result.Adr)
            .Take(3)
            .ToArray();
        var title = ApplyChatColorTags(
            CurrentAutoTeamRule()?.TopPlayersChatTitle ?? _config.TopPlayersChatTitle);
        if (title.Length > 0)
        {
            PrintToAll(EnsureLeadingChatColorWorks(title));
        }

        string[] rankColors = [ChatColor.Gold, ChatColor.Red, ChatColor.Purple];
        for (var index = 0; index < top.Length; index++)
        {
            var result = top[index];
            PrintToAll(
                $" {rankColors[index]}#{index + 1} {result.PlayerName} "
                + $"rating {result.Rating:F2}  impact {result.Impact:F2}  adr {result.Adr:F1}{ChatColor.White}");
        }
    }

    private void PrintToAll(string message)
    {
        foreach (var client in _clients.GetGameClients(inGame: true)
            .Where(client => !BotIdentityRegistry.IsBot(
                    client.IsFakeClient,
                    client.Slot.AsPrimitive())
                && !client.IsHltv))
        {
            client.Print(HudPrintChannel.Chat, message);
        }
    }

    private static string ApplyChatColorTags(string message)
    {
        var formatted = message;
        foreach (var (name, color) in ChatColorTags)
        {
            formatted = formatted.Replace($"{{{name}}}", color, StringComparison.OrdinalIgnoreCase);
        }

        return formatted;
    }

    private static string EnsureLeadingChatColorWorks(string message)
        => ChatColorTags.Values.Any(message.StartsWith) ? $" {message}" : message;

    private void QueueWrite(PlayerRatingRepository repository, IReadOnlyCollection<PlayerMatchWrite> writes)
    {
        lock (_writeQueueGate)
        {
            _writeQueue = _writeQueue.ContinueWith(
                _ => PersistMatches(repository, writes),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    private void PersistMatches(PlayerRatingRepository repository, IReadOnlyCollection<PlayerMatchWrite> writes)
    {
        try
        {
            repository.WriteMatches(writes, _config.HistoryLimit);
            if (!_shuttingDown)
            {
                Reload();
            }

            _logger.LogInformation("Persisted {Count} player rating records.", writes.Count);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to persist {Count} player rating records.", writes.Count);
        }
    }

    private void DiscardIncompleteMatch(string reason)
    {
        var playerCount = _tracker.MatchPlayerCount;
        var hadLiveRound = _tracker.IsRoundLive;
        _tracker.DiscardMatch();
        if (playerCount > 0 || hadLiveRound)
        {
            _logger.LogInformation(
                "Discarded incomplete player rating data: {Reason}; players={PlayerCount}.",
                reason,
                playerCount);
        }
    }

    private bool ShouldCollectRoundData()
        => _enabled
            && _config.IsMapAllowed(_modSharp.GetMapName())
            && (ShouldRecordPlayerData() || ShouldPrintTopPlayers())
            && IsRatingModeActive()
            && !_modSharp.GetGameRules().IsWarmupPeriod;

    private bool ShouldRecordPlayerData()
        => _config.IsMapAllowed(_modSharp.GetMapName())
            && (CurrentAutoTeamRule()?.RecordPlayerData ?? _config.RecordPlayerData);

    private bool ShouldPrintTopPlayers()
        => _config.IsMapAllowed(_modSharp.GetMapName())
            && (CurrentAutoTeamRule()?.PrintTopPlayersToChat ?? _config.PrintTopPlayersToChat);

    private bool IsRatingModeActive()
    {
        var selection = _modeContext?.Instance?.Current?.Selection;
        if (selection is null)
        {
            return false;
        }

        return selection.AutoTeam?.RecordPlayerData == true
            || selection.AutoTeam?.PrintTopPlayersToChat == true
            || selection.Mode == ModeId.Classic;
    }

    private AutoTeamRuleOverrides? CurrentAutoTeamRule()
        => _modeContext?.Instance?.Current?.Selection.AutoTeam;

    private void RefreshModeContext()
    {
        _modeContext = _shared.GetSharpModuleManager()
            .GetOptionalSharpModuleInterface<IModeContext>(IModeContext.Identity);
    }

    private IEnumerable<TrackedPlayer> GetActiveHumanPlayers()
        => _clients.GetGameClients(inGame: true)
            .Where(client => !BotIdentityRegistry.IsBot(
                    client.IsFakeClient,
                    client.Slot.AsPrimitive())
                && !client.IsHltv)
            .Select(client => ToTrackedPlayer(client.GetPlayerController()))
            .OfType<TrackedPlayer>();

    private static TrackedPlayer? ToTrackedPlayer(IPlayerController? controller)
    {
        if (controller is null
            || BotIdentityRegistry.IsBot(
                controller.IsFakeClient,
                controller.PlayerSlot.AsPrimitive())
            || controller.IsHltv)
        {
            return null;
        }

        var team = ToTrackedTeam(controller.Team);
        var steamId = controller.SteamId.AsPrimitive();
        if (team is null || steamId == 0)
        {
            return null;
        }

        return new TrackedPlayer(
            steamId,
            controller.PlayerName,
            team.Value,
            controller.GetPlayerPawn()?.IsAlive ?? false);
    }

    private static TrackedTeam? ToTrackedTeam(CStrikeTeam team)
        => team switch
        {
            CStrikeTeam.CT => TrackedTeam.CounterTerrorist,
            CStrikeTeam.TE => TrackedTeam.Terrorist,
            _ => null,
        };

    private static PlayerMatchWrite CreateWrite(
        CompletedPlayerMatchStatistics stats,
        string mapName,
        DateTimeOffset recordedAt,
        MatchRating rating)
    {
        return new PlayerMatchWrite(
            stats.SteamId,
            stats.PlayerName,
            mapName,
            recordedAt,
            stats.RoundsPlayed,
            rating.Rating,
            rating.Impact,
            rating.Kast,
            rating.Adr,
            rating.KillsPerRound,
            rating.DeathsPerRound,
            rating.AssistsPerRound,
            stats.Kills,
            stats.Deaths,
            stats.Assists,
            stats.Damage,
            stats.Headshots,
            stats.EntryKills,
            stats.EntryDeaths,
            stats.MultiKillRounds,
            stats.ClutchesWon,
            stats.KastRounds,
            stats.SurvivedRounds);
    }
}
