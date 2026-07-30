using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SharpGameModes.Contracts;
using SharpGameModes.Domain;
using Sharp.Shared;

namespace SharpGameModes.Core;

public sealed class CoreModule : IModSharpModule
{
    private readonly ISharedSystem _shared;
    private readonly ILogger<CoreModule> _logger;
    private readonly ModeContextState _modeContext = new();

    public CoreModule(
        ISharedSystem sharedSystem,
        string dllPath,
        string sharpPath,
        Version version,
        IConfiguration coreConfiguration,
        bool hotReload)
    {
        _shared = sharedSystem;
        _logger = sharedSystem.GetLoggerFactory().CreateLogger<CoreModule>();
    }

    public string DisplayName => "SharpGameModes Core";
    public string DisplayAuthor => "SharpGameModes Contributors";

    public bool Init() => true;

    public void PostInit()
    {
        _shared.GetSharpModuleManager().RegisterSharpModuleInterface<IModeContext>(
            this,
            IModeContext.Identity,
            _modeContext);
        _logger.LogInformation("SharpGameModes mode context service registered.");
    }

    public void Shutdown()
    {
        _modeContext.Dispose();
    }
}
