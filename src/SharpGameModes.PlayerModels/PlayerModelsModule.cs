using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SharpGameModes.Contracts;
using SharpGameModes.Domain;
using Sharp.Modules.AdminManager.Shared;
using Sharp.Modules.ClientPreferences.Shared;
using Sharp.Modules.MenuManager.Shared;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace SharpGameModes.PlayerModels;

public sealed partial class PlayerModelsModule : IModSharpModule, IGameListener, IClientListener
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly string[] ModelCommands = ["model", "m"];
    private static readonly string[] MenuCommands = ["md", "models"];
    private static readonly string[] MeshCommands = ["mg", "mesh"];
    private static readonly string[] SkinCommands = ["skin", "mat", "materialgroup"];

    private readonly ISharedSystem _shared;
    private readonly IModSharp _modSharp;
    private readonly IClientManager _clients;
    private readonly IFileManager _files;
    private readonly IHookManager _hooks;
    private readonly ILogger<PlayerModelsModule> _logger;
    private readonly string _configPath;
    private readonly string _defaultsPath;
    private readonly List<string> _registeredCommands = [];
    private readonly Dictionary<(ulong SteamId, CStrikeTeam Team), string> _originalModels = [];
    private readonly Dictionary<(ulong SteamId, CStrikeTeam Team), string> _appliedModels = [];
    private readonly Dictionary<ulong, DateTimeOffset> _cooldowns = [];
    private PlayerModelCatalogConfig _config = new();
    private PlayerModelDefaultsConfig _defaults = new();
    private IModSharpModuleInterface<IClientPreference>? _preferences;
    private IModSharpModuleInterface<IMenuManager>? _menus;
    private IModSharpModuleInterface<IAdminManager>? _admins;
    private IModSharpModuleInterface<IModeContext>? _modeContext;
    private IDisposable? _preferenceLoadSubscription;
    private bool _hooksInstalled;

    public PlayerModelsModule(
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
        _files = sharedSystem.GetFileManager();
        _hooks = sharedSystem.GetHookManager();
        _logger = sharedSystem.GetLoggerFactory().CreateLogger<PlayerModelsModule>();
        _configPath = Path.Combine(sharpPath, "configs", "sharp-gamemodes", "player-models.jsonc");
        _defaultsPath = Path.Combine(sharpPath, "configs", "sharp-gamemodes", "player-model-defaults.jsonc");
    }

    public string DisplayName => "SharpGameModes Player Models";
    public string DisplayAuthor => "SharpGameModes Contributors";
    public int ListenerVersion => IGameListener.ApiVersion;
    public int ListenerPriority => 15;

    public bool Init()
    {
        try
        {
            _config = JsonSerializer.Deserialize<PlayerModelCatalogConfig>(
                File.ReadAllText(_configPath),
                SerializerOptions) ?? throw new InvalidDataException("Player model config is empty.");
            _config.Validate();

            if (File.Exists(_defaultsPath))
            {
                _defaults = JsonSerializer.Deserialize<PlayerModelDefaultsConfig>(
                    File.ReadAllText(_defaultsPath),
                    SerializerOptions) ?? new PlayerModelDefaultsConfig();
            }

            ValidateDefaults();
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or InvalidDataException or ArgumentException)
        {
            _logger.LogError(exception, "Failed to load player model configuration.");
            return false;
        }

        if (!_config.Enabled)
        {
            _logger.LogInformation("SharpGameModes Player Models is disabled by configuration.");
            return true;
        }

        _modSharp.InstallGameListener(this);
        _clients.InstallClientListener(this);
        InstallHooks();
        InstallCommands();
        _logger.LogInformation("Loaded {Count} PMC-compatible player models.", _config.Models.Count);
        return true;
    }

    public void OnAllModulesLoaded()
        => RefreshInterfaces();

    public void OnLibraryConnected(string name)
        => RefreshInterfaces();

    public void OnLibraryDisconnect(string name)
        => RefreshInterfaces();

    public void OnGameInit()
    {
        _originalModels.Clear();
        _appliedModels.Clear();
    }

    public void OnGamePreShutdown()
    {
        _originalModels.Clear();
        _appliedModels.Clear();
    }

    public void OnResourcePrecache()
    {
        if (_config.DisablePrecache)
        {
            return;
        }

        var paths = _config.Models.Values
            .Select(model => model.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var missing = paths
            .Where(path =>
                !_files.FileExists(path, "GAME")
                && !_files.FileExists($"{path}_c", "GAME"))
            .ToArray();

        if (missing.Length == 0)
        {
            _logger.LogInformation(
                "Verified {Count} configured player model resources through the GAME search path.",
                paths.Length);
        }
        else
        {
            _logger.LogWarning(
                "{MissingCount} of {Count} configured player model resources are unavailable through GAME; first missing: {Paths}.",
                missing.Length,
                paths.Length,
                string.Join(", ", missing.Take(5)));
        }

        foreach (var path in paths)
        {
            _modSharp.PrecacheResource(path);
        }
    }

    public void OnClientDisconnecting(IGameClient client, NetworkDisconnectionReason reason)
    {
        var steamId = client.SteamId.AsPrimitive();
        _cooldowns.Remove(steamId);
        _originalModels.Remove((steamId, CStrikeTeam.TE));
        _originalModels.Remove((steamId, CStrikeTeam.CT));
        _appliedModels.Remove((steamId, CStrikeTeam.TE));
        _appliedModels.Remove((steamId, CStrikeTeam.CT));
    }

    public void Shutdown()
    {
        _preferenceLoadSubscription?.Dispose();
        _preferenceLoadSubscription = null;
        RemoveCommands();
        RemoveHooks();
        if (_config.Enabled)
        {
            _clients.RemoveClientListener(this);
            _modSharp.RemoveGameListener(this);
        }

        _preferences = null;
        _menus = null;
        _admins = null;
        _modeContext = null;
        _originalModels.Clear();
        _appliedModels.Clear();
        _cooldowns.Clear();
    }

    private void RefreshInterfaces()
    {
        var modules = _shared.GetSharpModuleManager();
        var preferences = modules.GetOptionalSharpModuleInterface<IClientPreference>(IClientPreference.Identity);
        if (!ReferenceEquals(preferences?.Instance, _preferences?.Instance))
        {
            _preferenceLoadSubscription?.Dispose();
            _preferenceLoadSubscription = null;
            _preferences = preferences;
            if (_preferences?.Instance is { } instance)
            {
                _preferenceLoadSubscription = instance.ListenOnLoad(OnPreferencesLoaded);
            }
        }

        _menus = modules.GetOptionalSharpModuleInterface<IMenuManager>(IMenuManager.Identity);
        _admins = modules.GetOptionalSharpModuleInterface<IAdminManager>(IAdminManager.Identity);
        _modeContext = modules.GetOptionalSharpModuleInterface<IModeContext>(IModeContext.Identity);
    }

    private void OnPreferencesLoaded(IGameClient client)
    {
        if (!IsHuman(client))
        {
            return;
        }

        ValidateSelections(client);
        ApplyCurrentModel(client);
    }

    private void ValidateDefaults()
    {
        foreach (var rule in _defaults.DefaultModels.All.Values
                     .Concat(_defaults.DefaultModels.T.Values)
                     .Concat(_defaults.DefaultModels.CT.Values))
        {
            foreach (var index in rule.Index)
            {
                if (string.IsNullOrEmpty(index) || index == "@random")
                {
                    continue;
                }

                if (!_config.Models.ContainsKey(index))
                {
                    throw new InvalidDataException($"Default player model '{index}' does not exist.");
                }
            }
        }
    }

    private bool IsAvailableForTeam(CStrikeTeam team)
        => IsPlayingTeam(team)
            && PlayerModelModePolicy.CanApplyPlayerModel(
                _modeContext?.Instance?.Current?.Selection.Mode,
                ToModelSide(team));

    private bool IsAvailableForClient(IGameClient client)
        => client.GetPlayerController() is { } controller && IsAvailableForTeam(controller.Team);

    private static bool IsPlayingTeam(CStrikeTeam team)
        => team is CStrikeTeam.TE or CStrikeTeam.CT;

    private static PlayerModelSide ToModelSide(CStrikeTeam team)
        => team == CStrikeTeam.TE ? PlayerModelSide.T : PlayerModelSide.CT;

    private static bool IsHuman([NotNullWhen(true)] IGameClient? client)
        => client is { IsValid: true, IsInGame: true, IsHltv: false }
            && !BotIdentityRegistry.IsBot(client.IsFakeClient, client.Slot.AsPrimitive())
            && client.SteamId.AsPrimitive() != 0;

    private bool PreferencesReady(IGameClient client, [NotNullWhen(true)] out IClientPreference? preferences)
    {
        preferences = _preferences?.Instance;
        if (preferences is not null && preferences.IsLoaded(client.SteamId))
        {
            return true;
        }

        client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 玩家偏好仍在加载，请稍后再试。");
        return false;
    }

    private bool TryGetMenu(IGameClient client, [NotNullWhen(true)] out IMenuManager? menus)
    {
        menus = _menus?.Instance;
        if (menus is not null)
        {
            return true;
        }

        client.Print(HudPrintChannel.Chat, $"{_config.Prefix} MenuManager 尚未加载。");
        return false;
    }

    private void PrintControls(IGameClient client)
        => client.Print(HudPrintChannel.Chat, $"{_config.Prefix} W/S 移动，E 选择，R 返回，Tab 退出。");

}
