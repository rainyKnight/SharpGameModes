using System.Globalization;
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
using Sharp.Shared.Units;

namespace SharpGameModes.BotMatch;

public sealed class BotMatchModule : IModSharpModule, IGameListener, IClientListener, IEventListener
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
    private readonly IConVarManager _conVars;
    private readonly IEventManager _events;
    private readonly ILogger<BotMatchModule> _logger;
    private readonly string _sharpPath;
    private readonly string _configPath;
    private readonly string _botIdentityPath;
    private readonly string _nadeLineupPath;
    private readonly string _botCosmeticPath;
    private readonly string _botRecordingPath;
    private readonly ConVarLease _conVarLease;
    private readonly Dictionary<int, string> _botRecordingFiles = [];
    private BotMatchConfig _config = new();
    private BotProfileMountRuntime? _botProfileMount;
    private BotIdentityRuntime? _botIdentity;
    private BotControllerRuntime? _botController;
    private BotAiRuntime? _botAi;
    private BotAimRuntime? _botAim;
    private BotStateRuntime? _botState;
    private BotBuyRuntime? _botBuy;
    private NadeSystemRuntime? _nadeSystem;
    private BotCosmeticRuntime? _botCosmetics;
    private RoundDamageRecapRuntime? _damageRecap;
    private IModSharpModuleInterface<IModeContext>? _modeContext;
    private IDisposable? _modeContextSubscription;
    private bool _active;
    private bool _stopping;
    private int _lifecycleGeneration;
    private int _quotaGeneration;
    private int _teamBalanceGeneration;
    private int _identityGeneration;
    private int _identityFastGeneration;
    private int _controllerTestGeneration;
    private bool _teamEliminationHandled;

    public BotMatchModule(
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
        _conVars = sharedSystem.GetConVarManager();
        _events = sharedSystem.GetEventManager();
        _logger = sharedSystem.GetLoggerFactory().CreateLogger<BotMatchModule>();
        _sharpPath = sharpPath;
        _configPath = Path.Combine(sharpPath, "configs", "sharp-gamemodes", "botmatch.jsonc");
        _botIdentityPath = Path.Combine(
            sharpPath,
            "configs",
            "sharp-gamemodes",
            "botmatch-identities",
            "bot_info.json");
        _nadeLineupPath = Path.Combine(
            sharpPath,
            "configs",
            "sharp-gamemodes",
            "botmatch-grenades");
        _botCosmeticPath = Path.Combine(
            sharpPath,
            "configs",
            "sharp-gamemodes",
            "botmatch-cosmetics");
        _botRecordingPath = Path.Combine(
            sharpPath,
            "data",
            "sharp-gamemodes",
            "botcontroller-recordings");
        _conVarLease = new ConVarLease(sharedSystem.GetConVarManager(), _logger);
    }

    public string DisplayName => "SharpGameModes Enhanced Bot Match";
    public string DisplayAuthor => "SharpGameModes Contributors";
    public int ListenerVersion => IGameListener.ApiVersion;
    public int ListenerPriority => 10;

    internal bool IsActive => _active && !_stopping;
    internal int LifecycleGeneration => _lifecycleGeneration;

    public bool Init()
    {
        try
        {
            _config = JsonSerializer.Deserialize<BotMatchConfig>(
                File.ReadAllText(_configPath),
                SerializerOptions) ?? throw new InvalidDataException("Bot-match config is empty.");
            _config.Validate();
            var gameRoot = Path.GetFullPath(Path.Combine(_sharpPath, ".."));
            _botProfileMount = new BotProfileMountRuntime(
                _shared.GetFileManager(),
                _logger,
                Path.Combine(gameRoot, "csgo", "overrides"),
                _config.DifficultyTier);
            if (!_botProfileMount.Mount("module initialization"))
            {
                throw new InvalidDataException(_botProfileMount.GetStatus());
            }

            Directory.CreateDirectory(_botRecordingPath);
            _botIdentity = new BotIdentityRuntime(
                _shared,
                _clients,
                _logger,
                _config,
                _botIdentityPath);
            _botController = new BotControllerRuntime(_shared, _clients, _logger);
            _botAi = new BotAiRuntime(_shared, _logger);
            _botAim = new BotAimRuntime(_shared, _clients, _logger);
            _botState = new BotStateRuntime(_shared, _clients, _botController, _logger);
            _botBuy = new BotBuyRuntime(_shared, _clients, _logger);
            _nadeSystem = new NadeSystemRuntime(_shared, _clients, _logger);
            _botCosmetics = new BotCosmeticRuntime(
                _shared,
                _clients,
                _logger,
                _botCosmeticPath);
            _damageRecap = new RoundDamageRecapRuntime(
                _shared,
                _clients,
                _logger,
                _config.DifficultyTier,
                _config.DamageRecapStyle);
            _modSharp.InstallGameListener(this);
            _clients.InstallClientListener(this);
            _clients.InstallCommandCallback("bot_aim", OnBotAimCommand);
            _clients.InstallCommandCallback("bot_nades", OnBotNadesCommand);
            _clients.InstallCommandCallback("br_reroll", OnBotCosmeticRerollCommand);
            _clients.InstallCommandCallback("botstate_flashdebug", OnFlashDebugCommand);
            _clients.InstallCommandCallback("damage_style", OnDamageStyleCommand);
            _clients.InstallCommandCallback("bc_record", OnBotControllerRecordCommand);
            _clients.InstallCommandCallback("bc_stoprecord", OnBotControllerStopRecordCommand);
            _clients.InstallCommandCallback("bc_replay", OnBotControllerReplayCommand);
            _clients.InstallCommandCallback("bc_stopreplay", OnBotControllerStopReplayCommand);
            _conVars.CreateServerCommand(
                "bot_aim",
                OnServerBotAimCommand,
                "Set enhanced bot aim mode: head, body or mixed.");
            _conVars.CreateServerCommand(
                "bot_nades",
                OnServerBotNadesCommand,
                "Set enhanced bot grenade mode: off, less, normal, more or max.");
            _conVars.CreateServerCommand(
                "bot_nades_test",
                OnServerBotNadesTestCommand,
                "Spawn a diagnostic bot grenade: flash, smoke, he or molotov.");
            _conVars.CreateServerCommand(
                "br_reroll",
                OnServerBotCosmeticRerollCommand,
                "Queue new bot cosmetic loadouts: br_reroll [all|bot slot].");
            _conVars.CreateServerCommand(
                "br_status",
                OnServerBotCosmeticStatusCommand,
                "Show pure ModSharp BotRandomizer status.");
            _conVars.CreateServerCommand(
                "botbuy_status",
                OnServerBotBuyStatusCommand,
                "Show pure ModSharp BotBuy status.");
            _conVars.CreateServerCommand(
                "botai_status",
                OnServerBotAiStatusCommand,
                "Show pure ModSharp BotAI patch status.");
            _conVars.CreateServerCommand(
                "botprofile_status",
                OnServerBotProfileStatusCommand,
                "Show the mounted upstream BotProfile tier and resolved database.");
            _conVars.CreateServerCommand(
                "bc_status",
                OnServerBotControllerStatusCommand,
                "Show pure ModSharp BotController ABI and runtime status.");
            _conVars.CreateServerCommand(
                "bc_lock",
                OnServerBotControllerLockCommand,
                "Lock a bot: bc_lock <slot> <all|aim|jump|slot1..slot5>.");
            _conVars.CreateServerCommand(
                "bc_unlock",
                OnServerBotControllerUnlockCommand,
                "Unlock a bot: bc_unlock <slot> <all|aim|jump|weapon>.");
            _conVars.CreateServerCommand(
                "bc_motiontest",
                OnServerBotControllerMotionTestCommand,
                "Record one live bot and replay the motion on a teammate: bc_motiontest [seconds].");
            _conVars.CreateServerCommand(
                "bh_status",
                OnServerBotHiderStatusCommand,
                "Show pure ModSharp BotHider status.");
            _conVars.CreateServerCommand(
                "bh_setsid",
                OnServerBotHiderSetSidCommand,
                "Set a managed bot SteamID64: bh_setsid <slot> <sid64>.");
            _conVars.CreateServerCommand(
                "bh_setname",
                OnServerBotHiderSetNameCommand,
                "Set a managed bot name: bh_setname <slot> <name>.");
            _conVars.CreateServerCommand(
                "bh_setflair",
                OnServerBotHiderSetFlairCommand,
                "Set a managed bot scoreboard flair: bh_setflair <slot> <item_def_index>.");
            _conVars.CreateServerCommand(
                "bh_setcrosshair",
                OnServerBotHiderSetCrosshairCommand,
                "Set a managed bot crosshair: bh_setcrosshair <slot> <code|0>.");
            _conVars.CreateServerCommand(
                "bh_setavatar",
                OnServerBotHiderSetAvatarCommand,
                "Set a managed bot avatar: bh_setavatar <slot> <png_path|0>.");
            _conVars.CreateServerCommand(
                "bh_disguise",
                OnServerBotHiderDisguiseCommand,
                "Toggle bot disguise: bh_disguise <0|1>.");
            _conVars.CreateServerCommand(
                "bh_namesource",
                OnServerBotHiderNameSourceCommand,
                "Set new-bot name source: bh_namesource <0|1>.");
            _conVars.CreateServerCommand(
                "botstate_flashdebug",
                OnServerFlashDebugCommand,
                "Toggle BotState flash avoidance diagnostics.");
            _conVars.CreateServerCommand(
                "damage_style",
                OnServerDamageStyleCommand,
                "Set round damage recap style: auto, classic or pw.");
            _conVars.CreateServerCommand(
                "damage_recap_status",
                OnServerDamageRecapStatusCommand,
                "Show pure ModSharp RoundDamageRecap status.");
            _events.InstallEventListener(this);
            _events.HookEvent("player_team");
            _events.HookEvent("player_death");
            _events.HookEvent("player_hurt");
            _events.HookEvent("player_blind");
            _events.HookEvent("round_start");
            _events.HookEvent("round_prestart");
            _events.HookEvent("round_freeze_end");
            _events.HookEvent("round_end");
            _events.HookEvent("player_spawn");
            _events.HookEvent("round_mvp");
            _events.HookEvent("item_pickup");
            _events.HookEvent("weapon_fire");
            _events.HookEvent("weapon_reload");
            _events.HookEvent("weapon_zoom");
            _events.HookEvent("grenade_thrown");
            _events.HookEvent("player_jump");
            _events.HookEvent("bomb_planted");
            _events.HookEvent("bomb_beginplant");
            _events.HookEvent("bomb_begindefuse");
            _events.HookEvent("bomb_abortdefuse");
            _events.HookEvent("bomb_defused");
            _events.HookEvent("bomb_exploded");
            _events.HookEvent("door_open");
            _events.HookEvent("door_close");
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or JsonException
            or InvalidDataException
            or ArgumentException)
        {
            _botProfileMount?.Dispose();
            _botProfileMount = null;
            _logger.LogError(exception, "Failed to initialize bot-match runtime from {Path}.", _configPath);
            return false;
        }
    }

    public void PostInit()
    {
        if (_botIdentity is null || _botController is null)
        {
            return;
        }

        _shared.GetSharpModuleManager().RegisterSharpModuleInterface<IBotHider>(
            this,
            IBotHider.Identity,
            _botIdentity);
        _logger.LogInformation(
            "Pure ModSharp BotHider module interface registered as {Identity}.",
            IBotHider.Identity);
        _shared.GetSharpModuleManager().RegisterSharpModuleInterface<IBotController>(
            this,
            IBotController.Identity,
            _botController);
        _logger.LogInformation(
            "Pure ModSharp BotController ABI {Abi} module interface registered as {Identity}.",
            _botController.AbiVersion,
            IBotController.Identity);
    }

    public void OnAllModulesLoaded() => RefreshModeContext();

    public void OnServerInit()
    {
        if (_botProfileMount?.Mount("server initialization") == false)
        {
            _logger.LogCritical(
                "BotProfile remount failed during server initialization; BotMatch activation will remain blocked instead of silently using stock Bot profiles.");
        }
    }

    public void OnServerSpawn()
    {
        if (IsActive)
        {
            _botAi?.ResetMap();
            _nadeSystem?.ReloadForCurrentMap();
            _botBuy?.ResetMap();
            _botCosmetics?.ResetMap();
            _damageRecap?.ResetMap();
            ScheduleBotQuotaReconciliation(1);
        }
    }

    public void OnResourcePrecache()
        => _botCosmetics?.PrecacheModels();

    public void OnLibraryConnected(string name)
    {
        if (name.Equals("SharpGameModes.Core", StringComparison.OrdinalIgnoreCase))
        {
            RefreshModeContext();
        }
    }

    public void OnLibraryDisconnect(string name)
    {
        if (!name.Equals("SharpGameModes.Core", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _modeContextSubscription?.Dispose();
        _modeContextSubscription = null;
        _modeContext = null;
        Deactivate();
    }

    public void Shutdown()
    {
        _stopping = true;
        _teamBalanceGeneration++;
        _identityGeneration++;
        _identityFastGeneration++;
        _modeContextSubscription?.Dispose();
        _modeContextSubscription = null;
        _modeContext = null;
        Deactivate();
        _botIdentity?.Dispose();
        _botIdentity = null;
        _botProfileMount?.Dispose();
        _botProfileMount = null;
        _botController?.Dispose();
        _botController = null;
        _botAi?.Dispose();
        _botAi = null;
        _botAim?.Dispose();
        _botAim = null;
        _botState?.Dispose();
        _botState = null;
        _botBuy?.Dispose();
        _botBuy = null;
        _nadeSystem?.Dispose();
        _nadeSystem = null;
        _botCosmetics?.Dispose();
        _botCosmetics = null;
        _damageRecap?.Dispose();
        _damageRecap = null;
        _events.RemoveEventListener(this);
        _conVars.ReleaseCommand("damage_recap_status");
        _conVars.ReleaseCommand("damage_style");
        _conVars.ReleaseCommand("bh_namesource");
        _conVars.ReleaseCommand("bh_disguise");
        _conVars.ReleaseCommand("bh_setavatar");
        _conVars.ReleaseCommand("bh_setcrosshair");
        _conVars.ReleaseCommand("bh_setflair");
        _conVars.ReleaseCommand("bh_setname");
        _conVars.ReleaseCommand("bh_setsid");
        _conVars.ReleaseCommand("bh_status");
        _conVars.ReleaseCommand("bc_unlock");
        _conVars.ReleaseCommand("bc_lock");
        _conVars.ReleaseCommand("bc_motiontest");
        _conVars.ReleaseCommand("bc_status");
        _conVars.ReleaseCommand("botprofile_status");
        _conVars.ReleaseCommand("botai_status");
        _conVars.ReleaseCommand("botbuy_status");
        _conVars.ReleaseCommand("br_status");
        _conVars.ReleaseCommand("br_reroll");
        _conVars.ReleaseCommand("botstate_flashdebug");
        _conVars.ReleaseCommand("bot_nades_test");
        _conVars.ReleaseCommand("bot_nades");
        _conVars.ReleaseCommand("bot_aim");
        _clients.RemoveCommandCallback("botstate_flashdebug", OnFlashDebugCommand);
        _clients.RemoveCommandCallback("damage_style", OnDamageStyleCommand);
        _clients.RemoveCommandCallback("br_reroll", OnBotCosmeticRerollCommand);
        _clients.RemoveCommandCallback("bc_stopreplay", OnBotControllerStopReplayCommand);
        _clients.RemoveCommandCallback("bc_replay", OnBotControllerReplayCommand);
        _clients.RemoveCommandCallback("bc_stoprecord", OnBotControllerStopRecordCommand);
        _clients.RemoveCommandCallback("bc_record", OnBotControllerRecordCommand);
        _clients.RemoveCommandCallback("bot_nades", OnBotNadesCommand);
        _clients.RemoveCommandCallback("bot_aim", OnBotAimCommand);
        _clients.RemoveClientListener(this);
        _modSharp.RemoveGameListener(this);
    }

    public void OnClientConnected(IGameClient client)
    {
        if (IsActive)
        {
            _botIdentity?.OnClientConnected(client);
        }
    }

    public void OnClientPutInServer(IGameClient client)
    {
        if (IsActive)
        {
            _damageRecap?.OnClientPutInServer(client);
            ScheduleIdentityReconcile(client, 0.2);
            StartIdentityFastApplyWindow();
            ScheduleBotTeamBalance();
        }
    }

    public void OnClientSettingChanged(IGameClient client)
    {
        if (IsActive)
        {
            _botIdentity?.OnClientSettingChanged(client);
        }
    }

    public void OnClientDisconnecting(IGameClient client, NetworkDisconnectionReason reason)
    {
        _botRecordingFiles.Remove(client.Slot.AsPrimitive());
        if (IsActive)
        {
            _botIdentity?.Release(client);
            _botController?.Release(client);
            _botAim?.Release(client);
            _botState?.Release(client);
            _botBuy?.Release(client);
            _nadeSystem?.Release(client);
            _botCosmetics?.Release(client);
            _damageRecap?.Release(client);
            ScheduleBotTeamBalance();
        }
    }

    public void FireGameEvent(IGameEvent gameEvent)
    {
        if (!IsActive)
        {
            return;
        }

        _botState?.HandleGameEvent(gameEvent);
        _botAi?.HandleGameEvent(gameEvent);
        _botBuy?.HandleGameEvent(gameEvent);
        _nadeSystem?.HandleGameEvent(gameEvent);
        _botCosmetics?.HandleGameEvent(gameEvent);
        _damageRecap?.HandleGameEvent(gameEvent);
        if (gameEvent.Name is "round_prestart"
            or "round_start"
            or "round_freeze_end"
            or "player_spawn"
            or "player_team")
        {
            StartIdentityFastApplyWindow();
        }
        switch (gameEvent.Name)
        {
            case "player_team" when gameEvent is IEventPlayerTeam { Disconnect: false }:
                ScheduleBotTeamBalance();
                break;
            case "player_death" when gameEvent is IEventPlayerDeath death:
                HandleTeamElimination(death);
                break;
            case "round_start":
                _teamEliminationHandled = false;
                _botController?.UnlockAllWeapons();
                _botAim?.ClearCache();
                break;
        }
    }

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
            Deactivate();
            return;
        }

        _modeContextSubscription = context.Subscribe(ApplyModeContext);
        if (context.Current is { } snapshot)
        {
            ApplyModeContext(snapshot);
        }
    }

    private void ApplyModeContext(ModeContextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_config.Enabled && snapshot.Selection.Mode == ModeId.BotMatch)
        {
            Activate();
        }
        else
        {
            Deactivate();
        }
    }

    private void Activate()
    {
        if (_active || _stopping)
        {
            return;
        }

        if (_botProfileMount?.IsReady != true)
        {
            _logger.LogCritical(
                "BotMatch activation was blocked because the selected upstream BotProfile database is not mounted.");
            return;
        }

        _active = true;
        _lifecycleGeneration++;
        _teamBalanceGeneration++;
        _identityGeneration++;
        _identityFastGeneration++;
        var desired = new Dictionary<string, string>(_config.ConVars, StringComparer.Ordinal)
        {
            ["bot_quota"] = "0",
            ["bot_quota_mode"] = _config.BotQuotaMode,
        };
        _conVarLease.Acquire(desired);
        if (_botIdentity?.Activate() == false)
        {
            _logger.LogError(
                "BotMatch will continue, but BotHider is disabled because its native signatures or offsets could not be resolved.");
        }

        if (_botController?.Activate() == false)
        {
            _logger.LogError(
                "BotMatch will continue, but BotController is disabled because its schema fields or PlayerRunCommand hooks could not be installed.");
        }
        if (_config.EnableBotAi && _botAi?.Activate() == false)
        {
            _logger.LogError(
                "BotMatch will continue, but BotAI is disabled because at least one signature, original byte sequence or schema field could not be validated.");
        }

        if (_botAim?.Activate(_config.AimMode) == false)
        {
            _logger.LogError(
                "BotMatch will continue, but BotAimImprover is disabled because PickNewAimSpot could not be hooked.");
        }

        if (_config.EnableBotState && _botState?.Activate() == false)
        {
            _logger.LogError(
                "BotMatch will continue, but BotState core is disabled because its schema fields could not be resolved.");
        }

        if (_config.EnableBotBuy)
        {
            _botBuy?.Activate();
        }

        if (_nadeSystem?.Activate(_config.NadeMode, _nadeLineupPath) == false)
        {
            _logger.LogError(
                "BotMatch will continue, but NadeSystem is disabled because its native factories, schema fields or lineup data could not be resolved.");
        }

        if (_config.EnableBotCosmetics
            && _botCosmetics?.Activate() == false)
        {
            _logger.LogError(
                "BotMatch will continue, but BotRandomizer is disabled because its catalog, native attribute writer or schema fields could not be resolved.");
        }

        if (_config.EnableDamageRecap)
        {
            _damageRecap?.Activate();
        }

        _teamEliminationHandled = false;
        ScheduleBotQuotaReconciliation(0.5);
        StartIdentityReconciliationLoop();
        StartIdentityFastApplyWindow();
        _logger.LogInformation(
            "Pure ModSharp bot-match runtime enabled with quota {BotQuota}, aim {AimMode}, nades {NadeMode}.",
            _config.BotQuota,
            _config.AimMode,
            _config.NadeMode);
    }

    private void Deactivate()
    {
        if (!_active)
        {
            return;
        }

        _active = false;
        _lifecycleGeneration++;
        _quotaGeneration++;
        _teamBalanceGeneration++;
        _identityGeneration++;
        _identityFastGeneration++;
        _controllerTestGeneration++;
        _teamEliminationHandled = false;
        _botRecordingFiles.Clear();
        _damageRecap?.Deactivate();
        _botBuy?.Deactivate();
        _nadeSystem?.Deactivate();
        _botCosmetics?.Deactivate();
        _botState?.Deactivate();
        _botAim?.Deactivate();
        _botAi?.Deactivate();
        _botController?.Deactivate();
        _botIdentity?.Deactivate();
        _conVarLease.Release();
        _logger.LogInformation("Pure ModSharp bot-match runtime disabled and leased ConVars restored.");
    }

    private ECommandAction OnBotAimCommand(IGameClient client, StringCommand command)
    {
        var slot = client.Slot.AsPrimitive();
        if (!client.IsValid
            || client.IsHltv
            || BotIdentityRegistry.IsBot(client.IsFakeClient, slot))
        {
            return ECommandAction.Handled;
        }

        if (!IsActive || _botAim is not { } runtime)
        {
            client.Print(HudPrintChannel.Chat, $"{_config.Prefix} bot_aim 只在人机对抗模式中启用。");
            return ECommandAction.Handled;
        }

        if (command.ArgCount > 0 && !runtime.TrySetMode(command.GetArg(1)))
        {
            client.Print(
                HudPrintChannel.Chat,
                $"{_config.Prefix} 用法：!bot_aim head、body 或 mixed。当前：{runtime.CurrentMode}。");
            return ECommandAction.Handled;
        }

        client.Print(
            HudPrintChannel.Chat,
            $"{_config.Prefix} 人机瞄准模式：{runtime.CurrentMode}（head/body/mixed）。");
        return ECommandAction.Handled;
    }

    private ECommandAction OnBotControllerRecordCommand(
        IGameClient client,
        StringCommand command)
    {
        if (!TryGetHumanBotControllerClient(client, out var runtime))
        {
            return ECommandAction.Handled;
        }

        var requestedName = command.ArgCount > 0 ? command.GetArg(1) : null;
        if (command.ArgCount > 1
            || !BotMotionStore.TryResolvePath(
                _botRecordingPath,
                requestedName,
                client.SteamId.AsPrimitive(),
                out var path))
        {
            client.Print(
                HudPrintChannel.Chat,
                $"{_config.Prefix} 用法：!bc_record [文件名]（仅字母、数字、_、-）。");
            return ECommandAction.Handled;
        }

        var slot = client.Slot.AsPrimitive();
        if (!runtime.StartRecord(slot))
        {
            client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 开始录制失败。");
            return ECommandAction.Handled;
        }

        _botRecordingFiles[slot] = path;
        client.Print(
            HudPrintChannel.Chat,
            $"{_config.Prefix} 已开始录制，使用 !bc_stoprecord 保存。");
        return ECommandAction.Handled;
    }

    private ECommandAction OnBotControllerStopRecordCommand(
        IGameClient client,
        StringCommand command)
    {
        if (!TryGetHumanBotControllerClient(client, out var runtime))
        {
            return ECommandAction.Handled;
        }

        var slot = client.Slot.AsPrimitive();
        _ = runtime.StopRecord(slot);
        if (!_botRecordingFiles.Remove(slot, out var path)
            && !BotMotionStore.TryResolvePath(
                _botRecordingPath,
                null,
                client.SteamId.AsPrimitive(),
                out path))
        {
            return ECommandAction.Handled;
        }

        try
        {
            var saved = BotMotionStore.Save(
                path,
                runtime.GetRecordedMotion(slot),
                tickrate: 64);
            client.Print(
                HudPrintChannel.Chat,
                saved > 0
                    ? $"{_config.Prefix} 已保存 {saved} tick。"
                    : $"{_config.Prefix} 没有可保存的录制。");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            _logger.LogWarning(exception, "Failed to save BotController recording for slot {Slot}.", slot);
            client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 保存录制失败。");
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnBotControllerReplayCommand(
        IGameClient client,
        StringCommand command)
    {
        if (!TryGetHumanBotControllerClient(client, out var runtime))
        {
            return ECommandAction.Handled;
        }

        var requestedName = command.ArgCount > 1 ? command.GetArg(2) : null;
        if (command.ArgCount is < 1 or > 2
            || !int.TryParse(
                command.GetArg(1),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var botSlot)
            || !BotMotionStore.TryResolvePath(
                _botRecordingPath,
                requestedName,
                client.SteamId.AsPrimitive(),
                out var path))
        {
            client.Print(
                HudPrintChannel.Chat,
                $"{_config.Prefix} 用法：!bc_replay <bot slot> [文件名]。");
            return ECommandAction.Handled;
        }

        try
        {
            var recording = BotMotionStore.Load(path);
            if (recording.Ticks.Length == 0)
            {
                client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 录制不存在或为空。");
                return ECommandAction.Handled;
            }

            if (recording.Tickrate != 64)
            {
                client.Print(
                    HudPrintChannel.Chat,
                    $"{_config.Prefix} 警告：录制 tickrate 为 {recording.Tickrate}，服务器按 64 回放。");
            }

            if (runtime.LoadReplay(botSlot, recording.Ticks, recording.Subticks)
                && runtime.StartReplay(botSlot))
            {
                client.Print(
                    HudPrintChannel.Chat,
                    $"{_config.Prefix} 已在 bot slot {botSlot} 开始回放。");
            }
            else
            {
                client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 开始回放失败。");
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            _logger.LogWarning(exception, "Failed to load BotController recording {Path}.", path);
            client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 读取录制失败。");
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnBotControllerStopReplayCommand(
        IGameClient client,
        StringCommand command)
    {
        if (!TryGetHumanBotControllerClient(client, out var runtime))
        {
            return ECommandAction.Handled;
        }

        if (command.ArgCount != 1
            || !int.TryParse(
                command.GetArg(1),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var botSlot))
        {
            client.Print(
                HudPrintChannel.Chat,
                $"{_config.Prefix} 用法：!bc_stopreplay <bot slot>。");
            return ECommandAction.Handled;
        }

        _ = runtime.StopReplay(botSlot);
        client.Print(
            HudPrintChannel.Chat,
            $"{_config.Prefix} 已停止 bot slot {botSlot} 的回放。");
        return ECommandAction.Handled;
    }

    private bool TryGetHumanBotControllerClient(
        IGameClient client,
        out BotControllerRuntime runtime)
    {
        runtime = null!;
        var slot = client.Slot.AsPrimitive();
        if (!client.IsValid
            || client.IsHltv
            || BotIdentityRegistry.IsBot(client.IsFakeClient, slot))
        {
            return false;
        }

        if (!IsActive || _botController is not { IsActive: true } controller)
        {
            client.Print(
                HudPrintChannel.Chat,
                $"{_config.Prefix} BotController 只在人机对抗模式中启用。");
            return false;
        }

        runtime = controller;
        return true;
    }

    private ECommandAction OnServerBotAimCommand(StringCommand command)
    {
        if (!IsActive || _botAim is not { } runtime)
        {
            _logger.LogInformation("bot_aim is available only while BotMatch is active.");
            return ECommandAction.Handled;
        }

        if (command.ArgCount > 0 && !runtime.TrySetMode(command.GetArg(1)))
        {
            _logger.LogInformation(
                "Invalid bot_aim value. Use head, body or mixed; current mode is {Mode}.",
                runtime.CurrentMode);
            return ECommandAction.Handled;
        }

        _logger.LogInformation("Current BotAimImprover mode is {Mode}.", runtime.CurrentMode);
        return ECommandAction.Handled;
    }

    private ECommandAction OnBotNadesCommand(
        IGameClient client,
        StringCommand command)
    {
        var slot = client.Slot.AsPrimitive();
        if (!client.IsValid
            || client.IsHltv
            || BotIdentityRegistry.IsBot(client.IsFakeClient, slot))
        {
            return ECommandAction.Handled;
        }

        if (!IsActive || _nadeSystem is not { } runtime)
        {
            client.Print(
                HudPrintChannel.Chat,
                $"{_config.Prefix} bot_nades 只在人机对抗模式中启用。");
            return ECommandAction.Handled;
        }

        if (command.ArgCount > 0
            && !runtime.TrySetMode(command.GetArg(1)))
        {
            client.Print(
                HudPrintChannel.Chat,
                $"{_config.Prefix} 用法：!bot_nades off、less、normal、more 或 max。当前：{runtime.CurrentMode}。");
            return ECommandAction.Handled;
        }

        client.Print(
            HudPrintChannel.Chat,
            $"{_config.Prefix} 人机投掷物模式：{runtime.CurrentMode}（off/less/normal/more/max）。");
        return ECommandAction.Handled;
    }

    private ECommandAction OnServerBotNadesCommand(StringCommand command)
    {
        if (!IsActive || _nadeSystem is not { } runtime)
        {
            _logger.LogInformation(
                "bot_nades is available only while BotMatch is active.");
            return ECommandAction.Handled;
        }

        if (command.ArgCount > 0
            && !runtime.TrySetMode(command.GetArg(1)))
        {
            _logger.LogInformation(
                "Invalid bot_nades value. Use off, less, normal, more or max; current mode is {Mode}.",
                runtime.CurrentMode);
            return ECommandAction.Handled;
        }

        _logger.LogInformation(
            "Current NadeSystem mode is {Mode}.",
            runtime.CurrentMode);
        return ECommandAction.Handled;
    }

    private ECommandAction OnServerBotNadesTestCommand(StringCommand command)
    {
        if (!IsActive || _nadeSystem is not { } runtime)
        {
            _logger.LogInformation(
                "bot_nades_test is available only while BotMatch is active.");
            return ECommandAction.Handled;
        }

        if (command.ArgCount == 0)
        {
            _logger.LogInformation(
                "Usage: bot_nades_test <flash|smoke|he|molotov>.");
            return ECommandAction.Handled;
        }

        runtime.TryDiagnosticSpawn(command.GetArg(1), out var result);
        _logger.LogInformation("{Result}", result);
        return ECommandAction.Handled;
    }

    private ECommandAction OnBotCosmeticRerollCommand(
        IGameClient client,
        StringCommand command)
    {
        var slot = client.Slot.AsPrimitive();
        if (!client.IsValid
            || client.IsHltv
            || BotIdentityRegistry.IsBot(client.IsFakeClient, slot))
        {
            return ECommandAction.Handled;
        }

        if (!IsActive || _botCosmetics is not { } runtime)
        {
            client.Print(
                HudPrintChannel.Chat,
                $"{_config.Prefix} br_reroll 只在人机对抗模式中启用。");
            return ECommandAction.Handled;
        }

        var target = command.ArgCount > 0 ? command.GetArg(1) : "all";
        runtime.QueueReroll(target, out var result);
        client.Print(HudPrintChannel.Chat, $"{_config.Prefix} {result}");
        return ECommandAction.Handled;
    }

    private ECommandAction OnServerBotCosmeticRerollCommand(
        StringCommand command)
    {
        if (!IsActive || _botCosmetics is not { } runtime)
        {
            _logger.LogInformation(
                "br_reroll is available only while BotMatch is active.");
            return ECommandAction.Handled;
        }

        var target = command.ArgCount > 0 ? command.GetArg(1) : "all";
        runtime.QueueReroll(target, out var result);
        _logger.LogInformation("{Result}", result);
        return ECommandAction.Handled;
    }

    private ECommandAction OnServerBotCosmeticStatusCommand(
        StringCommand command)
    {
        _logger.LogInformation(
            "{Status}",
            IsActive && _botCosmetics is { } runtime
                ? runtime.GetStatus()
                : "BotRandomizer is available only while BotMatch is active.");
        return ECommandAction.Handled;
    }

    private ECommandAction OnServerBotBuyStatusCommand(
        StringCommand command)
    {
        _logger.LogInformation(
            "{Status}",
            IsActive && _config.EnableBotBuy && _botBuy is { } runtime
                ? runtime.GetStatus()
                : "BotBuy is available only while BotMatch is active.");
        return ECommandAction.Handled;
    }

    private ECommandAction OnServerBotAiStatusCommand(
        StringCommand command)
    {
        _logger.LogInformation(
            "{Status}",
            IsActive && _config.EnableBotAi && _botAi is { } runtime
                ? runtime.GetStatus()
                : "BotAI is available only while BotMatch is active.");
        return ECommandAction.Handled;
    }

    private ECommandAction OnServerBotProfileStatusCommand(
        StringCommand command)
    {
        _logger.LogInformation(
            "{Status}",
            _botProfileMount?.GetStatus() ?? "BotProfile runtime unavailable.");
        return ECommandAction.Handled;
    }

    private ECommandAction OnServerBotControllerStatusCommand(
        StringCommand command)
    {
        _logger.LogInformation(
            "{Status}",
            _botController?.GetStatus() ?? "BotController runtime unavailable.");
        return ECommandAction.Handled;
    }

    private ECommandAction OnServerBotControllerLockCommand(
        StringCommand command)
    {
        if (!TryParseBotControllerSlotCommand(command, out var runtime, out var slot, out var kind))
        {
            _logger.LogInformation(
                "Usage: bc_lock <slot> <all|aim|jump|slot1|slot2|slot3|slot4|slot5>.");
            return ECommandAction.Handled;
        }

        var locked = kind switch
        {
            "all" => runtime.Lock(slot, BotLockKind.All),
            "aim" => runtime.Lock(slot, BotLockKind.Aim),
            "jump" => runtime.Lock(slot, BotLockKind.Jump),
            "slot1" => runtime.Lock(slot, BotLockTarget.Slot1),
            "slot2" => runtime.Lock(slot, BotLockTarget.Slot2),
            "slot3" => runtime.Lock(slot, BotLockTarget.Slot3),
            "slot4" => runtime.Lock(slot, BotLockTarget.Slot4),
            "slot5" => runtime.Lock(slot, BotLockTarget.Slot5),
            _ => false,
        };
        _logger.LogInformation(
            "BotController lock slot {Slot} kind {Kind}: {Result}.",
            slot,
            kind,
            locked);
        return ECommandAction.Handled;
    }

    private ECommandAction OnServerBotControllerUnlockCommand(
        StringCommand command)
    {
        if (!TryParseBotControllerSlotCommand(command, out var runtime, out var slot, out var kind))
        {
            _logger.LogInformation(
                "Usage: bc_unlock <slot> <all|aim|jump|weapon>.");
            return ECommandAction.Handled;
        }

        var unlocked = kind switch
        {
            "all" => runtime.Unlock(slot, BotLockKind.All),
            "aim" => runtime.Unlock(slot, BotLockKind.Aim),
            "jump" => runtime.Unlock(slot, BotLockKind.Jump),
            "weapon" => runtime.Unlock(slot, BotLockKind.Weapon),
            _ => false,
        };
        _logger.LogInformation(
            "BotController unlock slot {Slot} kind {Kind}: {Result}.",
            slot,
            kind,
            unlocked);
        return ECommandAction.Handled;
    }

    private ECommandAction OnServerBotControllerMotionTestCommand(
        StringCommand command)
    {
        if (!IsActive
            || _botController is not { IsActive: true } runtime
            || command.ArgCount > 1)
        {
            _logger.LogInformation("Usage: bc_motiontest [seconds between 1 and 5].");
            return ECommandAction.Handled;
        }

        var durationSeconds = 1d;
        if (command.ArgCount == 1
            && (!double.TryParse(
                    command.GetArg(1),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out durationSeconds)
                || durationSeconds is < 1 or > 5))
        {
            _logger.LogInformation("Usage: bc_motiontest [seconds between 1 and 5].");
            return ECommandAction.Handled;
        }

        var pair = _clients.GetGameClients(inGame: true)
            .Where(client =>
                client.IsValid
                && BotIdentityRegistry.IsBot(
                    client.IsFakeClient,
                    client.Slot.AsPrimitive())
                && client.GetPlayerController() is
                {
                    Team: CStrikeTeam.CT or CStrikeTeam.TE,
                } controller
                && controller.GetPlayerPawn() is
                {
                    IsValidEntity: true,
                    IsAlive: true,
                    Health: > 0,
                })
            .GroupBy(client => client.GetPlayerController()!.Team)
            .Select(group => group.Take(2).ToArray())
            .FirstOrDefault(group => group.Length == 2);
        if (pair is null)
        {
            _logger.LogWarning(
                "BotController motion self-test requires two living managed bots on the same team.");
            return ECommandAction.Handled;
        }

        var sourceSlot = pair[0].Slot.AsPrimitive();
        var destinationSlot = pair[1].Slot.AsPrimitive();
        var sourceLock = runtime.GetWeaponLock(sourceSlot);
        var destinationLock = runtime.GetWeaponLock(destinationSlot);
        _ = runtime.StopReplay(destinationSlot);
        _ = runtime.Unlock(sourceSlot, BotLockKind.Weapon);
        _ = runtime.Lock(sourceSlot, BotLockTarget.Slot3);
        if (!runtime.StartRecord(sourceSlot))
        {
            RestoreBotControllerWeaponLock(runtime, sourceSlot, sourceLock);
            _logger.LogWarning(
                "BotController motion self-test could not start recording slot {SourceSlot}.",
                sourceSlot);
            return ECommandAction.Handled;
        }

        var lifecycleGeneration = _lifecycleGeneration;
        var testGeneration = ++_controllerTestGeneration;
        var baselineReplayed = runtime.ReplayedTickCount;
        var baselineErrors = runtime.ErrorCount;
        _logger.LogInformation(
            "BotController motion self-test started: source slot {SourceSlot}, destination slot {DestinationSlot}, duration {Duration:F1}s.",
            sourceSlot,
            destinationSlot,
            durationSeconds);
        _modSharp.PushTimer(
            () => CompleteBotControllerMotionRecording(
                runtime,
                sourceSlot,
                destinationSlot,
                sourceLock,
                destinationLock,
                durationSeconds,
                baselineReplayed,
                baselineErrors,
                lifecycleGeneration,
                testGeneration),
            durationSeconds,
            GameTimerFlags.StopOnMapEnd);
        return ECommandAction.Handled;
    }

    private void CompleteBotControllerMotionRecording(
        BotControllerRuntime runtime,
        int sourceSlot,
        int destinationSlot,
        BotLockTarget sourceLock,
        BotLockTarget destinationLock,
        double durationSeconds,
        long baselineReplayed,
        long baselineErrors,
        int lifecycleGeneration,
        int testGeneration)
    {
        if (!IsActive
            || lifecycleGeneration != _lifecycleGeneration
            || testGeneration != _controllerTestGeneration
            || !ReferenceEquals(runtime, _botController))
        {
            return;
        }

        _ = runtime.StopRecord(sourceSlot);
        RestoreBotControllerWeaponLock(runtime, sourceSlot, sourceLock);
        var recordedTicks = runtime.RecordedTickCount(sourceSlot);
        if (recordedTicks <= 0
            || !runtime.TransferRecordingToReplay(sourceSlot, destinationSlot))
        {
            RestoreBotControllerWeaponLock(runtime, destinationSlot, destinationLock);
            _logger.LogWarning(
                "BotController motion self-test FAIL: recording/transfer failed, recorded ticks {RecordedTicks}.",
                recordedTicks);
            return;
        }

        _ = runtime.Unlock(destinationSlot, BotLockKind.Weapon);
        _ = runtime.Lock(destinationSlot, BotLockTarget.Slot3);
        if (!runtime.StartReplay(destinationSlot))
        {
            RestoreBotControllerWeaponLock(runtime, destinationSlot, destinationLock);
            _logger.LogWarning(
                "BotController motion self-test FAIL: replay could not start after recording {RecordedTicks} ticks.",
                recordedTicks);
            return;
        }

        _logger.LogInformation(
            "BotController motion self-test replay started: {RecordedTicks} ticks from slot {SourceSlot} on slot {DestinationSlot}.",
            recordedTicks,
            sourceSlot,
            destinationSlot);
        _modSharp.PushTimer(
            () => FinishBotControllerMotionTest(
                runtime,
                destinationSlot,
                destinationLock,
                recordedTicks,
                baselineReplayed,
                baselineErrors,
                lifecycleGeneration,
                testGeneration),
            durationSeconds + 1,
            GameTimerFlags.StopOnMapEnd);
    }

    private void FinishBotControllerMotionTest(
        BotControllerRuntime runtime,
        int destinationSlot,
        BotLockTarget destinationLock,
        int recordedTicks,
        long baselineReplayed,
        long baselineErrors,
        int lifecycleGeneration,
        int testGeneration)
    {
        if (!IsActive
            || lifecycleGeneration != _lifecycleGeneration
            || testGeneration != _controllerTestGeneration
            || !ReferenceEquals(runtime, _botController))
        {
            return;
        }

        var replayedTicks = runtime.ReplayedTickCount - baselineReplayed;
        var errors = runtime.ErrorCount - baselineErrors;
        var stillReplaying = runtime.IsReplaying(destinationSlot);
        var replayTotal = runtime.ReplayTotal(destinationSlot);
        _ = runtime.StopReplay(destinationSlot);
        RestoreBotControllerWeaponLock(runtime, destinationSlot, destinationLock);
        if (!stillReplaying
            && replayTotal == recordedTicks
            && replayedTicks >= recordedTicks
            && errors == 0)
        {
            _logger.LogInformation(
                "BotController motion self-test PASS: recorded {RecordedTicks}, replayed {ReplayedTicks}, destination slot {DestinationSlot}, errors {Errors}.",
                recordedTicks,
                replayedTicks,
                destinationSlot,
                errors);
            return;
        }

        _logger.LogWarning(
            "BotController motion self-test FAIL: recorded {RecordedTicks}, replay total {ReplayTotal}, replayed {ReplayedTicks}, still replaying {StillReplaying}, errors {Errors}.",
            recordedTicks,
            replayTotal,
            replayedTicks,
            stillReplaying,
            errors);
    }

    private static void RestoreBotControllerWeaponLock(
        BotControllerRuntime runtime,
        int slot,
        BotLockTarget target)
    {
        _ = runtime.Unlock(slot, BotLockKind.Weapon);
        if (target != BotLockTarget.None)
        {
            _ = runtime.Lock(slot, target);
        }
    }

    private bool TryParseBotControllerSlotCommand(
        StringCommand command,
        out BotControllerRuntime runtime,
        out int slot,
        out string kind)
    {
        runtime = null!;
        slot = -1;
        kind = string.Empty;
        if (!IsActive
            || _botController is not { IsActive: true } controller
            || command.ArgCount != 2
            || !int.TryParse(
                command.GetArg(1),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out slot)
            || slot is < 0 or >= 64)
        {
            return false;
        }

        runtime = controller;
        kind = command.GetArg(2).Trim().ToLowerInvariant();
        return true;
    }

    private ECommandAction OnServerBotHiderStatusCommand(
        StringCommand command)
    {
        var status = IsActive && _botIdentity is { } runtime
            ? runtime.GetStatus()
            : "BotHider is available only while BotMatch is active.";
        _logger.LogInformation(
            "{Status} Actual bot_quota={BotQuota}, bot_quota_mode={BotQuotaMode}.",
            status,
            ReadConVarValue("bot_quota"),
            ReadConVarValue("bot_quota_mode"));
        return ECommandAction.Handled;
    }

    private string ReadConVarValue(string name)
    {
        try
        {
            return (_conVars.FindConVar(name)
                    ?? _conVars.FindConVar(name, useIterator: true))
                ?.GetString()
                ?? "<missing>";
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                exception,
                "Failed to read BotMatch ConVar {ConVar} for status.",
                name);
            return "<error>";
        }
    }

    private ECommandAction OnServerBotHiderSetSidCommand(
        StringCommand command)
    {
        if (!TryGetActiveBotHider(out var runtime))
        {
            return ECommandAction.Handled;
        }
        if (command.ArgCount < 2
            || !int.TryParse(
                command.GetArg(1),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var slot)
            || !ulong.TryParse(
                command.GetArg(2),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var steamId))
        {
            _logger.LogInformation("Usage: bh_setsid <slot> <sid64>.");
            return ECommandAction.Handled;
        }

        var applied = runtime.SetBotSteamId(slot, steamId);
        _logger.LogInformation(
            "BotHider SetBotSteamId({Slot}, {SteamId}) -> {Applied}.",
            slot,
            steamId,
            applied);
        return ECommandAction.Handled;
    }

    private ECommandAction OnServerBotHiderSetNameCommand(
        StringCommand command)
    {
        if (!TryGetActiveBotHider(out var runtime))
        {
            return ECommandAction.Handled;
        }
        if (command.ArgCount < 2
            || !int.TryParse(
                command.GetArg(1),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var slot))
        {
            _logger.LogInformation("Usage: bh_setname <slot> <name>.");
            return ECommandAction.Handled;
        }

        var requestedName = command.GetArg(2);
        var applied = runtime.SetPersonaName(slot, requestedName);
        _logger.LogInformation(
            "BotHider SetPersonaName({Slot}, '{Name}') -> {Applied}.",
            slot,
            applied ? runtime.GetPersonaName(slot) : requestedName,
            applied);
        return ECommandAction.Handled;
    }

    private ECommandAction OnServerBotHiderSetFlairCommand(
        StringCommand command)
    {
        if (!TryGetActiveBotHider(out var runtime))
        {
            return ECommandAction.Handled;
        }
        if (command.ArgCount < 2
            || !int.TryParse(
                command.GetArg(1),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var slot)
            || !uint.TryParse(
                command.GetArg(2),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var flair))
        {
            _logger.LogInformation(
                "Usage: bh_setflair <slot> <item_def_index>.");
            return ECommandAction.Handled;
        }

        var applied = runtime.SetScoreboardFlair(slot, flair);
        _logger.LogInformation(
            "BotHider SetScoreboardFlair({Slot}, {Flair}) -> {Applied}.",
            slot,
            flair,
            applied);
        return ECommandAction.Handled;
    }

    private ECommandAction OnServerBotHiderSetCrosshairCommand(
        StringCommand command)
    {
        if (!TryGetActiveBotHider(out var runtime))
        {
            return ECommandAction.Handled;
        }
        if (command.ArgCount < 2
            || !int.TryParse(
                command.GetArg(1),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var slot))
        {
            _logger.LogInformation(
                "Usage: bh_setcrosshair <slot> <code|0>.");
            return ECommandAction.Handled;
        }

        var crosshair = command.GetArg(2);
        var applied = runtime.SetCrosshairCode(slot, crosshair);
        _logger.LogInformation(
            "BotHider SetCrosshairCode({Slot}, '{Code}') -> {Applied}.",
            slot,
            crosshair,
            applied);
        return ECommandAction.Handled;
    }

    private ECommandAction OnServerBotHiderSetAvatarCommand(
        StringCommand command)
    {
        if (!TryGetActiveBotHider(out var runtime))
        {
            return ECommandAction.Handled;
        }
        if (command.ArgCount < 2
            || !int.TryParse(
                command.GetArg(1),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var slot))
        {
            _logger.LogInformation(
                "Usage: bh_setavatar <slot> <png_path|0>.");
            return ECommandAction.Handled;
        }

        var path = command.GetArg(2);
        var applied = runtime.TrySetBotAvatar(slot, path, out var error);
        if (applied)
        {
            if (path == "0")
            {
                _logger.LogInformation(
                    "BotHider custom avatar cleared for slot {Slot}.",
                    slot);
            }
            else
            {
                _logger.LogInformation(
                    "BotHider custom avatar applied for slot {Slot}: {Bytes} bytes.",
                    slot,
                    runtime.GetConfiguredAvatarSize(slot));
            }
        }
        else
        {
            _logger.LogWarning(
                "BotHider custom avatar rejected for slot {Slot}: {Error}.",
                slot,
                error);
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnServerBotHiderDisguiseCommand(
        StringCommand command)
    {
        if (!TryGetActiveBotHider(out var runtime))
        {
            return ECommandAction.Handled;
        }
        if (command.ArgCount < 1
            || !TryParseToggle(command.GetArg(1), out var enabled))
        {
            _logger.LogInformation("Usage: bh_disguise <0|1>.");
            return ECommandAction.Handled;
        }

        var applied = runtime.SetDisguise(enabled);
        _logger.LogInformation(
            "BotHider disguise -> {State} ({Applied}); in-place userinfo update was requested without rebuilding Bot clients (see bh_status for API compatibility).",
            enabled ? "ON" : "OFF",
            applied);
        return ECommandAction.Handled;
    }

    private ECommandAction OnServerBotHiderNameSourceCommand(
        StringCommand command)
    {
        if (!TryGetActiveBotHider(out var runtime))
        {
            return ECommandAction.Handled;
        }
        if (command.ArgCount < 1
            || !TryParseToggle(command.GetArg(1), out var useBotInfo))
        {
            _logger.LogInformation(
                "Usage: bh_namesource <0|1> (0=botprofile, 1=bot_info).");
            return ECommandAction.Handled;
        }

        var applied = runtime.SetNameSource(useBotInfo);
        _logger.LogInformation(
            "BotHider name source -> {Source} ({Applied}); applies to newly adopted bots.",
            useBotInfo ? "bot_info" : "botprofile",
            applied);
        return ECommandAction.Handled;
    }

    private bool TryGetActiveBotHider(out BotIdentityRuntime runtime)
    {
        if (IsActive && _botIdentity is { IsActive: true } activeRuntime)
        {
            runtime = activeRuntime;
            return true;
        }

        runtime = null!;
        _logger.LogInformation(
            "BotHider management commands are available only while BotMatch is active.");
        return false;
    }

    private ECommandAction OnDamageStyleCommand(
        IGameClient client,
        StringCommand command)
    {
        var slot = client.Slot.AsPrimitive();
        if (!client.IsValid
            || client.IsHltv
            || BotIdentityRegistry.IsBot(client.IsFakeClient, slot))
        {
            return ECommandAction.Handled;
        }

        if (!IsActive
            || !_config.EnableDamageRecap
            || _damageRecap is not { } runtime)
        {
            client.Print(
                HudPrintChannel.Chat,
                $"{_config.Prefix} damage_style 只在人机对抗模式中启用。");
            return ECommandAction.Handled;
        }

        if (command.ArgCount > 0 && !runtime.TrySetStyle(command.GetArg(1)))
        {
            client.Print(
                HudPrintChannel.Chat,
                $"{_config.Prefix} 用法：!damage_style auto、classic 或 pw。");
            return ECommandAction.Handled;
        }

        client.Print(
            HudPrintChannel.Chat,
            $"{_config.Prefix} 回合伤害样式：{runtime.DescribeStyle(client)}。");
        return ECommandAction.Handled;
    }

    private ECommandAction OnServerDamageStyleCommand(StringCommand command)
    {
        if (!IsActive
            || !_config.EnableDamageRecap
            || _damageRecap is not { } runtime)
        {
            _logger.LogInformation(
                "damage_style is available only while BotMatch is active.");
            return ECommandAction.Handled;
        }

        if (command.ArgCount > 0 && !runtime.TrySetStyle(command.GetArg(1)))
        {
            _logger.LogInformation(
                "Invalid damage_style value. Use auto, classic or pw.");
            return ECommandAction.Handled;
        }

        _logger.LogInformation(
            "Current RoundDamageRecap style is {Style}.",
            runtime.DescribeStyle());
        return ECommandAction.Handled;
    }

    private ECommandAction OnServerDamageRecapStatusCommand(
        StringCommand command)
    {
        _logger.LogInformation(
            "{Status}",
            IsActive && _config.EnableDamageRecap && _damageRecap is { } runtime
                ? runtime.GetStatus()
                : "RoundDamageRecap is available only while BotMatch is active.");
        return ECommandAction.Handled;
    }

    private ECommandAction OnFlashDebugCommand(IGameClient client, StringCommand command)
    {
        var slot = client.Slot.AsPrimitive();
        if (!client.IsValid
            || client.IsHltv
            || BotIdentityRegistry.IsBot(client.IsFakeClient, slot))
        {
            return ECommandAction.Handled;
        }

        if (!IsActive || _botState is not { } runtime)
        {
            client.Print(
                HudPrintChannel.Chat,
                $"{_config.Prefix} botstate_flashdebug 只在人机对抗模式中启用。");
            return ECommandAction.Handled;
        }

        var enabled = !runtime.FlashDebugEnabled;
        if (command.ArgCount > 0
            && !TryParseToggle(command.GetArg(1), out enabled))
        {
            client.Print(
                HudPrintChannel.Chat,
                $"{_config.Prefix} 用法：!botstate_flashdebug [on/off]。");
            return ECommandAction.Handled;
        }

        runtime.SetFlashDebug(enabled);
        client.Print(
            HudPrintChannel.Chat,
            $"{_config.Prefix} 闪光规避调试：{(runtime.FlashDebugEnabled ? "开启" : "关闭")}。");
        return ECommandAction.Handled;
    }

    private ECommandAction OnServerFlashDebugCommand(StringCommand command)
    {
        if (!IsActive || _botState is not { } runtime)
        {
            _logger.LogInformation(
                "botstate_flashdebug is available only while BotMatch is active.");
            return ECommandAction.Handled;
        }

        var enabled = !runtime.FlashDebugEnabled;
        if (command.ArgCount > 0
            && !TryParseToggle(command.GetArg(1), out enabled))
        {
            _logger.LogInformation(
                "Invalid botstate_flashdebug value. Use on/off, 1/0 or true/false.");
            return ECommandAction.Handled;
        }

        runtime.SetFlashDebug(enabled);
        _logger.LogInformation(
            "BotState flash diagnostics are {State}.",
            runtime.FlashDebugEnabled ? "enabled" : "disabled");
        return ECommandAction.Handled;
    }

    private static bool TryParseToggle(string value, out bool enabled)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "on":
            case "1":
            case "true":
                enabled = true;
                return true;
            case "off":
            case "0":
            case "false":
                enabled = false;
                return true;
            default:
                enabled = false;
                return false;
        }
    }

    private void ScheduleBotQuotaReconciliation(double delaySeconds)
    {
        var lifecycleGeneration = _lifecycleGeneration;
        var quotaGeneration = ++_quotaGeneration;
        _modSharp.PushTimer(
            () =>
            {
                if (!IsActive
                    || lifecycleGeneration != _lifecycleGeneration
                    || quotaGeneration != _quotaGeneration)
                {
                    return;
                }

                var desired = new Dictionary<string, string>(_config.ConVars, StringComparer.Ordinal)
                {
                    ["bot_quota"] = "0",
                    ["bot_quota_mode"] = _config.BotQuotaMode,
                };
                _conVarLease.Reapply(desired);
                _botIdentity?.RestoreAllForQuotaRebuild();
                _modSharp.ServerCommand("bot_quota 0");
                _modSharp.ServerCommand("bot_kick");
                _modSharp.PushTimer(
                    () => ApplyConfiguredBotQuota(lifecycleGeneration, quotaGeneration),
                    0.25,
                    GameTimerFlags.StopOnMapEnd);
            },
            delaySeconds,
            GameTimerFlags.StopOnMapEnd);
    }

    private void ApplyConfiguredBotQuota(int lifecycleGeneration, int quotaGeneration)
    {
        if (!IsActive
            || lifecycleGeneration != _lifecycleGeneration
            || quotaGeneration != _quotaGeneration)
        {
            return;
        }

        var quota = _config.BotQuota.ToString(CultureInfo.InvariantCulture);
        _conVarLease.SetOwned("bot_quota", quota);
        _modSharp.ServerCommand($"bot_quota {quota}");
        ScheduleIdentityReconciliation(0.35);
        ScheduleBotTeamBalance();
        _logger.LogInformation(
            "Bot quota reconciled at {BotQuota} after server spawn; BotHider preserves native engine bot identity and bot_auto_vacate remains enabled.",
            _config.BotQuota);
    }

    private void ScheduleBotTeamBalance()
    {
        if (!_config.BalanceBotTeams)
        {
            return;
        }

        var lifecycleGeneration = _lifecycleGeneration;
        var teamBalanceGeneration = ++_teamBalanceGeneration;
        _modSharp.PushTimer(
            () =>
            {
                if (!IsActive
                    || lifecycleGeneration != _lifecycleGeneration
                    || teamBalanceGeneration != _teamBalanceGeneration)
                {
                    return;
                }

                BalanceBotTeams();
            },
            _config.TeamBalanceDelaySeconds,
            GameTimerFlags.StopOnMapEnd);
    }

    private void BalanceBotTeams()
    {
        var players = _clients.GetGameClients(inGame: true)
            .Where(client => client.IsValid && !client.IsHltv)
            .Select(client => (Client: client, Controller: client.GetPlayerController()))
            .Where(player => player.Controller?.Team is CStrikeTeam.CT or CStrikeTeam.TE)
            .ToArray();
        var ctCount = players.Count(player => player.Controller!.Team == CStrikeTeam.CT);
        var tCount = players.Count(player => player.Controller!.Team == CStrikeTeam.TE);
        var moved = 0;

        while (Math.Abs(ctCount - tCount) > 1)
        {
            var source = ctCount > tCount ? CStrikeTeam.CT : CStrikeTeam.TE;
            var target = source == CStrikeTeam.CT ? CStrikeTeam.TE : CStrikeTeam.CT;
            var bot = players.FirstOrDefault(
                player => BotIdentityRegistry.IsBot(
                        player.Client.IsFakeClient,
                        player.Client.Slot.AsPrimitive())
                    && player.Controller!.Team == source);
            if (bot.Controller is null)
            {
                _logger.LogWarning(
                    "Cannot balance BotMatch teams without moving a human: CT {CtCount}, T {TCount}.",
                    ctCount,
                    tCount);
                return;
            }

            if (_botIdentity is { } identity)
            {
                identity.RunWithEngineBotIdentity(
                    bot.Client,
                    () => bot.Controller.SwitchTeam(target));
            }
            else
            {
                bot.Controller.SwitchTeam(target);
            }

            moved++;
            if (source == CStrikeTeam.CT)
            {
                ctCount--;
                tCount++;
            }
            else
            {
                tCount--;
                ctCount++;
            }
        }

        if (moved > 0)
        {
            _logger.LogInformation(
                "Balanced BotMatch teams by moving {Moved} bot(s): CT {CtCount}, T {TCount}.",
                moved,
                ctCount,
                tCount);
        }
    }

    private void StartIdentityReconciliationLoop()
    {
        var generation = ++_identityGeneration;
        ScheduleIdentityReconciliationTick(generation, 0.25);
    }

    private void StartIdentityFastApplyWindow()
    {
        if (!IsActive)
        {
            return;
        }

        var generation = ++_identityFastGeneration;
        ScheduleIdentityFastApplyTick(generation, remainingTicks: 80, delaySeconds: 0.05);
    }

    private void ScheduleIdentityFastApplyTick(
        int generation,
        int remainingTicks,
        double delaySeconds)
    {
        var lifecycleGeneration = _lifecycleGeneration;
        _modSharp.PushTimer(
            () =>
            {
                if (!IsActive
                    || lifecycleGeneration != _lifecycleGeneration
                    || generation != _identityFastGeneration)
                {
                    return;
                }

                _botIdentity?.Reconcile();
                if (remainingTicks > 1)
                {
                    ScheduleIdentityFastApplyTick(
                        generation,
                        remainingTicks - 1,
                        0.25);
                }
            },
            delaySeconds,
            GameTimerFlags.StopOnMapEnd);
    }

    private void ScheduleIdentityReconciliation(double delaySeconds)
    {
        var lifecycleGeneration = _lifecycleGeneration;
        _modSharp.PushTimer(
            () =>
            {
                if (IsActive && lifecycleGeneration == _lifecycleGeneration)
                {
                    _botIdentity?.Reconcile();
                }
            },
            delaySeconds,
            GameTimerFlags.StopOnMapEnd);
    }

    private void ScheduleIdentityReconcile(IGameClient client, double delaySeconds)
    {
        var lifecycleGeneration = _lifecycleGeneration;
        _modSharp.PushTimer(
            () =>
            {
                if (IsActive && lifecycleGeneration == _lifecycleGeneration)
                {
                    _botIdentity?.TryAdoptOrRefresh(client);
                }
            },
            delaySeconds,
            GameTimerFlags.StopOnMapEnd);
    }

    private void ScheduleIdentityReconciliationTick(int generation, double delaySeconds)
    {
        var lifecycleGeneration = _lifecycleGeneration;
        _modSharp.PushTimer(
            () =>
            {
                if (!IsActive
                    || lifecycleGeneration != _lifecycleGeneration
                    || generation != _identityGeneration)
                {
                    return;
                }

                _botIdentity?.Reconcile();
                ScheduleIdentityReconciliationTick(generation, 1);
            },
            delaySeconds,
            GameTimerFlags.StopOnMapEnd);
    }

    private void HandleTeamElimination(IEventPlayerDeath death)
    {
        if (!_config.KnifeAfterTeamElimination
            || _teamEliminationHandled
            || _botController is not { } controllerRuntime
            || death.VictimController is not { } victim
            || victim.Team is not (CStrikeTeam.CT or CStrikeTeam.TE))
        {
            return;
        }

        var victimTeam = victim.Team;
        var victimSlot = victim.PlayerSlot.AsPrimitive();
        var players = _clients.GetGameClients(inGame: true)
            .Where(client => client is { IsValid: true, IsHltv: false })
            .Select(client => (Client: client, Controller: client.GetPlayerController()))
            .Where(player => player.Controller?.Team is CStrikeTeam.CT or CStrikeTeam.TE)
            .ToArray();
        if (players.Any(player =>
                player.Client.Slot.AsPrimitive() != victimSlot
                && player.Controller!.Team == victimTeam
                && player.Controller.GetPlayerPawn() is { IsAlive: true }))
        {
            return;
        }

        var winningTeam = victimTeam == CStrikeTeam.CT ? CStrikeTeam.TE : CStrikeTeam.CT;
        if (!players.Any(player =>
                player.Controller!.Team == winningTeam
                && player.Controller.GetPlayerPawn() is { IsAlive: true }))
        {
            return;
        }

        _teamEliminationHandled = true;
        var locked = 0;
        foreach (var player in players.Where(player =>
                     player.Controller!.Team == winningTeam
                     && player.Controller.GetPlayerPawn() is { IsAlive: true }
                     && BotIdentityRegistry.IsBot(
                         player.Client.IsFakeClient,
                         player.Client.Slot.AsPrimitive())))
        {
            var slot = player.Client.Slot.AsPrimitive();
            var switched = controllerRuntime.SwitchBotWeapon(slot, 42);
            var weaponLocked = controllerRuntime.LockWeapon(slot, GearSlot.Knife);
            if (switched)
            {
                _botState?.QueueInspect(slot);
            }

            if (switched && weaponLocked)
            {
                locked++;
            }
            else
            {
                _logger.LogWarning(
                    "BotController knife action incomplete for slot {Slot}: switch {Switched}, lock {Locked}.",
                    slot,
                    switched,
                    weaponLocked);
            }
        }

        if (locked > 0)
        {
            _logger.LogInformation(
                "BotController switched and locked {Count} surviving bot(s) to knife after team elimination.",
                locked);
        }
    }
}
