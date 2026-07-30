using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;

namespace SharpGameModes.WorkshopMount;

public sealed class WorkshopMountModule : IModSharpModule, IGameListener
{
    private const int PathAddToHead = 0;
    private const int SearchPathPriorityVpk = 2;

    private readonly IModSharp _modSharp;
    private readonly IFileManager _files;
    private readonly ILogger<WorkshopMountModule> _logger;
    private readonly string _gameRoot;
    private WorkshopVpkPath? _vpk;
    private string? _mountedPath;
    private bool _listenerInstalled;

    public WorkshopMountModule(
        ISharedSystem sharedSystem,
        string dllPath,
        string sharpPath,
        Version version,
        IConfiguration coreConfiguration,
        bool hotReload)
    {
        _modSharp = sharedSystem.GetModSharp();
        _files = sharedSystem.GetFileManager();
        _logger = sharedSystem.GetLoggerFactory().CreateLogger<WorkshopMountModule>();
        _gameRoot = Path.GetFullPath(Path.Combine(sharpPath, ".."));
    }

    public string DisplayName => "SharpGameModes Workshop Mount";
    public string DisplayAuthor => "SharpGameModes Contributors";
    public int ListenerVersion => IGameListener.ApiVersion;
    public int ListenerPriority => 100;

    public bool Init()
    {
        var value = _modSharp.GetCommandLine("-dual_addon");
        if (!ulong.TryParse(value, out var addonId) || addonId == 0)
        {
            _logger.LogInformation("No valid -dual_addon was configured; Workshop VPK mounting is inactive.");
            return true;
        }

        _vpk = WorkshopVpkResolver.Resolve(_gameRoot, addonId);
        if (_vpk is null)
        {
            _logger.LogError(
                "Dual addon {AddonId} is not installed under the server runtime Workshop directory.",
                addonId);
            return false;
        }

        _modSharp.InstallGameListener(this);
        _listenerInstalled = true;

        // This covers a module hot reload. Source 2 rebuilds GAME paths while starting
        // a map, so OnServerInit mounts the VPK again for normal server startup.
        if (Mount("module initialization"))
        {
            return true;
        }

        _modSharp.RemoveGameListener(this);
        _listenerInstalled = false;
        _vpk = null;
        return false;
    }

    public void OnServerInit()
    {
        if (!Mount("server initialization"))
        {
            _logger.LogError("Workshop VPK remount failed during server initialization.");
        }
    }

    private bool Mount(string lifecycle)
    {
        if (_vpk is not { } vpk)
        {
            return true;
        }

        try
        {
            RemoveMountedPath();
            _files.AddSearchPath(
                vpk.SearchPath,
                "GAME",
                PathAddToHead,
                SearchPathPriorityVpk);
            _mountedPath = vpk.SearchPath;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to mount dual addon from {IndexPath} during {Lifecycle}.",
                vpk.IndexPath,
                lifecycle);
            return false;
        }

        _logger.LogInformation(
            "Mounted dual addon from {IndexPath} as {SearchPath} ({Format}) during {Lifecycle}.",
            vpk.IndexPath,
            vpk.SearchPath,
            vpk.IsChunked ? "multi-chunk VPK" : "legacy VPK",
            lifecycle);
        return true;
    }

    public void Shutdown()
    {
        if (_listenerInstalled)
        {
            _modSharp.RemoveGameListener(this);
            _listenerInstalled = false;
        }

        RemoveMountedPath();
        _vpk = null;
    }

    private void RemoveMountedPath()
    {
        if (_mountedPath is null)
        {
            return;
        }

        try
        {
            _files.RemoveSearchPath(_mountedPath, "GAME");
            _logger.LogInformation("Removed Workshop VPK search path {SearchPath}.", _mountedPath);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to remove Workshop VPK search path {SearchPath}.", _mountedPath);
        }
        finally
        {
            _mountedPath = null;
        }
    }
}
