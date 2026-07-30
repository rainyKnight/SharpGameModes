using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SharpGameModes.Contracts;
using Sharp.Shared;
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Hooks;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace SharpGameModes.BotMatch;

/// <summary>
/// Pure ModSharp port of CS2-Bot-Improver's BotAimImprover. The stock
/// PickNewAimSpot runs first; this runtime then replaces only m_targetSpot
/// with the highest-priority point that has world line of sight.
/// </summary>
internal sealed class BotAimRuntime : IDisposable
{
    private const string PickNewAimSpotLinux =
        "55 48 89 E5 41 55 41 54 53 48 89 FB 48 83 EC 58 8B 8F E0 59 00 00 83 F9 FF";
    private const string PickNewAimSpotWindows =
        "48 8B C4 55 57 48 8D 68 ? 48 81 EC ? ? ? ? 48 8B F9 0F 29 70 ? 8B 89 ? ? ? ? 83 F9 FF";
    private static BotAimRuntime? s_active;
    private static unsafe delegate* unmanaged<nint, void> s_pickNewAimSpotOriginal;

    private readonly object _gate = new();
    private readonly ISharedSystem _shared;
    private readonly IClientManager _clients;
    private readonly IEntityManager _entities;
    private readonly IPhysicsQueryManager _traces;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<nint, CachedBot> _botCache = new();
    private readonly AimOffsets _offsets;
    private IDetourHook? _hook;
    private bool _active;
    private int _mode;
    private long _hookCalls;
    private long _visibleCalls;
    private long _rayTraces;
    private long _overrides;
    private long _cacheMisses;
    private long _hookErrors;
    private int _firstOverrideLogged;

    public BotAimRuntime(ISharedSystem shared, IClientManager clients, ILogger logger)
    {
        _shared = shared;
        _clients = clients;
        _entities = shared.GetEntityManager();
        _traces = shared.GetPhysicsQueryManager();
        _logger = logger;
        _offsets = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new AimOffsets(0x599C, 0x5A08, 0x5A0C, 0x12C0, PickNewAimSpotWindows)
            : new AimOffsets(0x5974, 0x59E0, 0x59E4, 0x1590, PickNewAimSpotLinux);
    }

    public string CurrentMode
        => BotAimPolicy.FormatMode((BotAimMode)Volatile.Read(ref _mode));

    public bool Activate(string configuredMode)
    {
        if (!BotAimPolicy.TryParseMode(configuredMode, out var mode))
        {
            _logger.LogError("Cannot enable BotAimImprover with invalid aim mode '{Mode}'.", configuredMode);
            return false;
        }

        lock (_gate)
        {
            Volatile.Write(ref _mode, (int)mode);
            if (_active)
            {
                return true;
            }

            try
            {
                var target = _shared.GetLibraryModuleManager().Server.FindPatternExactly(_offsets.Signature);
                if (target == 0)
                {
                    _logger.LogError("BotAimImprover PickNewAimSpot signature could not be resolved.");
                    return false;
                }

                unsafe
                {
                    _hook = _shared.GetHookManager().CreateDetourHook();
                    _hook.Prepare(
                        target,
                        (nint)(delegate* unmanaged<nint, void>)&HookPickNewAimSpot);
                    if (!_hook.Install())
                    {
                        _logger.LogError(
                            "Failed to install BotAimImprover PickNewAimSpot detour at 0x{Address:X}.",
                            target);
                        RemoveHook();
                        return false;
                    }

                    s_pickNewAimSpotOriginal =
                        (delegate* unmanaged<nint, void>)_hook.Trampoline;
                }

                _botCache.Clear();
                _active = true;
                s_active = this;
                _logger.LogInformation(
                    "Pure ModSharp BotAimImprover enabled at 0x{Address:X} in {Mode} mode (target=0x{TargetOffset:X}, enemy=0x{EnemyOffset:X}, visible=0x{VisibleOffset:X}, pawnBot=0x{PawnBotOffset:X}).",
                    target,
                    BotAimPolicy.FormatMode(mode),
                    _offsets.TargetSpot,
                    _offsets.Enemy,
                    _offsets.IsVisible,
                    _offsets.PawnBot);
                return true;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to enable pure ModSharp BotAimImprover.");
                _active = false;
                if (ReferenceEquals(s_active, this))
                {
                    s_active = null;
                }

                RemoveHook();
                return false;
            }
        }
    }

    public void Deactivate()
    {
        lock (_gate)
        {
            if (!_active && _hook is null)
            {
                _botCache.Clear();
                return;
            }

            _active = false;
            if (ReferenceEquals(s_active, this))
            {
                s_active = null;
            }

            _botCache.Clear();
        }

        RemoveHook();
        _logger.LogInformation(
            "Pure ModSharp BotAimImprover disabled. Calls {Calls}, visible {Visible}, traces {Traces}, overrides {Overrides}, cache misses {CacheMisses}, hook errors {HookErrors}.",
            Interlocked.Read(ref _hookCalls),
            Interlocked.Read(ref _visibleCalls),
            Interlocked.Read(ref _rayTraces),
            Interlocked.Read(ref _overrides),
            Interlocked.Read(ref _cacheMisses),
            Interlocked.Read(ref _hookErrors));
    }

    public bool TrySetMode(string? value)
    {
        if (!BotAimPolicy.TryParseMode(value, out var mode))
        {
            return false;
        }

        Volatile.Write(ref _mode, (int)mode);
        _logger.LogInformation("BotAimImprover aim mode changed to {Mode}.", BotAimPolicy.FormatMode(mode));
        return true;
    }

    public void ClearCache() => _botCache.Clear();

    public void Release(IGameClient client)
    {
        var slot = client.Slot.AsPrimitive();
        var userId = client.UserId.AsPrimitive();
        nint key = 0;
        foreach (var (pointer, cached) in _botCache)
        {
            if (cached.Slot == slot || cached.UserId == userId)
            {
                key = pointer;
                break;
            }
        }

        if (key != 0)
        {
            _botCache.TryRemove(key, out _);
        }
    }

    public void Dispose() => Deactivate();

    private void RemoveHook()
    {
        var hook = Interlocked.Exchange(ref _hook, null);
        if (hook is not null)
        {
            try
            {
                hook.Uninstall();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to uninstall BotAimImprover detour cleanly.");
            }
            finally
            {
                hook.Dispose();
            }
        }

        unsafe
        {
            s_pickNewAimSpotOriginal = null;
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe void HookPickNewAimSpot(nint botPointer)
    {
        var original = s_pickNewAimSpotOriginal;
        if (original == null)
        {
            return;
        }

        original(botPointer);

        var runtime = s_active;
        if (runtime is null || !runtime._active || botPointer == 0)
        {
            return;
        }

        Interlocked.Increment(ref runtime._hookCalls);
        try
        {
            runtime.ApplyAimOverride(botPointer);
        }
        catch
        {
            Interlocked.Increment(ref runtime._hookErrors);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void ApplyAimOverride(nint botPointer)
    {
        if (*(byte*)(botPointer + _offsets.IsVisible) == 0)
        {
            return;
        }

        Interlocked.Increment(ref _visibleCalls);
        var enemyHandle = *(int*)(botPointer + _offsets.Enemy);
        if (enemyHandle == -1)
        {
            return;
        }

        var enemyIndexValue = enemyHandle & 0x7FFF;
        if (enemyIndexValue is <= 0 or >= 4096
            || _entities.FindEntityByIndex<IPlayerPawn>((EntityIndex)enemyIndexValue) is not
            {
                IsValidEntity: true,
                IsAlive: true,
            } enemyPawn
            || !TryResolveBotPawn(botPointer, out var botPawn))
        {
            return;
        }

        var botEye = botPawn.GetEyePosition();
        var weaponIndex = botPawn.GetActiveWeapon()?.ItemDefinitionIndex ?? 0;
        var enemyOrigin = enemyPawn.GetAbsOrigin();
        var enemyEyeHeight = enemyPawn.ViewOffset.Z;
        if (!float.IsFinite(enemyEyeHeight) || enemyEyeHeight <= 0f)
        {
            enemyEyeHeight = 64f;
        }

        var enemyYaw = enemyPawn.GetEyeAngles().Y;
        var priority = BotAimPolicy.SelectPriority(
            (BotAimMode)Volatile.Read(ref _mode),
            weaponIndex);
        foreach (var pointIndex in priority)
        {
            if (!BotAimPolicy.TryComputePoint(
                    pointIndex,
                    enemyOrigin.X,
                    enemyOrigin.Y,
                    enemyOrigin.Z,
                    enemyEyeHeight,
                    enemyYaw,
                    out var point))
            {
                continue;
            }

            Interlocked.Increment(ref _rayTraces);
            var target = new Vector(point.X, point.Y, point.Z);
            var trace = _traces.TraceLineNoPlayers(
                botEye,
                target,
                UsefulInteractionLayers.BrushOnly,
                CollisionGroupType.Default,
                TraceQueryFlag.Static);
            if (trace.StartInSolid || trace.Fraction < 0.999f)
            {
                continue;
            }

            var destination = (float*)(botPointer + _offsets.TargetSpot);
            destination[0] = point.X;
            destination[1] = point.Y;
            destination[2] = point.Z;
            Interlocked.Increment(ref _overrides);
            if (Interlocked.CompareExchange(ref _firstOverrideLogged, 1, 0) == 0)
            {
                _logger.LogInformation(
                    "BotAimImprover first end-to-end override: weapon {WeaponDefinition}, point {Point}.",
                    weaponIndex,
                    BotAimPolicy.GetPointName(pointIndex));
            }

            return;
        }
    }

    private unsafe bool TryResolveBotPawn(nint botPointer, out IPlayerPawn pawn)
    {
        pawn = null!;
        if (_botCache.TryGetValue(botPointer, out var cached)
            && TryResolveCachedBot(botPointer, cached, out pawn))
        {
            return true;
        }

        _botCache.TryRemove(botPointer, out _);
        Interlocked.Increment(ref _cacheMisses);
        foreach (var client in _clients.GetGameClients(inGame: true))
        {
            var slot = client.Slot.AsPrimitive();
            if (!client.IsValid
                || client.IsHltv
                || slot is < 0 or >= 64
                || !BotIdentityRegistry.IsBot(client.IsFakeClient, slot)
                || client.GetPlayerController()?.GetPlayerPawn() is not
                {
                    IsValidEntity: true,
                } candidate)
            {
                continue;
            }

            if (*(nint*)(candidate.GetAbsPtr() + _offsets.PawnBot) != botPointer)
            {
                continue;
            }

            _botCache[botPointer] = new CachedBot(slot, client.UserId.AsPrimitive());
            pawn = candidate;
            return true;
        }

        return false;
    }

    private unsafe bool TryResolveCachedBot(
        nint botPointer,
        CachedBot cached,
        out IPlayerPawn pawn)
    {
        pawn = null!;
        if (cached.Slot is < 0 or >= 64
            || _clients.GetGameClient(new PlayerSlot((byte)cached.Slot)) is not
            {
                IsValid: true,
                IsHltv: false,
            } client
            || client.UserId.AsPrimitive() != cached.UserId
            || !BotIdentityRegistry.IsBot(client.IsFakeClient, cached.Slot)
            || client.GetPlayerController()?.GetPlayerPawn() is not
            {
                IsValidEntity: true,
            } current
            || *(nint*)(current.GetAbsPtr() + _offsets.PawnBot) != botPointer)
        {
            return false;
        }

        pawn = current;
        return true;
    }

    private readonly record struct AimOffsets(
        int TargetSpot,
        int Enemy,
        int IsVisible,
        int PawnBot,
        string Signature);

    private readonly record struct CachedBot(int Slot, int UserId);
}
