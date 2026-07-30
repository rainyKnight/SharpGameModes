using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SharpGameModes.Contracts;
using SharpGameModes.Cosmetics.Storage;
using SharpGameModes.Domain;
using Sharp.Modules.MenuManager.Shared;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace SharpGameModes.Cosmetics;

public sealed partial class CosmeticsModule : IModSharpModule, IGameListener, IClientListener
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
    private readonly IHookManager _hooks;
    private readonly ILogger<CosmeticsModule> _logger;
    private readonly string _sharpPath;
    private readonly string _configPath;
    private readonly Dictionary<WeaponSkinKey, WeaponSkinPreference> _skinPreferences = [];
    private readonly Dictionary<KnifeKey, KnifePreference> _knifePreferences = [];
    private readonly List<string> _registeredCommands = [];
    private CosmeticsConfig _config = new();
    private WeaponSkinCatalog _skinCatalog = null!;
    private CosmeticsRepository _repository = null!;
    private IModSharpModuleInterface<IMenuManager>? _menuManager;
    private int _fadeSeed;
    private int _lifecycleGeneration;
    private bool _hooksInstalled;
    private bool _stopping;

    public CosmeticsModule(
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
        _hooks = sharedSystem.GetHookManager();
        _logger = sharedSystem.GetLoggerFactory().CreateLogger<CosmeticsModule>();
        _sharpPath = sharpPath;
        _configPath = Path.Combine(sharpPath, "configs", "sharp-gamemodes", "cosmetics.jsonc");
    }

    public string DisplayName => "SharpGameModes Cosmetics";
    public string DisplayAuthor => "SharpGameModes Contributors";
    public int ListenerVersion => IGameListener.ApiVersion;
    public int ListenerPriority => 10;

    public bool Init()
    {
        try
        {
            LoadConfiguration();
            _repository = new CosmeticsRepository(ResolvePath(_config.DatabasePath));
            _repository.EnsureCreated();
            LoadPreferences();
        }
        catch (Exception exception) when (
            exception is IOException
                or JsonException
                or InvalidDataException
                or InvalidOperationException
                or ArgumentException
                or OverflowException)
        {
            _logger.LogError(exception, "Failed to initialize SharpGameModes Cosmetics.");
            return false;
        }

        if (!_config.Enabled)
        {
            _logger.LogInformation("SharpGameModes Cosmetics is disabled by configuration.");
            return true;
        }

        _modSharp.InstallGameListener(this);
        _clients.InstallClientListener(this);
        InstallHooks();
        InstallCommands();
        _logger.LogInformation(
            "SharpGameModes Cosmetics loaded {SkinCount} weapon paints and {KnifeCount} knife preferences.",
            _skinPreferences.Count,
            _knifePreferences.Count);

        return true;
    }

    public void OnAllModulesLoaded()
    {
        RefreshInterfaces();
    }

    public void OnLibraryConnected(string name)
    {
        RefreshInterfaces();
    }

    public void OnLibraryDisconnect(string name)
    {
        RefreshInterfaces();
    }

    public void OnGameInit()
    {
        _lifecycleGeneration++;
        _fadeSeed = 0;
    }

    public void Shutdown()
    {
        _stopping = true;
        _lifecycleGeneration++;
        RemoveCommands();
        RemoveHooks();
        if (_config.Enabled)
        {
            _clients.RemoveClientListener(this);
            _modSharp.RemoveGameListener(this);
        }

        _menuManager = null;
        _skinPreferences.Clear();
        _knifePreferences.Clear();
    }

    private void LoadConfiguration()
    {
        _config = JsonSerializer.Deserialize<CosmeticsConfig>(
            File.ReadAllText(_configPath),
            SerializerOptions) ?? throw new InvalidDataException("Cosmetics config is empty.");
        _config.Validate();

        _skinCatalog = WeaponSkinCatalog.Parse(File.ReadAllText(ResolvePath(_config.WeaponSkinCatalogPath)));
    }

    private void LoadPreferences()
    {
        var snapshot = _repository.LoadAll();
        _skinPreferences.Clear();
        _knifePreferences.Clear();

        foreach (var preference in snapshot.WeaponSkins)
        {
            _skinPreferences[preference.Key] = preference.Value;
        }

        foreach (var preference in snapshot.Knives)
        {
            _knifePreferences[preference.Key] = preference.Value;
        }
    }

    private void RefreshInterfaces()
    {
        var modules = _shared.GetSharpModuleManager();
        _menuManager = modules.GetOptionalSharpModuleInterface<IMenuManager>(IMenuManager.Identity);
    }

    private string ResolvePath(string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(_sharpPath, path);

    private static bool IsPlayingTeam(CStrikeTeam team)
        => team is CStrikeTeam.TE or CStrikeTeam.CT;

    private static bool IsHuman(IGameClient? client)
        => client is { IsValid: true, IsInGame: true, IsHltv: false }
            && !BotIdentityRegistry.IsBot(client.IsFakeClient, client.Slot.AsPrimitive())
            && client.SteamId.AsPrimitive() != 0;

    private void Schedule(Action action, double delay)
    {
        var generation = _lifecycleGeneration;
        _modSharp.PushTimer(
            () =>
            {
                if (!_stopping && generation == _lifecycleGeneration)
                {
                    action();
                }
            },
            delay,
            GameTimerFlags.StopOnMapEnd);
    }

    private void InstallCommands()
    {
        foreach (var command in _config.WeaponSkinCommands
                     .Concat(_config.KnifeCommands)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _clients.InstallCommandCallback(command, OnCosmeticsCommand);
            _registeredCommands.Add(command);
        }
    }

    private void RemoveCommands()
    {
        foreach (var command in _registeredCommands)
        {
            _clients.RemoveCommandCallback(command, OnCosmeticsCommand);
        }

        _registeredCommands.Clear();
    }

    private ECommandAction OnCosmeticsCommand(IGameClient client, StringCommand command)
    {
        if (!IsHuman(client))
        {
            return ECommandAction.Skipped;
        }

        var alias = NormalizeCommand(command.CommandName);
        if (_config.KnifeCommands.Contains(alias, StringComparer.OrdinalIgnoreCase))
        {
            ShowKnifeMenu(client);
        }
        else
        {
            ShowWeaponSkinMenu(client);
        }

        return ECommandAction.Handled;
    }

    private static string NormalizeCommand(string command)
    {
        if (command.StartsWith("ms_", StringComparison.OrdinalIgnoreCase))
        {
            return command[3..];
        }

        return command.StartsWith("css_", StringComparison.OrdinalIgnoreCase)
            ? command[4..]
            : command;
    }

    private bool TryGetMenu(IGameClient client, out IMenuManager menuManager)
    {
        if (_menuManager?.Instance is { } instance)
        {
            menuManager = instance;
            return true;
        }

        client.Print(HudPrintChannel.Chat, $"{_config.Prefix} MenuManager 尚未加载。");
        menuManager = null!;
        return false;
    }

    private void PrintMenuControls(IGameClient client)
        => client.Print(HudPrintChannel.Chat, $"{_config.Prefix} W/S 移动，E 选择，R 返回，Tab 退出。");
}
