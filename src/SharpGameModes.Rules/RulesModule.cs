using Microsoft.Extensions.Configuration;
using SharpGameModes.Contracts;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Managers;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace SharpGameModes.Rules;

public sealed class RulesModule : IModSharpModule
{
    private readonly ISharedSystem _shared;
    private readonly IClientManager _clients;
    private readonly IHookManager _hooks;
    private IModSharpModuleInterface<IModeContext>? _modeContext;
    private IDisposable? _modeContextSubscription;
    private bool _knifeHookInstalled;
    private bool _stopping;

    public RulesModule(
        ISharedSystem sharedSystem,
        string dllPath,
        string sharpPath,
        Version version,
        IConfiguration coreConfiguration,
        bool hotReload)
    {
        _shared = sharedSystem;
        _clients = sharedSystem.GetClientManager();
        _hooks = sharedSystem.GetHookManager();
    }

    public string DisplayName => "SharpGameModes Rules";
    public string DisplayAuthor => "SharpGameModes Contributors";

    public bool Init() => true;

    public void OnAllModulesLoaded() => RefreshModeContext();

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
            SetKnifeHookEnabled(false);
        }
    }

    public void Shutdown()
    {
        _stopping = true;
        _modeContextSubscription?.Dispose();
        _modeContextSubscription = null;
        _modeContext = null;
        SetKnifeHookEnabled(false);
    }

    private HookReturnValue<long> OnPlayerDispatchTraceAttack(
        IPlayerDispatchTraceAttackHookParams parameters,
        HookReturnValue<long> current)
    {
        // The shared trace hook cannot be filtered by damage type at registration.
        // Keep this as the first operation so non-knife hits take the cheapest path.
        if ((parameters.DamageType & DamageFlagBits.Slash) == 0
            || parameters.AttackerPlayerSlot is < 0 or > 63)
        {
            return current;
        }

        var attacker = _clients
            .GetGameClient(new PlayerSlot((byte)parameters.AttackerPlayerSlot))
            ?.GetPlayerController();
        if (attacker is null
            || attacker.PlayerSlot == parameters.Controller.PlayerSlot
            || attacker.Team is not (CStrikeTeam.CT or CStrikeTeam.TE)
            || attacker.Team != parameters.Controller.Team)
        {
            return current;
        }

        return new HookReturnValue<long>(EHookAction.SkipCallReturnOverride);
    }

    private void RefreshModeContext()
    {
        var next = _shared.GetSharpModuleManager()
            .GetOptionalSharpModuleInterface<IModeContext>(IModeContext.Identity);
        if (ReferenceEquals(_modeContext?.Instance, next?.Instance))
        {
            SetKnifeHookEnabled(RequiresKnifeFriendlyFireBlock(next?.Instance?.Current));
            return;
        }

        _modeContextSubscription?.Dispose();
        _modeContextSubscription = null;
        _modeContext = next;
        if (next?.Instance is not { } context)
        {
            SetKnifeHookEnabled(false);
            return;
        }

        _modeContextSubscription = context.Subscribe(ApplyModeContext);
        SetKnifeHookEnabled(RequiresKnifeFriendlyFireBlock(context.Current));
    }

    private void ApplyModeContext(ModeContextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        SetKnifeHookEnabled(RequiresKnifeFriendlyFireBlock(snapshot));
    }

    private void SetKnifeHookEnabled(bool enabled)
    {
        enabled &= !_stopping;
        if (enabled == _knifeHookInstalled)
        {
            return;
        }

        if (enabled)
        {
            _hooks.PlayerDispatchTraceAttack.InstallHookPre(OnPlayerDispatchTraceAttack);
        }
        else
        {
            _hooks.PlayerDispatchTraceAttack.RemoveHookPre(OnPlayerDispatchTraceAttack);
        }

        _knifeHookInstalled = enabled;
    }

    private static bool RequiresKnifeFriendlyFireBlock(ModeContextSnapshot? snapshot)
        => snapshot?.Selection.Mode is { } mode
            && (mode == ModeId.Classic || mode == ModeId.BotMatch);
}
