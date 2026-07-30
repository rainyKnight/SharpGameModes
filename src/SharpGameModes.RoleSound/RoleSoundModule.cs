using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SharpGameModes.Contracts;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameEvents;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace SharpGameModes.RoleSound;

public sealed class RoleSoundModule : IModSharpModule, IGameListener, IClientListener, IEventListener
{
    private const string DeathEvent = "death";
    private const string HurtEvent = "hurt";
    private const string KillEvent = "kill";
    private const string ThrowEvent = "throw";
    private const string RoundStartEvent = "round_start";
    private const string RoundEndEvent = "round_end";
    private const string RadioCooldownEvent = "radio";
    private const string RadioPrefix = "radio.";
    private const string GenericRadioEvent = "radio.generic";
    private const char ChatDefault = '\x01';
    private const char ChatRed = '\x07';
    private const char ChatPink = '\x03';

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly string[] DiagnosticCommands =
    [
        "rsreload", "rsdebug", "rspreview", "rsemit", "rsplay",
    ];

    private readonly IModSharp _modSharp;
    private readonly IClientManager _clients;
    private readonly IEventManager _events;
    private readonly IFileManager _files;
    private readonly ISoundManager _sounds;
    private readonly ILogger<RoleSoundModule> _logger;
    private readonly string _configPath;
    private readonly Random _random = new();
    private readonly Dictionary<ulong, Dictionary<string, DateTimeOffset>> _lastPlayedAt = [];
    private readonly List<string> _radioCommands = [];
    private RoleSoundConfig _config = new();
    private RoleSoundCatalog _catalog = null!;
    private ulong? _lastRoundEndingDeadPlayerSteamId;
    private string? _lastRoundEndingDeadPlayerProfileName;
    private int _lifecycleGeneration;
    private bool _stopping;
    private bool _listenersInstalled;
    private bool _audioListenersInstalled;

    public RoleSoundModule(
        ISharedSystem sharedSystem,
        string dllPath,
        string sharpPath,
        Version version,
        IConfiguration coreConfiguration,
        bool hotReload)
    {
        _modSharp = sharedSystem.GetModSharp();
        _clients = sharedSystem.GetClientManager();
        _events = sharedSystem.GetEventManager();
        _files = sharedSystem.GetFileManager();
        _sounds = sharedSystem.GetSoundManager();
        _logger = sharedSystem.GetLoggerFactory().CreateLogger<RoleSoundModule>();
        _configPath = Path.Combine(sharpPath, "configs", "sharp-gamemodes", "rolesound.jsonc");
    }

    public string DisplayName => "SharpGameModes Role Sound";
    public string DisplayAuthor => "SharpGameModes Contributors";
    public int ListenerVersion => IGameListener.ApiVersion;
    public int ListenerPriority => 10;

    public bool Init()
    {
        if (!LoadConfiguration())
        {
            return false;
        }

        if (!_config.Enabled)
        {
            _logger.LogInformation("SharpGameModes Role Sound is disabled.");
            return true;
        }

        _modSharp.InstallGameListener(this);
        _clients.InstallClientListener(this);
        _listenersInstalled = true;
        if (_config.Enabled)
        {
            _events.InstallEventListener(this);
            foreach (var eventName in new[]
                     {
                         "player_death", "player_hurt", "grenade_thrown", "weapon_fire",
                         "player_radio", "round_start", "round_end",
                     })
            {
                _events.HookEvent(eventName);
            }

            InstallCommands();
            _audioListenersInstalled = true;
        }

        _logger.LogInformation(
            "SharpGameModes Role Sound loaded with {ProfileCount} configured voice profiles.",
            _catalog.ProfileCount);
        return true;
    }

    public void Shutdown()
    {
        _stopping = true;
        _lifecycleGeneration++;
        if (_listenersInstalled)
        {
            _clients.RemoveClientListener(this);
            _modSharp.RemoveGameListener(this);
            _listenersInstalled = false;
        }

        if (_audioListenersInstalled)
        {
            RemoveCommands();
            _events.RemoveEventListener(this);
            _audioListenersInstalled = false;
        }

        _lastPlayedAt.Clear();
        _lastRoundEndingDeadPlayerSteamId = null;
        _lastRoundEndingDeadPlayerProfileName = null;
    }

    public void OnGameInit()
    {
        _lifecycleGeneration++;
        _lastRoundEndingDeadPlayerSteamId = null;
        _lastRoundEndingDeadPlayerProfileName = null;
    }

    public void OnGamePreShutdown()
    {
        _lifecycleGeneration++;
        _lastRoundEndingDeadPlayerSteamId = null;
        _lastRoundEndingDeadPlayerProfileName = null;
    }

    public void OnResourcePrecache()
    {
        foreach (var resource in _config.SoundEventResources)
        {
            var available = _files.FileExists(resource, "GAME")
                || _files.FileExists($"{resource}_c", "GAME");
            if (!available)
            {
                _logger.LogWarning(
                    "RoleSound resource {Resource} is not visible through the GAME search path.",
                    resource);
            }

            _modSharp.PrecacheResource(resource);
        }
    }

    public void OnGameActivate()
    {
        var generation = _lifecycleGeneration;
        _modSharp.PushTimer(
            () =>
            {
                if (_stopping || generation != _lifecycleGeneration)
                {
                    return;
                }

                ValidateRepresentativeSoundEvent();
            },
            0.25,
            GameTimerFlags.StopOnMapEnd);
    }

    public void OnClientDisconnecting(IGameClient client, NetworkDisconnectionReason reason)
    {
        _lastPlayedAt.Remove(client.SteamId.AsPrimitive());
    }

    public bool HookFireEvent(IGameEvent gameEvent, ref bool serverOnly)
    {
        if (!gameEvent.Name.Equals("player_radio", StringComparison.OrdinalIgnoreCase)
            || !_config.Enabled
            || !_config.EnableRadio
            || !IsHuman(gameEvent.GetPlayerController("userid"), out var player)
            || player.GetPawn() is not { IsAlive: true })
        {
            return true;
        }

        var rawSlot = gameEvent.GetString("slot");
        if (string.IsNullOrWhiteSpace(rawSlot))
        {
            rawSlot = gameEvent.GetInt("slot").ToString();
        }

        var radioKey = ResolveRadioKey(rawSlot, _config.RadioSlotToKey, "roger");
        var result = TryPlayRadio(player, radioKey);
        return !_config.BlockDefaultRadio
            || result == RadioPlaybackResult.NoSound && !_config.BlockRadioWhenNoVoice;
    }

    public void FireGameEvent(IGameEvent gameEvent)
    {
        try
        {
            switch (gameEvent.Name)
            {
                case "player_death" when gameEvent is IEventPlayerDeath death:
                    OnPlayerDeath(death);
                    break;
                case "player_hurt" when gameEvent is IEventPlayerHurt hurt:
                    OnPlayerHurt(hurt);
                    break;
                case "grenade_thrown" when gameEvent is IEventGrenadeThrown grenade:
                    OnGrenadeThrown(grenade);
                    break;
                case "weapon_fire" when gameEvent is IEventWeaponFired weapon:
                    OnWeaponFired(weapon);
                    break;
                case "round_start":
                    OnRoundStart();
                    break;
                case "round_end":
                    OnRoundEnd();
                    break;
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to handle RoleSound event {EventName}.", gameEvent.Name);
        }
    }

    private bool LoadConfiguration()
    {
        try
        {
            _config = JsonSerializer.Deserialize<RoleSoundConfig>(
                File.ReadAllText(_configPath),
                SerializerOptions) ?? throw new InvalidDataException("RoleSound configuration is empty.");
            _config.Normalize();
            _catalog = new RoleSoundCatalog(_config);
            if (_catalog.ProfileCount == 0)
            {
                throw new InvalidDataException("RoleSound configuration contains no voice profiles.");
            }

            return true;
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or InvalidDataException or ArgumentException)
        {
            _logger.LogError(exception, "Failed to load RoleSound configuration from {Path}.", _configPath);
            return false;
        }
    }

    private void InstallCommands()
    {
        foreach (var command in DiagnosticCommands)
        {
            _clients.InstallCommandCallback(command, OnDiagnosticCommand);
        }

        _clients.InstallCommandListener("+reload", OnReloadInput);
        foreach (var command in new[] { "roger", "negative", "cheer", "holdpos", "followme", "thanks" }
                     .Concat(_config.RadioCommandToKey.Keys)
                     .Where(command => !string.IsNullOrWhiteSpace(command))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _clients.InstallCommandListener(command, OnRadioCommand);
            _radioCommands.Add(command);
        }
    }

    private void RemoveCommands()
    {
        foreach (var command in DiagnosticCommands)
        {
            _clients.RemoveCommandCallback(command, OnDiagnosticCommand);
        }

        _clients.RemoveCommandListener("+reload", OnReloadInput);
        foreach (var command in _radioCommands)
        {
            _clients.RemoveCommandListener(command, OnRadioCommand);
        }

        _radioCommands.Clear();
    }

    private ECommandAction OnReloadInput(IGameClient client, StringCommand command)
    {
        if (_config.Enabled && _config.EnableReload && IsHuman(client, out var player))
        {
            TryPlayRoleSound(player, "reload", VoiceAudience.Self);
        }

        return ECommandAction.Skipped;
    }

    private ECommandAction OnRadioCommand(IGameClient client, StringCommand command)
    {
        if (!_config.Enabled || !_config.EnableRadio || !IsHuman(client, out var player))
        {
            return ECommandAction.Skipped;
        }

        if (player.GetPawn() is not { IsAlive: true })
        {
            return _config.BlockDefaultRadio ? ECommandAction.Stopped : ECommandAction.Skipped;
        }

        var radioKey = ResolveRadioKey(command.CommandName, _config.RadioCommandToKey, command.CommandName);
        var result = TryPlayRadio(player, radioKey);
        return _config.BlockDefaultRadio
            && (result != RadioPlaybackResult.NoSound || _config.BlockRadioWhenNoVoice)
                ? ECommandAction.Stopped
                : ECommandAction.Skipped;
    }

    private ECommandAction OnDiagnosticCommand(IGameClient client, StringCommand command)
    {
        if (!IsHuman(client, out var player))
        {
            return ECommandAction.Handled;
        }

        var commandName = NormalizeCommandName(command.CommandName);
        switch (commandName)
        {
            case "rsreload":
                if (LoadConfiguration())
                {
                    _lastPlayedAt.Clear();
                    client.Print(
                        HudPrintChannel.Chat,
                        $"{_config.ChatPrefix} 配置已重载，语音角色数：{_catalog.ProfileCount}。");
                }
                else
                {
                    client.Print(HudPrintChannel.Chat, $"{_config.ChatPrefix} 配置重载失败，请查看服务器日志。");
                }
                break;
            case "rsdebug":
                PrintDebug(client, player);
                break;
            case "rspreview":
                Preview(client, player, command.ArgCount > 0 ? command.GetArg(1) : "radio.roger");
                break;
            case "rsemit":
                EmitRaw(client, player, command);
                break;
            case "rsplay":
                PlayClientResource(client, player, command);
                break;
        }

        return ECommandAction.Handled;
    }

    private void OnPlayerDeath(IEventPlayerDeath death)
    {
        if (!_config.Enabled || !IsHuman(death.VictimController, out var victim))
        {
            return;
        }

        var victimWasLastAlive = IsLastAliveOnTeam(victim);
        if (victimWasLastAlive)
        {
            _lastRoundEndingDeadPlayerSteamId = victim.SteamId.AsPrimitive();
            _lastRoundEndingDeadPlayerProfileName = _catalog.ResolveProfileName(GetCurrentModelPath(victim));
        }

        if (_config.EnableDeath && !victimWasLastAlive)
        {
            TryPlayRoleSound(victim, DeathEvent, VoiceAudience.Self);
        }

        if (_config.EnableKill
            && !victimWasLastAlive
            && IsHuman(death.KillerController, out var killer)
            && !IsSamePlayer(killer, victim))
        {
            TryPlayRoleSound(killer, KillEvent, VoiceAudience.Self);
        }
    }

    private void OnPlayerHurt(IEventPlayerHurt hurt)
    {
        if (_config.Enabled
            && _config.EnableHurt
            && hurt.Health > 0
            && IsHuman(hurt.VictimController, out var victim))
        {
            TryPlayRoleSound(victim, HurtEvent, VoiceAudience.Self);
        }
    }

    private void OnGrenadeThrown(IEventGrenadeThrown grenade)
    {
        if (_config.Enabled && _config.EnableThrow && IsHuman(grenade.Controller, out var player))
        {
            TryPlayRoleSound(player, ThrowEvent, VoiceAudience.Self);
        }
    }

    private void OnWeaponFired(IEventWeaponFired weapon)
    {
        if (_config.Enabled
            && _config.EnableThrow
            && IsThrowableWeapon(weapon.Weapon)
            && IsHuman(weapon.Controller, out var player))
        {
            TryPlayRoleSound(player, ThrowEvent, VoiceAudience.Self);
        }
    }

    private void OnRoundStart()
    {
        _lastRoundEndingDeadPlayerSteamId = null;
        _lastRoundEndingDeadPlayerProfileName = null;
        if (!_config.Enabled || !_config.EnableRoundStart)
        {
            return;
        }

        var generation = _lifecycleGeneration;
        _modSharp.PushTimer(
            () =>
            {
                if (_stopping || generation != _lifecycleGeneration)
                {
                    return;
                }

                foreach (var player in GetHumanPlayers())
                {
                    TryPlayRoleSound(player, RoundStartEvent, VoiceAudience.Self);
                }
            },
            _config.RoundStartDelaySeconds,
            GameTimerFlags.StopOnMapEnd);
    }

    private void OnRoundEnd()
    {
        if (!_config.Enabled || !_config.EnableRoundEnd)
        {
            return;
        }

        foreach (var player in GetHumanPlayers())
        {
            var steamId = player.SteamId.AsPrimitive();
            if (player.GetPawn() is { IsAlive: true })
            {
                TryPlayRoleSound(player, RoundEndEvent, VoiceAudience.Self);
            }
            else if (_lastRoundEndingDeadPlayerSteamId == steamId
                     && !string.IsNullOrWhiteSpace(_lastRoundEndingDeadPlayerProfileName))
            {
                TryPlayRoleSound(
                    player,
                    _lastRoundEndingDeadPlayerProfileName,
                    RoundEndEvent,
                    VoiceAudience.Self);
            }
        }
    }

    private RadioPlaybackResult TryPlayRadio(IPlayerController player, string radioKey)
    {
        if (player.GetPawn() is not { IsAlive: true })
        {
            return RadioPlaybackResult.NoSound;
        }

        var eventKey = $"{RadioPrefix}{RoleSoundCatalog.NormalizeKey(radioKey)}";
        if (!IsCooldownReady(player, RadioCooldownEvent))
        {
            if (!WasPlayedWithin(player, RadioCooldownEvent, 0.35))
            {
                PrintCooldownMessage(player, RadioCooldownEvent);
            }

            return RadioPlaybackResult.Cooldown;
        }

        if (!TrySelectSound(player, eventKey, RadioCooldownEvent, out var selected)
            && !TrySelectSound(player, GenericRadioEvent, RadioCooldownEvent, out selected))
        {
            return RadioPlaybackResult.NoSound;
        }

        var audioAudience = ResolveAudience(_config.RadioAudience, VoiceAudience.Nearby);
        EmitSelectedSound(player, selected, audioAudience);
        if (_config.ShowRadioText)
        {
            PrintRadioText(player, selected, ResolveAudience(_config.RadioTextAudience, VoiceAudience.Nearby));
        }

        return RadioPlaybackResult.Played;
    }

    private bool TryPlayRoleSound(IPlayerController player, string eventKey, VoiceAudience audience)
    {
        if (!TrySelectSound(player, eventKey, eventKey, out var selected))
        {
            return false;
        }

        EmitSelectedSound(player, selected, audience);
        return true;
    }

    private bool TryPlayRoleSound(
        IPlayerController player,
        string profileName,
        string eventKey,
        VoiceAudience audience)
    {
        if (!TrySelectSoundForProfile(player, profileName, eventKey, eventKey, out var selected))
        {
            return false;
        }

        EmitSelectedSound(player, selected, audience);
        return true;
    }

    private bool TrySelectSound(
        IPlayerController player,
        string eventKey,
        string cooldownKey,
        [NotNullWhen(true)] out SelectedSound? selected)
    {
        selected = null;
        if (!IsHuman(player, out _) || !IsCooldownReady(player, cooldownKey))
        {
            return false;
        }

        var profileName = _catalog.ResolveProfileName(GetCurrentModelPath(player));
        return profileName is not null
            && TrySelectSoundForProfile(player, profileName, eventKey, cooldownKey, out selected);
    }

    private bool TrySelectSoundForProfile(
        IPlayerController player,
        string profileName,
        string eventKey,
        string cooldownKey,
        [NotNullWhen(true)] out SelectedSound? selected)
    {
        selected = null;
        if (!IsHuman(player, out _)
            || !IsCooldownReady(player, cooldownKey)
            || !_catalog.TrySelect(profileName, eventKey, _random, out selected))
        {
            return false;
        }

        MarkCooldown(player, cooldownKey);
        return true;
    }

    private void EmitSelectedSound(IPlayerController source, SelectedSound selected, VoiceAudience audience)
    {
        var recipients = GetRecipients(source, audience).ToArray();
        if (recipients.Length == 0)
        {
            return;
        }

        try
        {
            if (ShouldUseClientCommand(selected.EventKey))
            {
                foreach (var recipient in recipients)
                {
                    ExecuteClientSound(recipient, selected.Sound.Sound);
                }

                return;
            }

            var soundEventName = _catalog.BuildSoundEventName(selected);
            if (audience == VoiceAudience.Self)
            {
                source.EmitSoundClient(soundEventName, _config.Volume);
                return;
            }

            if (source.GetPlayerPawn() is { } pawn)
            {
                pawn.EmitSound(soundEventName, _config.Volume, new RecipientFilter(recipients));
            }
            else
            {
                foreach (var recipient in recipients)
                {
                    recipient.GetPlayerController()?.EmitSoundClient(soundEventName, _config.Volume);
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to play RoleSound event {EventKey} for {PlayerName}.",
                selected.EventKey,
                source.PlayerName);
        }
    }

    private IEnumerable<IGameClient> GetRecipients(IPlayerController source, VoiceAudience audience)
    {
        if (audience == VoiceAudience.Self)
        {
            return IsHuman(source.GetGameClient(), out _) ? [source.GetGameClient()!] : [];
        }

        var humans = _clients.GetGameClients(inGame: true).Where(IsHumanClient);
        if (audience == VoiceAudience.Everyone)
        {
            return humans;
        }

        if (source.GetPlayerPawn() is not { } sourcePawn)
        {
            return [];
        }

        var origin = sourcePawn.GetAbsOrigin();
        var maximumDistanceSquared = _config.NearbyDistance * _config.NearbyDistance;
        return humans.Where(client =>
            client.GetPlayerController()?.GetPlayerPawn() is { } pawn
            && pawn.GetAbsOrigin().DistToSqr(origin) <= maximumDistanceSquared);
    }

    private void PrintRadioText(IPlayerController player, SelectedSound selected, VoiceAudience audience)
    {
        var text = string.IsNullOrWhiteSpace(selected.Sound.Text)
            ? RoleSoundCatalog.GetRadioDisplayName(selected.EventKey)
            : selected.Sound.Text;
        var message = " " + _config.RadioTextFormat
            .Replace("{prefix}", _config.ChatPrefix, StringComparison.OrdinalIgnoreCase)
            .Replace("{player}", player.PlayerName, StringComparison.OrdinalIgnoreCase)
            .Replace("{role}", $"{ChatRed}{selected.ProfileName}{ChatDefault}", StringComparison.OrdinalIgnoreCase)
            .Replace("{profile}", $"{ChatRed}{selected.ProfileName}{ChatDefault}", StringComparison.OrdinalIgnoreCase)
            .Replace("{text}", $"{ChatPink}{text}{ChatDefault}", StringComparison.OrdinalIgnoreCase)
            .Replace("{event}", selected.EventKey, StringComparison.OrdinalIgnoreCase);

        foreach (var recipient in GetRecipients(player, audience))
        {
            recipient.Print(HudPrintChannel.Chat, message);
        }
    }

    private void PrintCooldownMessage(IPlayerController player, string cooldownKey)
    {
        if (!_config.ShowCooldownMessage
            || !TryGetCooldownRemainingSeconds(player, cooldownKey, out var remainingSeconds))
        {
            return;
        }

        var role = _catalog.ResolveProfileName(GetCurrentModelPath(player))
            ?? RoleSoundCatalog.ExtractModelFolder(GetCurrentModelPath(player) ?? string.Empty)
            ?? "role";
        var message = " " + _config.CooldownMessageFormat
            .Replace("{prefix}", _config.ChatPrefix, StringComparison.OrdinalIgnoreCase)
            .Replace("{player}", player.PlayerName, StringComparison.OrdinalIgnoreCase)
            .Replace("{role}", $"{ChatRed}{role}{ChatDefault}", StringComparison.OrdinalIgnoreCase)
            .Replace("{profile}", $"{ChatRed}{role}{ChatDefault}", StringComparison.OrdinalIgnoreCase)
            .Replace("{event}", cooldownKey, StringComparison.OrdinalIgnoreCase)
            .Replace(
                "{seconds}",
                $"{ChatPink}{Math.Ceiling(remainingSeconds):0}{ChatDefault}",
                StringComparison.OrdinalIgnoreCase);
        player.Print(HudPrintChannel.Chat, message);
    }

    private void PrintDebug(IGameClient client, IPlayerController player)
    {
        var model = GetCurrentModelPath(player) ?? "<none>";
        var folder = RoleSoundCatalog.ExtractModelFolder(model) ?? "<none>";
        var profile = _catalog.ResolveProfileName(model) ?? "<none>";
        var available = profile == "<none>"
            ? "<none>"
            : string.Join(", ", _catalog.GetEvents(profile).Order(StringComparer.OrdinalIgnoreCase));
        client.Print(
            HudPrintChannel.Chat,
            $"{_config.ChatPrefix} model='{model}', folder='{folder}', profile='{profile}', events='{available}'");
    }

    private void Preview(IGameClient client, IPlayerController player, string eventKey)
    {
        var normalizedEvent = RoleSoundCatalog.NormalizeKey(eventKey);
        var profile = _catalog.ResolveProfileName(GetCurrentModelPath(player));
        if (profile is null || !_catalog.TrySelect(profile, normalizedEvent, _random, out var selected))
        {
            client.Print(HudPrintChannel.Chat, $"{_config.ChatPrefix} 当前模型没有事件 '{normalizedEvent}'。");
            return;
        }

        EmitSelectedSound(player, selected, VoiceAudience.Self);
        client.Print(
            HudPrintChannel.Chat,
            $"{_config.ChatPrefix} preview profile='{selected.ProfileName}', event='{normalizedEvent}', soundevent='{_catalog.BuildSoundEventName(selected)}'");
    }

    private void EmitRaw(IGameClient client, IPlayerController player, StringCommand command)
    {
        if (command.ArgCount == 0)
        {
            client.Print(HudPrintChannel.Chat, $"{_config.ChatPrefix} 用法：!rsemit <soundevent>");
            return;
        }

        var soundEventName = command.GetArg(1).Trim();
        player.EmitSoundClient(soundEventName, _config.Volume);
        client.Print(HudPrintChannel.Chat, $"{_config.ChatPrefix} emitted '{soundEventName}'");
    }

    private void PlayClientResource(IGameClient client, IPlayerController player, StringCommand command)
    {
        if (command.ArgCount > 0
            && (command.GetArg(1).Contains('/')
                || command.GetArg(1).EndsWith(".vsnd_c", StringComparison.OrdinalIgnoreCase)))
        {
            ExecuteClientSound(client, command.GetArg(1));
            return;
        }

        var eventKey = command.ArgCount > 0 ? command.GetArg(1) : "radio.roger";
        var profile = _catalog.ResolveProfileName(GetCurrentModelPath(player));
        if (profile is null || !_catalog.TrySelect(profile, eventKey, _random, out var selected))
        {
            client.Print(HudPrintChannel.Chat, $"{_config.ChatPrefix} 当前模型没有事件 '{eventKey}'。");
            return;
        }

        ExecuteClientSound(client, selected.Sound.Sound);
    }

    private void ValidateRepresentativeSoundEvent()
    {
        var profileName = _catalog.ProfileNames.FirstOrDefault(
                              name => name.Equals("anomea", StringComparison.OrdinalIgnoreCase))
            ?? _catalog.ProfileNames.FirstOrDefault();
        if (profileName is null)
        {
            return;
        }

        var eventKey = _catalog.GetEvents(profileName).Contains(DeathEvent, StringComparer.OrdinalIgnoreCase)
            ? DeathEvent
            : _catalog.GetEvents(profileName).FirstOrDefault();
        if (eventKey is null || !_catalog.TrySelect(profileName, eventKey, _random, out var selected))
        {
            return;
        }

        var soundEventName = _catalog.BuildSoundEventName(selected);
        if (_sounds.IsSoundEventValid(soundEventName))
        {
            _logger.LogInformation(
                "Validated RoleSound soundevent {SoundEventName} after game activation.",
                soundEventName);
        }
        else
        {
            _logger.LogWarning(
                "RoleSound soundevent {SoundEventName} is not valid after game activation.",
                soundEventName);
        }
    }

    private bool ShouldUseClientCommand(string eventKey)
        => _config.ClientCommandEvents.Contains(eventKey, StringComparer.OrdinalIgnoreCase)
            || _config.PlaybackMode.Equals("clientcommand", StringComparison.OrdinalIgnoreCase)
            || _config.PlaybackMode.Equals("client", StringComparison.OrdinalIgnoreCase);

    private void ExecuteClientSound(IGameClient client, string soundPath)
        => client.Command(
            $"{_config.ClientCommandName} \"{RoleSoundCatalog.NormalizePath(soundPath).Replace("\"", string.Empty)}\"");

    private bool IsCooldownReady(IPlayerController player, string eventKey)
        => !TryGetCooldownRemainingSeconds(player, eventKey, out _);

    private bool TryGetCooldownRemainingSeconds(
        IPlayerController player,
        string eventKey,
        out double remainingSeconds)
    {
        remainingSeconds = 0;
        var steamId = player.SteamId.AsPrimitive();
        var cooldownSeconds = ResolveCooldownSeconds(eventKey);
        if (steamId == 0
            || cooldownSeconds <= 0
            || !_lastPlayedAt.TryGetValue(steamId, out var cooldowns)
            || !cooldowns.TryGetValue(eventKey, out var lastPlayed))
        {
            return false;
        }

        remainingSeconds = cooldownSeconds - (DateTimeOffset.UtcNow - lastPlayed).TotalSeconds;
        return remainingSeconds > 0;
    }

    private void MarkCooldown(IPlayerController player, string eventKey)
    {
        var steamId = player.SteamId.AsPrimitive();
        if (steamId == 0)
        {
            return;
        }

        if (!_lastPlayedAt.TryGetValue(steamId, out var cooldowns))
        {
            cooldowns = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
            _lastPlayedAt[steamId] = cooldowns;
        }

        cooldowns[eventKey] = DateTimeOffset.UtcNow;
    }

    private bool WasPlayedWithin(IPlayerController player, string eventKey, double seconds)
    {
        var steamId = player.SteamId.AsPrimitive();
        return steamId != 0
            && _lastPlayedAt.TryGetValue(steamId, out var cooldowns)
            && cooldowns.TryGetValue(eventKey, out var lastPlayed)
            && (DateTimeOffset.UtcNow - lastPlayed).TotalSeconds <= seconds;
    }

    private double ResolveCooldownSeconds(string eventKey)
    {
        if (_config.CooldownsSeconds.TryGetValue(eventKey, out var exact))
        {
            return exact;
        }

        if (eventKey.StartsWith(RadioPrefix, StringComparison.OrdinalIgnoreCase)
            && _config.CooldownsSeconds.TryGetValue(RadioCooldownEvent, out var radio))
        {
            return radio;
        }

        return 5;
    }

    private string? GetCurrentModelPath(IPlayerController player)
        => player.GetPlayerPawn()
            ?.GetBodyComponent()
            .GetSceneNode()?
            .AsSkeletonInstance?
            .GetModelState()
            .ModelName;

    private bool IsLastAliveOnTeam(IPlayerController victim)
        => victim.Team is CStrikeTeam.TE or CStrikeTeam.CT
            && !_clients.GetGameClients(inGame: true)
                .Select(client => client.GetPlayerController())
                .Any(player => player is not null
                    && !IsSamePlayer(player, victim)
                    && player.Team == victim.Team
                    && player.GetPawn() is { IsAlive: true });

    private IEnumerable<IPlayerController> GetHumanPlayers()
        => _clients.GetGameClients(inGame: true)
            .Where(IsHumanClient)
            .Select(client => client.GetPlayerController())
            .Where(player => player is not null)
            .Select(player => player!);

    private static bool IsSamePlayer(IPlayerController left, IPlayerController right)
    {
        var leftSteamId = left.SteamId.AsPrimitive();
        var rightSteamId = right.SteamId.AsPrimitive();
        return leftSteamId != 0 && rightSteamId != 0
            ? leftSteamId == rightSteamId
            : left.Index == right.Index;
    }

    private static bool IsHuman(
        IPlayerController? player,
        [NotNullWhen(true)] out IPlayerController? validPlayer)
    {
        validPlayer = player;
        return player?.GetGameClient() is { } client && IsHumanClient(client);
    }

    private static bool IsHuman(
        IGameClient? client,
        [NotNullWhen(true)] out IPlayerController? player)
    {
        player = client?.GetPlayerController();
        return IsHumanClient(client) && player is not null;
    }

    private static bool IsHumanClient([NotNullWhen(true)] IGameClient? client)
        => client is { IsValid: true, IsInGame: true, IsHltv: false }
            && !BotIdentityRegistry.IsBot(client.IsFakeClient, client.Slot.AsPrimitive())
            && client.SteamId.AsPrimitive() != 0;

    private static string ResolveRadioKey(
        string rawKey,
        IReadOnlyDictionary<string, string> mapping,
        string fallback)
    {
        var normalized = RoleSoundCatalog.NormalizeKey(rawKey);
        return mapping.TryGetValue(normalized, out var mapped) && !string.IsNullOrWhiteSpace(mapped)
            ? RoleSoundCatalog.NormalizeKey(mapped)
            : RoleSoundCatalog.NormalizeKey(fallback);
    }

    private static VoiceAudience ResolveAudience(string value, VoiceAudience fallback)
        => Enum.TryParse<VoiceAudience>(value, ignoreCase: true, out var parsed) ? parsed : fallback;

    private static string NormalizeCommandName(string command)
        => command.StartsWith("ms_", StringComparison.OrdinalIgnoreCase)
            ? command[3..].ToLowerInvariant()
            : command.StartsWith("css_", StringComparison.OrdinalIgnoreCase)
                ? command[4..].ToLowerInvariant()
                : command.ToLowerInvariant();

    private static bool IsThrowableWeapon(string weapon)
        => RoleSoundCatalog.NormalizeKey(weapon)
            .Replace("weapon_", string.Empty, StringComparison.OrdinalIgnoreCase)
            is "hegrenade" or "flashbang" or "smokegrenade" or "molotov"
            or "incgrenade" or "decoy" or "tagrenade";
}
