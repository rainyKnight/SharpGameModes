using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SharpGameModes.Contracts;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameObjects;
using Sharp.Shared.HookParams;
using Sharp.Shared.Hooks;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Types.CppProtobuf;
using Sharp.Shared.Units;

namespace SharpGameModes.BotMatch;

/// <summary>
/// Pure ModSharp implementation of CS2-Bot-Controller v0.5.5 / ABI 16.
/// Native engine entry points are detoured through ModSharp itself; no
/// MetaMod or external native plugin is required.
/// </summary>
internal sealed class BotControllerRuntime : IBotController, IDisposable
{
    private const string UpdateWindows =
        "40 55 56 57 41 54 48 8D AC 24 ? ? ? ? 48 81 EC ? ? ? ? 48 8D 05 ? ? ? ? C6 81 ? ? ? ? 00";
    private const string UpdateLinux =
        "55 48 8D 05 ? ? ? ? 48 8D 35 ? ? ? ? 48 89 E5 41 57 41 56 41 55 4C 8D AD ? ? ? ? 41 54 4C 89 EA 53 48 89 FB 48 81 EC";
    private const string UpkeepWindows =
        "48 89 5C 24 ? 48 89 6C 24 ? 56 57 41 56 48 83 EC ? 4C 8B F1 33 ED 48 8B 49 ? 48 39 A9 ? ? ? ?";
    private const string UpkeepLinux =
        "55 48 8D 05 ? ? ? ? 48 8D 35 ? ? ? ? 48 89 E5 41 55 41 54 48 8D 55 ? 53";
    private const string JumpWindows =
        "48 89 5C 24 ? 57 48 83 EC ? 48 8B D9 0F B6 FA 48 8B 49 ? 48 8B 91";
    private const string JumpLinux =
        "55 48 89 E5 53 48 89 FB 48 83 EC ? 48 8B 47 ? 48 8B B0 ? ? ? ? 8B B8 ? ? ? ? E8 ? ? ? ? 31 D2";
    private const string BuyUpdateWindows =
        "48 89 54 24 ? 48 89 4C 24 ? 55 57 41 54 48 8D AC 24";
    private const string BuyUpdateLinux =
        "55 48 89 E5 41 57 41 56 41 55 41 54 49 89 FC 53 48 89 F3 48 81 EC ? ? ? ? 80 7F 08 00 74";
    private const int BuyInitialDelayOffset = 0x08;
    private const int BuyDoneOffset = 0x18;
    private const int BotProfilePointerOffset = 0x08;
    private const int BotProfileAggressionOffset = 0x08;
    private const int BotProfileSkillOffset = 0x0C;
    private const int BotProfileTeamworkOffset = 0x10;
    private const int BotProfileWeaponPreferencesOffset = 0x24;
    private const int BotProfileWeaponPreferenceCountOffset = 0x44;
    private const int BotProfileCostOffset = 0x48;
    private const int BotProfileDifficultyOffset = 0x50;
    private const int BotProfileReactionTimeOffset = 0x58;
    private const int BotProfileAttackDelayOffset = 0x5C;
    private const int BotProfileLookAccelerationOffset = 0x78;
    private const int BotProfileLookStiffnessOffset = 0x7C;
    private const int BotProfileLookDampingOffset = 0x80;
    private const uint ReplayFlagMask = 0x3;

    private static BotControllerRuntime? s_active;
    private static unsafe delegate* unmanaged<nint, void> s_updateOriginal;
    private static unsafe delegate* unmanaged<nint, void> s_upkeepOriginal;
    private static unsafe delegate* unmanaged<nint, byte, byte> s_jumpOriginal;
    private static unsafe delegate* unmanaged<nint, nint, void> s_buyUpdateOriginal;
    private readonly object _gate = new();
    private readonly ISharedSystem _shared;
    private readonly IModSharp _modSharp;
    private readonly IHookManager _hooks;
    private readonly ISchemaManager _schema;
    private readonly IClientManager _clients;
    private readonly IEntityManager _entities;
    private readonly ILogger _logger;
    private readonly BotControllerStateStore _state = new();
    private readonly ConcurrentDictionary<nint, int> _botSlots = new();
    private readonly BotMovementSnapshot?[] _lockSnapshots =
        new BotMovementSnapshot?[BotControllerStateStore.MaximumSlots];
    private readonly List<UsercmdInjection>[] _injections =
        Enumerable.Range(0, BotControllerStateStore.MaximumSlots)
            .Select(_ => new List<UsercmdInjection>())
            .ToArray();
    private readonly ulong[] _lastInjectionMasks =
        new ulong[BotControllerStateStore.MaximumSlots];
    private readonly int[] _hasInjections =
        new int[BotControllerStateStore.MaximumSlots];
    private readonly byte[] _lastBuyInitialDelay =
        new byte[BotControllerStateStore.MaximumSlots];
    private IDetourHook? _updateHook;
    private IDetourHook? _upkeepHook;
    private IDetourHook? _jumpHook;
    private IDetourHook? _buyUpdateHook;
    private MovementOffsets _movementOffsets;
    private int _pawnBotOffset;
    private int _actualMoveTypeOffset;
    private int _botAiTickedOffset;
    private long _nextInjectionId;
    private bool _frameworkHooksInstalled;
    private bool _nativeLocksInstalled;
    private bool _buyHookInstalled;
    private bool _active;
    private long _runCommandCalls;
    private long _recordedTicks;
    private long _replayedTicks;
    private long _buttonInjections;
    private long _nativeLockBypasses;
    private long _buyPlansApplied;
    private long _errors;

    public BotControllerRuntime(ISharedSystem shared, IClientManager clients, ILogger logger)
    {
        _shared = shared;
        _modSharp = shared.GetModSharp();
        _hooks = shared.GetHookManager();
        _schema = shared.GetSchemaManager();
        _clients = clients;
        _entities = shared.GetEntityManager();
        _logger = logger;
    }

    public int AbiVersion => IBotController.CurrentAbiVersion;

    public bool IsActive => _active;

    public bool Activate()
    {
        lock (_gate)
        {
            if (_active)
            {
                return true;
            }

            try
            {
                ResolveOffsets();
                _active = true;
                s_active = this;
                _hooks.PlayerRunCommand.InstallHookPre(OnPlayerRunCommandPre);
                _frameworkHooksInstalled = true;
                _hooks.PlayerRunCommand.InstallHookPost(OnPlayerRunCommandPost);
                _nativeLocksInstalled = InstallNativeLockHooks();
                _buyHookInstalled = InstallBuyHook();
                _logger.LogInformation(
                    "Pure ModSharp BotController ABI {Abi} enabled (native locks {NativeLocks}, buy hook {BuyHook}, recording/replay true, voice unsupported).",
                    AbiVersion,
                    _nativeLocksInstalled,
                    _buyHookInstalled);
                return true;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to enable pure ModSharp BotController.");
                _active = false;
                if (ReferenceEquals(s_active, this))
                {
                    s_active = null;
                }

                RemoveHooks();
                _state.Clear();
                return false;
            }
        }
    }

    public void Deactivate()
    {
        lock (_gate)
        {
            if (!_active && !_frameworkHooksInstalled)
            {
                _state.Clear();
                ClearTransientState();
                return;
            }

            _active = false;
            if (ReferenceEquals(s_active, this))
            {
                s_active = null;
            }
        }

        RemoveHooks();
        _state.Clear();
        _botSlots.Clear();
        ClearTransientState();
        _logger.LogInformation(
            "Pure ModSharp BotController disabled. RunCommand {RunCommands}, recorded {Recorded}, replayed {Replayed}, injections {Injections}, native bypasses {Bypasses}, buy plans {BuyPlans}, errors {Errors}.",
            Interlocked.Read(ref _runCommandCalls),
            Interlocked.Read(ref _recordedTicks),
            Interlocked.Read(ref _replayedTicks),
            Interlocked.Read(ref _buttonInjections),
            Interlocked.Read(ref _nativeLockBypasses),
            Interlocked.Read(ref _buyPlansApplied),
            Interlocked.Read(ref _errors));
    }

    public bool Lock(int slot, BotLockKind kind)
    {
        if (!_active || !_state.Lock(slot, kind))
        {
            return false;
        }

        _lockSnapshots[slot] = null;
        return true;
    }

    public bool Lock(int slot, BotLockTarget target)
    {
        if (!_active || !_state.LockWeapon(slot, target))
        {
            return false;
        }

        _ = TrySwitchToLockTarget(slot, target);
        return true;
    }

    public bool Unlock(int slot, BotLockKind kind)
    {
        if (!_state.Unlock(slot, kind))
        {
            return false;
        }

        if (slot is >= 0 and < BotControllerStateStore.MaximumSlots
            && kind is BotLockKind.All or BotLockKind.Aim)
        {
            _lockSnapshots[slot] = null;
        }

        return true;
    }

    public bool UnlockAll(BotLockKind kind)
    {
        if (!_state.UnlockAll(kind))
        {
            return false;
        }

        if (kind is BotLockKind.All or BotLockKind.Aim)
        {
            Array.Clear(_lockSnapshots);
        }

        return true;
    }

    public bool IsLocked(int slot, BotLockKind kind)
        => _state.IsLocked(slot, kind);

    public BotLockTarget GetWeaponLock(int slot)
        => _state.GetWeaponLock(slot);

    public bool StartRecord(int slot)
        => _active && _state.StartRecord(slot);

    public bool StopRecord(int slot)
        => _state.StopRecord(slot);

    public bool IsRecording(int slot)
        => _state.IsRecording(slot);

    public int RecordedTickCount(int slot)
        => _state.RecordedTickCount(slot);

    public (BotReplayTick[] Ticks, BotSubtickMove[] Subticks) GetRecordedMotion(int slot)
        => _state.GetRecordedMotion(slot);

    public bool LoadReplay(int slot, BotReplayTick[] ticks, BotSubtickMove[] subticks)
        => _active && _state.LoadReplay(slot, ticks, subticks, [], []);

    public bool LoadReplayExtended(
        int slot,
        BotReplayTick[] ticks,
        BotSubtickMove[] subticks,
        BotReplayCommandFrame[] commandFrames,
        BotReplayMovementExtra[] movementExtras)
        => _active
            && _state.LoadReplay(
                slot,
                ticks,
                subticks,
                commandFrames,
                movementExtras);

    public bool TransferRecordingToReplay(int sourceSlot, int destinationSlot)
        => _active && _state.TransferRecordingToReplay(sourceSlot, destinationSlot);

    public bool SetReplayPawn(int slot, nint pawn)
    {
        if (!_active
            || !TryGetManagedBotPawn(slot, out var current)
            || current.GetAbsPtr() != pawn)
        {
            return false;
        }

        return _state.SetReplayPawn(slot, pawn);
    }

    public bool StartReplay(int slot, bool loop = false)
    {
        if (!_active || !TryGetManagedBotPawn(slot, out var pawn))
        {
            return false;
        }

        if (!_state.SetReplayPawn(slot, pawn.GetAbsPtr())
            || !_state.StartReplay(slot, loop))
        {
            return false;
        }

        ClearUsercmdInjections(slot);
        return true;
    }

    public bool StopReplay(int slot)
        => _state.StopReplay(slot);

    public int ReplayCursor(int slot)
        => _state.ReplayCursor(slot);

    public int ReplayTotal(int slot)
        => _state.ReplayTotal(slot);

    public bool IsReplaying(int slot)
        => _state.IsReplaying(slot);

    public bool TryGetReplayTick(int slot, out BotReplayTick tick)
        => _state.TryGetReplayTick(slot, out tick);

    public bool SwitchBotWeapon(int slot, int definitionIndex)
    {
        if (!_active
            || definitionIndex < 0
            || _state.IsReplaying(slot)
            || !TryGetManagedBotPawn(slot, out var pawn))
        {
            return false;
        }

        if (pawn.GetWeaponService() is { } weapons)
        {
            foreach (var handle in weapons.GetMyWeapons())
            {
                if (handle.IsValid()
                    && _entities.FindEntityByHandle<IBaseWeapon>(handle) is
                    {
                        IsValidEntity: true,
                    } weapon
                    && weapon.ItemDefinitionIndex == definitionIndex)
                {
                    pawn.SwitchWeapon(weapon);
                    return true;
                }
            }
        }

        if (definitionIndex is 42 or 59 or IBotController.KnifeDefinition
            && pawn.GetWeaponBySlot(GearSlot.Knife) is { IsValidEntity: true } knife)
        {
            pawn.SwitchWeapon(knife);
            return true;
        }

        return false;
    }

    public int BotActiveWeaponDef(int slot)
    {
        if (!_active
            || !TryGetManagedBotPawn(slot, out var pawn)
            || pawn.GetActiveWeapon() is not { IsValidEntity: true } weapon)
        {
            return -1;
        }

        return weapon.Slot == GearSlot.Knife
            ? IBotController.KnifeDefinition
            : weapon.ItemDefinitionIndex;
    }

    public long InjectUsercmd(int slot, ulong buttonMask, int durationMs = 0)
    {
        if (!_active
            || slot is < 0 or >= BotControllerStateStore.MaximumSlots
            || buttonMask == 0
            || durationMs < 0
            || _state.IsReplaying(slot))
        {
            return -1;
        }

        var id = Interlocked.Increment(ref _nextInjectionId);
        lock (_injections[slot])
        {
            if (_state.IsReplaying(slot))
            {
                return -1;
            }

            _injections[slot].Add(
                new UsercmdInjection(
                    id,
                    buttonMask,
                    durationMs,
                    InjectionPhase.PendingPress,
                    0));
            Volatile.Write(ref _hasInjections[slot], 1);
        }

        return id;
    }

    public bool CancelUsercmdInjection(int slot, long injectionId)
    {
        if (slot is < 0 or >= BotControllerStateStore.MaximumSlots || injectionId <= 0)
        {
            return false;
        }

        lock (_injections[slot])
        {
            var index = _injections[slot].FindIndex(item => item.Id == injectionId);
            if (index < 0)
            {
                return false;
            }

            var injection = _injections[slot][index];
            if (injection.Phase == InjectionPhase.PendingPress)
            {
                _injections[slot].RemoveAt(index);
            }
            else
            {
                _injections[slot][index] = injection with
                {
                    Phase = InjectionPhase.PendingRelease,
                };
            }

            if (_injections[slot].Count == 0 && _lastInjectionMasks[slot] == 0)
            {
                Volatile.Write(ref _hasInjections[slot], 0);
            }

            return true;
        }
    }

    public bool InjectButton(int slot, UserCommandButtons button)
        => InjectUsercmd(slot, (ulong)button) > 0;

    public bool LockWeapon(int slot, GearSlot gearSlot)
        => TryMapGearSlot(gearSlot, out var target) && Lock(slot, target);

    public bool UnlockWeapon(int slot)
        => Unlock(slot, BotLockKind.Weapon);

    public bool IsWeaponLocked(int slot, GearSlot gearSlot)
        => TryMapGearSlot(gearSlot, out var target) && GetWeaponLock(slot) == target;

    public void UnlockAllWeapons()
        => _ = UnlockAll(BotLockKind.Weapon);

    public bool TryGetPreferredGun(int slot, bool requireLoadedAmmo, out ushort itemDefinitionIndex)
    {
        itemDefinitionIndex = 0;
        if (!_active
            || !TryGetManagedBotPawn(slot, out var pawn)
            || pawn.GetWeaponService() is not { } weapons)
        {
            return false;
        }

        ushort secondary = 0;
        foreach (var handle in weapons.GetMyWeapons())
        {
            if (!handle.IsValid()
                || _entities.FindEntityByHandle<IBaseWeapon>(handle) is not
                {
                    IsValidEntity: true,
                } weapon
                || requireLoadedAmmo && weapon.Clip <= 0)
            {
                continue;
            }

            if (weapon.Slot == GearSlot.Rifle)
            {
                itemDefinitionIndex = weapon.ItemDefinitionIndex;
                return itemDefinitionIndex > 0;
            }

            if (secondary == 0 && weapon.Slot == GearSlot.Pistol)
            {
                secondary = weapon.ItemDefinitionIndex;
            }
        }

        itemDefinitionIndex = secondary;
        return itemDefinitionIndex > 0;
    }

    public unsafe bool GetBotProfile(int slot, out BotProfileData profile)
    {
        profile = default;
        if (!_active
            || !TryGetManagedBotPawn(slot, out var pawn)
            || _pawnBotOffset <= 0)
        {
            return false;
        }

        var bot = *(nint*)(pawn.GetAbsPtr() + _pawnBotOffset);
        if (bot == 0)
        {
            return false;
        }

        var pointer = *(nint*)(bot + BotProfilePointerOffset);
        if (pointer == 0)
        {
            return false;
        }

        var count = Math.Clamp(
            *(int*)(pointer + BotProfileWeaponPreferenceCountOffset),
            0,
            16);
        var preferences = new ushort[16];
        for (var index = 0; index < count; index++)
        {
            preferences[index] = *(ushort*)(
                pointer + BotProfileWeaponPreferencesOffset + (index * sizeof(ushort)));
        }

        profile = new BotProfileData
        {
            Aggression = *(float*)(pointer + BotProfileAggressionOffset),
            Skill = *(float*)(pointer + BotProfileSkillOffset),
            Teamwork = *(float*)(pointer + BotProfileTeamworkOffset),
            ReactionTime = *(float*)(pointer + BotProfileReactionTimeOffset),
            AttackDelay = *(float*)(pointer + BotProfileAttackDelayOffset),
            LookAccelAtk = *(float*)(pointer + BotProfileLookAccelerationOffset),
            LookStiffAtk = *(float*)(pointer + BotProfileLookStiffnessOffset),
            LookDampAtk = *(float*)(pointer + BotProfileLookDampingOffset),
            Cost = *(int*)(pointer + BotProfileCostOffset),
            Difficulty = *(byte*)(pointer + BotProfileDifficultyOffset),
            WeaponPrefCount = count,
            WeaponPref = preferences,
        };
        return IsFiniteProfile(profile);
    }

    public bool SetBuyPlan(int slot, string aliases)
        => _active && _state.SetBuyPlan(slot, aliases, skip: false);

    public bool SetBuySkip(int slot)
        => _active && _state.SetBuyPlan(slot, string.Empty, skip: true);

    public bool ClearBuyPlan(int slot)
        => _state.ClearBuyPlan(slot);

    public bool ClearAllBuyPlans()
        => _state.ClearAllBuyPlans();

    public int BuyPlanItemCount(int slot)
        => _state.BuyPlanItemCount(slot);

    public bool CanSendVoice()
        => false;

    public int GetVoiceStatus()
        => -1;

    public int SendVoiceFrame(
        int recipientSlot,
        int senderClient,
        ulong senderXuid,
        byte[] audio,
        int audioBytes,
        int sampleRate,
        float voiceLevel,
        int sequenceBytes,
        int sectionNumber,
        int uncompressedSampleOffset,
        uint numPackets,
        uint[] packetOffsets,
        int packetOffsetCount,
        int tick,
        int audibleMask)
        => -1;

    public string GetStatus()
        => $"BotController ABI {AbiVersion}, active {_active}, native locks {_nativeLocksInstalled}, buy hook {_buyHookInstalled}, recording {CountSlots(IsRecording)}, replaying {CountSlots(IsReplaying)}, runcommand {Interlocked.Read(ref _runCommandCalls)}, native bypasses {Interlocked.Read(ref _nativeLockBypasses)}, recorded {Interlocked.Read(ref _recordedTicks)}, replayed {Interlocked.Read(ref _replayedTicks)}, injections {Interlocked.Read(ref _buttonInjections)}, voice unsupported, errors {Interlocked.Read(ref _errors)}.";

    internal long ReplayedTickCount
        => Interlocked.Read(ref _replayedTicks);

    internal long ErrorCount
        => Interlocked.Read(ref _errors);

    public void Release(IGameClient client)
    {
        var slot = client.Slot.AsPrimitive();
        if (slot is < 0 or >= BotControllerStateStore.MaximumSlots)
        {
            return;
        }

        _ = _state.Unlock(slot, BotLockKind.All);
        _ = _state.Unlock(slot, BotLockKind.Aim);
        _ = _state.Unlock(slot, BotLockKind.Jump);
        _ = _state.Unlock(slot, BotLockKind.Weapon);
        _ = _state.StopRecord(slot);
        _ = _state.StopReplay(slot);
        _ = _state.ClearBuyPlan(slot);
        _lockSnapshots[slot] = null;
        lock (_injections[slot])
        {
            _injections[slot].Clear();
        }

        Volatile.Write(ref _hasInjections[slot], 0);
        _lastInjectionMasks[slot] = 0;
        _lastBuyInitialDelay[slot] = 0;
        foreach (var binding in _botSlots.Where(binding => binding.Value == slot))
        {
            _botSlots.TryRemove(binding.Key, out _);
        }
    }

    public void Dispose() => Deactivate();

    private unsafe HookReturnValue<EmptyHookReturn> OnPlayerRunCommandPre(
        IPlayerRunCommandHookParams parameters,
        HookReturnValue<EmptyHookReturn> result)
    {
        if (!_active)
        {
            return result;
        }

        Interlocked.Increment(ref _runCommandCalls);
        try
        {
            var slot = parameters.Controller.PlayerSlot.AsPrimitive();
            if (slot is < 0 or >= BotControllerStateStore.MaximumSlots
                || parameters.Pawn is not { IsValidEntity: true } pawn)
            {
                return result;
            }

            var command = parameters.BaseUserCmd;
            if (_state.TryGetReplayFrame(
                    slot,
                    out var replayTick,
                    out var replaySubticks,
                    out var replayCommand,
                    out var replayMovement,
                    out var replayPawn))
            {
                if (replayPawn != pawn.GetAbsPtr())
                {
                    _ = _state.StopReplay(slot);
                    return result;
                }

                ApplyReplayPre(
                    slot,
                    parameters,
                    pawn,
                    command,
                    replayTick,
                    replaySubticks,
                    replayCommand,
                    replayMovement);
            }
            else if (BotIdentityRegistry.IsBot(
                         parameters.Controller.IsFakeClient,
                         slot))
            {
                ApplyCommandLocks(slot, parameters, pawn, command);
            }

            if (!_state.IsReplaying(slot))
            {
                ApplyUsercmdInjections(slot, parameters, command);
                ApplyWeaponLock(slot, pawn, command);
            }

            if (_state.IsRecording(slot))
            {
                _state.CapturePre(
                    slot,
                    CaptureSnapshot(pawn, parameters.Service),
                    CaptureSubticks(parameters),
                    CaptureCommand(parameters, command),
                    CaptureMovementExtra(pawn));
            }
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _errors);
            _logger.LogDebug(exception, "BotController PlayerRunCommand pre hook failed.");
        }

        return result;
    }

    private unsafe void OnPlayerRunCommandPost(
        IPlayerRunCommandHookParams parameters,
        HookReturnValue<EmptyHookReturn> result)
    {
        if (!_active)
        {
            return;
        }

        try
        {
            var slot = parameters.Controller.PlayerSlot.AsPrimitive();
            if (slot is < 0 or >= BotControllerStateStore.MaximumSlots
                || parameters.Pawn is not { IsValidEntity: true } pawn)
            {
                return;
            }

            if (_state.IsRecording(slot))
            {
                _state.CapturePost(
                    slot,
                    CaptureSnapshot(pawn, parameters.Service),
                    BotActiveWeaponDefForPawn(pawn));
                Interlocked.Increment(ref _recordedTicks);
            }

            if (_state.TryGetReplayFrame(
                    slot,
                    out var replayTick,
                    out _,
                    out _,
                    out _,
                    out var replayPawn)
                && replayPawn == pawn.GetAbsPtr())
            {
                ApplySnapshot(pawn, parameters.Service, replayTick.Post);
                _state.AdvanceReplay(slot);
                Interlocked.Increment(ref _replayedTicks);
            }
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _errors);
            _logger.LogDebug(exception, "BotController PlayerRunCommand post hook failed.");
        }
    }

    private unsafe void ApplyCommandLocks(
        int slot,
        IPlayerRunCommandHookParams parameters,
        IBasePlayerPawn pawn,
        CBaseUserCmdPb* command)
    {
        if (_state.IsLocked(slot, BotLockKind.All))
        {
            var snapshot = _lockSnapshots[slot] ?? CaptureSnapshot(pawn, parameters.Service);
            _lockSnapshots[slot] = snapshot;
            ApplySnapshot(pawn, parameters.Service, snapshot);
            parameters.ChangedButtons |= parameters.KeyButtons;
            parameters.KeyButtons = 0;
            parameters.ScrollButtons = 0;
            parameters.Service.KeyButtons = 0;
            parameters.Service.KeyChangedButtons = 0;
            parameters.Service.ScrollButtons = 0;
            if (command != null)
            {
                command->ForwardMove = 0;
                command->SideMove = 0;
                command->UpMove = 0;
                command->WeaponSelect = 0;
                if (command->ButtonState != null)
                {
                    command->ButtonState->ButtonChanged |= command->ButtonState->ButtonPressed;
                    command->ButtonState->ButtonPressed = 0;
                    command->ButtonState->ButtonScroll = 0;
                }
            }

            return;
        }

        if (_state.IsLocked(slot, BotLockKind.Aim))
        {
            var snapshot = _lockSnapshots[slot] ?? CaptureSnapshot(pawn, parameters.Service);
            _lockSnapshots[slot] = snapshot;
            pawn.SnapViewAngles(
                new Vector(snapshot.Pitch, NormalizeAngle(snapshot.Yaw), snapshot.Roll));
        }

        if (_state.IsLocked(slot, BotLockKind.Jump))
        {
            ApplyButtonMask(
                parameters,
                command,
                0,
                (ulong)UserCommandButtons.Jump);
        }
    }

    private unsafe void ApplyReplayPre(
        int slot,
        IPlayerRunCommandHookParams parameters,
        IBasePlayerPawn pawn,
        CBaseUserCmdPb* command,
        BotReplayTick tick,
        ArraySegment<BotSubtickMove> subticks,
        BotReplayCommandFrame commandFrame,
        BotReplayMovementExtra movementExtra)
    {
        ApplySnapshot(pawn, parameters.Service, tick.Pre);
        var held = tick.Pre.Buttons;
        var pressed = tick.Pre.Buttons1;
        var released = tick.Pre.Buttons2;
        if (pressed == 0 && released == 0)
        {
            pressed = held & ~(ulong)parameters.KeyButtons;
            released = (ulong)parameters.KeyButtons & ~held;
        }

        parameters.KeyButtons = (UserCommandButtons)held;
        parameters.ChangedButtons = (UserCommandButtons)(pressed | released);
        parameters.ScrollButtons = (UserCommandButtons)tick.Pre.Buttons2;
        if (command != null)
        {
            if (command->ButtonState != null)
            {
                command->ButtonState->ButtonPressed = (UserCommandButtons)held;
                command->ButtonState->ButtonChanged = (UserCommandButtons)(pressed | released);
                command->ButtonState->ButtonScroll = (UserCommandButtons)tick.Pre.Buttons2;
            }

            if (((BotReplayCommandFields)commandFrame.Fields)
                .HasFlag(BotReplayCommandFields.Movement))
            {
                command->ForwardMove = commandFrame.ForwardMove;
                command->SideMove = commandFrame.LeftMove;
                command->UpMove = commandFrame.UpMove;
            }
            else
            {
                command->ForwardMove = subticks.Sum(item => item.AnalogForward);
                command->SideMove = subticks.Sum(item => item.AnalogLeft);
                command->UpMove = 0;
            }

            if (((BotReplayCommandFields)commandFrame.Fields)
                .HasFlag(BotReplayCommandFields.Mouse))
            {
                command->MouseX = commandFrame.MouseDx;
                command->MouseY = commandFrame.MouseDy;
            }

            var targetDefinition = tick.WeaponDefIndex;
            if (targetDefinition >= 0 && BotActiveWeaponDefForPawn(pawn) != targetDefinition)
            {
                if (pawn.AsPlayer() is { } player
                    && TryFindWeaponByDefinition(pawn, targetDefinition, out var weapon))
                {
                    player.SwitchWeapon(weapon);
                    command->WeaponSelect = weapon.Index.AsPrimitive();
                }
            }

            ApplyReplaySubticks(command, subticks);
        }

        if (((BotReplayMovementFields)movementExtra.Fields)
            .HasFlag(BotReplayMovementFields.JumpPressedTime)
            && pawn.AsPlayer()?.GetPlayerMovementService() is { } movement)
        {
            movement.JumpPressedTime = movementExtra.JumpPressedTime;
        }
    }

    private unsafe void ApplyWeaponLock(
        int slot,
        IBasePlayerPawn pawn,
        CBaseUserCmdPb* command)
    {
        var target = _state.GetWeaponLock(slot);
        var player = pawn.AsPlayer();
        if (!TryMapLockTarget(target, out var gearSlot)
            || player is null
            || player.GetWeaponBySlot(gearSlot) is not { IsValidEntity: true } weapon)
        {
            return;
        }

        var active = player.GetActiveWeapon();
        if (target == BotLockTarget.Slot4)
        {
            if (command != null
                && command->WeaponSelect > 0
                && _entities.FindEntityByIndex<IBaseWeapon>(
                    (EntityIndex)command->WeaponSelect) is
                {
                    IsValidEntity: true,
                    Slot: GearSlot.Grenades,
                })
            {
                return;
            }

            if ((command == null || command->WeaponSelect == 0)
                && active is { IsValidEntity: true, Slot: GearSlot.Grenades })
            {
                return;
            }
        }

        var switchingAway = command != null
            && command->WeaponSelect != 0
            && command->WeaponSelect != weapon.Index.AsPrimitive();
        if (active?.GetAbsPtr() == weapon.GetAbsPtr() && !switchingAway)
        {
            return;
        }

        if (command != null)
        {
            command->WeaponSelect = weapon.Index.AsPrimitive();
        }

        player.SelectItem(weapon);
    }

    private unsafe void ApplyUsercmdInjections(
        int slot,
        IPlayerRunCommandHookParams parameters,
        CBaseUserCmdPb* command)
    {
        if (Volatile.Read(ref _hasInjections[slot]) == 0)
        {
            return;
        }

        ulong activeMask = 0;
        var now = Environment.TickCount64;
        var hasRemaining = false;
        lock (_injections[slot])
        {
            for (var index = _injections[slot].Count - 1; index >= 0; index--)
            {
                var injection = _injections[slot][index];
                switch (injection.Phase)
                {
                    case InjectionPhase.PendingRelease:
                        _injections[slot].RemoveAt(index);
                        continue;
                    case InjectionPhase.PendingPress:
                        activeMask |= injection.ButtonMask;
                        _injections[slot][index] = injection with
                        {
                            Phase = injection.DurationMs == 0
                                ? InjectionPhase.PendingRelease
                                : InjectionPhase.Holding,
                            ExpiresAt = injection.DurationMs == 0
                                ? 0
                                : now + injection.DurationMs,
                        };
                        hasRemaining = true;
                        continue;
                    case InjectionPhase.Holding when now < injection.ExpiresAt:
                        activeMask |= injection.ButtonMask;
                        hasRemaining = true;
                        continue;
                    case InjectionPhase.Holding:
                        _injections[slot].RemoveAt(index);
                        continue;
                }
            }

            if (!hasRemaining && _injections[slot].Count > 0)
            {
                hasRemaining = true;
            }
        }

        var previousMask = _lastInjectionMasks[slot];
        var pressMask = activeMask & ~previousMask;
        var releaseMask = previousMask & ~activeMask;
        _lastInjectionMasks[slot] = activeMask;
        if (!hasRemaining && activeMask == 0)
        {
            Volatile.Write(ref _hasInjections[slot], 0);
        }

        if (activeMask == 0 && pressMask == 0 && releaseMask == 0)
        {
            return;
        }

        ApplyButtonMask(parameters, command, activeMask, releaseMask);
        parameters.ChangedButtons |= (UserCommandButtons)(pressMask | releaseMask);
        if (command != null && command->ButtonState != null)
        {
            command->ButtonState->ButtonChanged |=
                (UserCommandButtons)(pressMask | releaseMask);
        }

        if (pressMask != 0)
        {
            Interlocked.Increment(ref _buttonInjections);
        }
    }

    private void ClearUsercmdInjections(int slot)
    {
        if (slot is < 0 or >= BotControllerStateStore.MaximumSlots)
        {
            return;
        }

        lock (_injections[slot])
        {
            _injections[slot].Clear();
            _lastInjectionMasks[slot] = 0;
            Volatile.Write(ref _hasInjections[slot], 0);
        }
    }

    private static unsafe void ApplyButtonMask(
        IPlayerRunCommandHookParams parameters,
        CBaseUserCmdPb* command,
        ulong setMask,
        ulong clearMask)
    {
        parameters.KeyButtons =
            (parameters.KeyButtons & ~(UserCommandButtons)clearMask)
            | (UserCommandButtons)setMask;
        if (command == null || command->ButtonState == null)
        {
            return;
        }

        command->ButtonState->ButtonPressed =
            (command->ButtonState->ButtonPressed & ~(UserCommandButtons)clearMask)
            | (UserCommandButtons)setMask;
    }

    private unsafe BotMovementSnapshot CaptureSnapshot(
        IBasePlayerPawn pawn,
        IMovementService service)
    {
        var origin = pawn.GetAbsOrigin();
        var velocity = pawn.GetAbsVelocity();
        var angles = pawn.GetEyeAngles();
        var pointer = service.GetAbsPtr();
        var ladder = _movementOffsets.LadderNormal > 0
            ? *(Vector*)(pointer + _movementOffsets.LadderNormal)
            : new Vector();
        return new BotMovementSnapshot
        {
            OriginX = origin.X,
            OriginY = origin.Y,
            OriginZ = origin.Z,
            VelX = velocity.X,
            VelY = velocity.Y,
            VelZ = velocity.Z,
            Pitch = angles.X,
            Yaw = angles.Y,
            Roll = angles.Z,
            EntityFlags = (uint)pawn.Flags,
            MoveType = (byte)pawn.MoveType,
            Buttons = (ulong)service.KeyButtons,
            Buttons1 = (ulong)service.KeyChangedButtons,
            Buttons2 = (ulong)service.ScrollButtons,
            DuckAmount = ReadFloat(pointer, _movementOffsets.DuckAmount),
            DuckSpeed = ReadFloat(pointer, _movementOffsets.DuckSpeed),
            LadderNormalX = ladder.X,
            LadderNormalY = ladder.Y,
            LadderNormalZ = ladder.Z,
            Ducked = ReadByte(pointer, _movementOffsets.Ducked),
            Ducking = ReadByte(pointer, _movementOffsets.Ducking),
            DesiresDuck = ReadByte(pointer, _movementOffsets.DesiresDuck),
            ActualMoveType = (byte)pawn.ActualMoveType,
        };
    }

    private unsafe void ApplySnapshot(
        IBasePlayerPawn pawn,
        IMovementService service,
        BotMovementSnapshot snapshot)
    {
        pawn.SetAbsOrigin(new Vector(snapshot.OriginX, snapshot.OriginY, snapshot.OriginZ));
        pawn.SetAbsVelocity(new Vector(snapshot.VelX, snapshot.VelY, snapshot.VelZ));
        pawn.SetMoveType((MoveType)snapshot.MoveType);
        if (_actualMoveTypeOffset > 0)
        {
            *(byte*)(pawn.GetAbsPtr() + _actualMoveTypeOffset) = snapshot.ActualMoveType;
        }

        var flags = (uint)pawn.Flags;
        flags = (flags & ~ReplayFlagMask) | (snapshot.EntityFlags & ReplayFlagMask);
        pawn.Flags = (EntityFlags)flags;
        pawn.SnapViewAngles(
            new Vector(snapshot.Pitch, NormalizeAngle(snapshot.Yaw), snapshot.Roll));
        service.KeyButtons = (UserCommandButtons)snapshot.Buttons;
        service.KeyChangedButtons = (UserCommandButtons)snapshot.Buttons1;
        service.ScrollButtons = (UserCommandButtons)snapshot.Buttons2;
        var pointer = service.GetAbsPtr();
        WriteFloat(pointer, _movementOffsets.DuckAmount, snapshot.DuckAmount);
        WriteFloat(pointer, _movementOffsets.DuckSpeed, snapshot.DuckSpeed);
        if (_movementOffsets.LadderNormal > 0)
        {
            *(Vector*)(pointer + _movementOffsets.LadderNormal) = new Vector(
                snapshot.LadderNormalX,
                snapshot.LadderNormalY,
                snapshot.LadderNormalZ);
        }

        WriteByte(pointer, _movementOffsets.Ducked, snapshot.Ducked);
        WriteByte(pointer, _movementOffsets.Ducking, snapshot.Ducking);
        WriteByte(pointer, _movementOffsets.DesiresDuck, snapshot.DesiresDuck);
    }

    private static unsafe List<BotSubtickMove> CaptureSubticks(
        IPlayerRunCommandHookParams parameters)
    {
        var count = Math.Min(
            parameters.SubtickMoveSize,
            BotControllerStateStore.MaximumSubticksPerTick);
        var result = new List<BotSubtickMove>(count);
        for (var index = 0; index < count; index++)
        {
            var move = parameters.GetSubtickMove(index);
            if (move == null)
            {
                continue;
            }

            result.Add(
                new BotSubtickMove
                {
                    When = move->When,
                    Button = (uint)move->Buttons,
                    Pressed = move->Pressed ? 1f : 0f,
                    AnalogForward = move->AnalogForwardDelta,
                    AnalogLeft = move->AnalogLeftDelta,
                    PitchDelta = move->AnalogPitchDelta,
                    YawDelta = move->AnalogYawDelta,
                });
        }

        return result;
    }

    private static unsafe BotReplayCommandFrame CaptureCommand(
        IPlayerRunCommandHookParams parameters,
        CBaseUserCmdPb* command)
    {
        if (command == null)
        {
            return default;
        }

        return new BotReplayCommandFrame
        {
            ForwardMove = command->ForwardMove,
            LeftMove = command->SideMove,
            UpMove = command->UpMove,
            Pitch = parameters.Pawn.GetEyeAngles().X,
            Yaw = parameters.Pawn.GetEyeAngles().Y,
            Roll = parameters.Pawn.GetEyeAngles().Z,
            Buttons = (ulong)parameters.KeyButtons,
            Buttons1 = (ulong)parameters.ChangedButtons,
            Buttons2 = (ulong)parameters.ScrollButtons,
            MouseDx = command->MouseX,
            MouseDy = command->MouseY,
            WeaponSelect = command->WeaponSelect,
            Fields = (uint)(
                BotReplayCommandFields.Movement
                | BotReplayCommandFields.ViewAngles
                | BotReplayCommandFields.Buttons
                | BotReplayCommandFields.Mouse
                | BotReplayCommandFields.WeaponSelect
                | BotReplayCommandFields.LeftHandDesired),
            LeftHandDesired = parameters.CSGOUserCmd != null
                && parameters.CSGOUserCmd->LeftHandDesired
                    ? (byte)1
                    : (byte)0,
        };
    }

    private static BotReplayMovementExtra CaptureMovementExtra(IBasePlayerPawn pawn)
    {
        if (pawn.AsPlayer()?.GetPlayerMovementService() is not { } movement)
        {
            return default;
        }

        return new BotReplayMovementExtra
        {
            Fields = (uint)BotReplayMovementFields.JumpPressedTime,
            JumpPressedTime = movement.JumpPressedTime,
        };
    }

    private static unsafe void ApplyReplaySubticks(
        CBaseUserCmdPb* command,
        ArraySegment<BotSubtickMove> subticks)
    {
        // ModSharp exposes the command's existing protobuf allocation but not
        // the engine allocator. Reuse the current entries safely; deterministic
        // post snapshots still preserve motion when a command has fewer entries.
        var count = Math.Min(subticks.Count, command->SubtickMoves.nCurrentSize);
        for (var index = 0; index < count; index++)
        {
            var target = command->SubtickMoves[index];
            if (target == null)
            {
                continue;
            }

            var source = subticks.Array![subticks.Offset + index];
            target->When = source.When;
            target->Buttons = (UserCommandButtons)source.Button;
            target->Pressed = source.Pressed != 0;
            target->AnalogForwardDelta = source.AnalogForward;
            target->AnalogLeftDelta = source.AnalogLeft;
            target->AnalogPitchDelta = source.PitchDelta;
            target->AnalogYawDelta = source.YawDelta;
        }
    }

    private bool InstallNativeLockHooks()
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var server = _shared.GetLibraryModuleManager().Server;
        var updateTarget = server.FindPatternExactly(isWindows ? UpdateWindows : UpdateLinux);
        var upkeepTarget = server.FindPatternExactly(isWindows ? UpkeepWindows : UpkeepLinux);
        if (updateTarget == 0 || upkeepTarget == 0)
        {
            _logger.LogWarning(
                "BotController native lock signatures unresolved (Update=0x{Update:X}, Upkeep=0x{Upkeep:X}); command-level fallback remains active.",
                updateTarget,
                upkeepTarget);
            return false;
        }

        unsafe
        {
            _updateHook = _hooks.CreateDetourHook();
            _updateHook.Prepare(
                updateTarget,
                (nint)(delegate* unmanaged<nint, void>)&HookUpdate);
            if (!_updateHook.Install())
            {
                RemoveNativeHooks();
                return false;
            }

            s_updateOriginal = (delegate* unmanaged<nint, void>)_updateHook.Trampoline;
            _upkeepHook = _hooks.CreateDetourHook();
            _upkeepHook.Prepare(
                upkeepTarget,
                (nint)(delegate* unmanaged<nint, void>)&HookUpkeep);
            if (!_upkeepHook.Install())
            {
                RemoveNativeHooks();
                return false;
            }

            s_upkeepOriginal = (delegate* unmanaged<nint, void>)_upkeepHook.Trampoline;
            var jumpTarget = server.FindPatternExactly(isWindows ? JumpWindows : JumpLinux);
            if (jumpTarget != 0)
            {
                _jumpHook = _hooks.CreateDetourHook();
                _jumpHook.Prepare(
                    jumpTarget,
                    (nint)(delegate* unmanaged<nint, byte, byte>)&HookJump);
                if (_jumpHook.Install())
                {
                    s_jumpOriginal =
                        (delegate* unmanaged<nint, byte, byte>)_jumpHook.Trampoline;
                }
                else
                {
                    DisposeHook(ref _jumpHook, "CCSBot::Jump");
                }
            }
        }

        return true;
    }

    private bool InstallBuyHook()
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var target = _shared.GetLibraryModuleManager().Server.FindPatternExactly(
            isWindows ? BuyUpdateWindows : BuyUpdateLinux);
        if (target == 0)
        {
            _logger.LogWarning(
                "BotController BuyState::OnUpdate signature unresolved; forced plans use the round-start fallback.");
            return false;
        }

        unsafe
        {
            _buyUpdateHook = _hooks.CreateDetourHook();
            _buyUpdateHook.Prepare(
                target,
                (nint)(delegate* unmanaged<nint, nint, void>)&HookBuyUpdate);
            if (!_buyUpdateHook.Install())
            {
                DisposeHook(ref _buyUpdateHook, "BuyState::OnUpdate");
                return false;
            }

            s_buyUpdateOriginal =
                (delegate* unmanaged<nint, nint, void>)_buyUpdateHook.Trampoline;
        }

        return true;
    }

    private void RemoveHooks()
    {
        RemoveBuyHook();
        RemoveNativeHooks();
        if (_frameworkHooksInstalled)
        {
            _hooks.PlayerRunCommand.RemoveHookPost(OnPlayerRunCommandPost);
            _hooks.PlayerRunCommand.RemoveHookPre(OnPlayerRunCommandPre);
            _frameworkHooksInstalled = false;
        }
    }

    private void RemoveNativeHooks()
    {
        DisposeHook(ref _jumpHook, "CCSBot::Jump");
        DisposeHook(ref _upkeepHook, "CCSBot::Upkeep");
        DisposeHook(ref _updateHook, "CCSBot::Update");
        unsafe
        {
            s_jumpOriginal = null;
            s_upkeepOriginal = null;
            s_updateOriginal = null;
        }

        _nativeLocksInstalled = false;
    }

    private void RemoveBuyHook()
    {
        DisposeHook(ref _buyUpdateHook, "BuyState::OnUpdate");
        unsafe
        {
            s_buyUpdateOriginal = null;
        }

        _buyHookInstalled = false;
    }

    private void DisposeHook(ref IDetourHook? hookField, string name)
    {
        var hook = Interlocked.Exchange(ref hookField, null);
        if (hook is null)
        {
            return;
        }

        try
        {
            hook.Uninstall();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to uninstall {HookName} cleanly.", name);
        }
        finally
        {
            hook.Dispose();
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe void HookUpdate(nint bot)
    {
        var original = s_updateOriginal;
        if (original == null)
        {
            return;
        }

        var runtime = s_active;
        try
        {
            if (runtime is not null
                && runtime._active
                && runtime.TryResolveBotSlot(bot, out var slot)
                && (runtime._state.IsLocked(slot, BotLockKind.All)
                    || runtime._state.IsReplaying(slot)))
            {
                *(byte*)(bot + runtime._botAiTickedOffset) = 1;
                Interlocked.Increment(ref runtime._nativeLockBypasses);
                return;
            }
        }
        catch
        {
            if (runtime is not null)
            {
                Interlocked.Increment(ref runtime._errors);
            }
        }

        original(bot);
    }

    [UnmanagedCallersOnly]
    private static unsafe void HookUpkeep(nint bot)
    {
        var original = s_upkeepOriginal;
        if (original == null)
        {
            return;
        }

        var runtime = s_active;
        try
        {
            if (runtime is not null
                && runtime._active
                && runtime.TryResolveBotSlot(bot, out var slot)
                && (runtime._state.IsLocked(slot, BotLockKind.All)
                    || runtime._state.IsLocked(slot, BotLockKind.Aim)
                    || runtime._state.IsReplaying(slot)))
            {
                Interlocked.Increment(ref runtime._nativeLockBypasses);
                return;
            }
        }
        catch
        {
            if (runtime is not null)
            {
                Interlocked.Increment(ref runtime._errors);
            }
        }

        original(bot);
    }

    [UnmanagedCallersOnly]
    private static unsafe byte HookJump(nint bot, byte mustJump)
    {
        var original = s_jumpOriginal;
        if (original == null)
        {
            return 0;
        }

        var runtime = s_active;
        try
        {
            if (runtime is not null
                && runtime._active
                && runtime.TryResolveBotSlot(bot, out var slot)
                && runtime._state.IsLocked(slot, BotLockKind.Jump))
            {
                Interlocked.Increment(ref runtime._nativeLockBypasses);
                return 0;
            }
        }
        catch
        {
            if (runtime is not null)
            {
                Interlocked.Increment(ref runtime._errors);
            }
        }

        return original(bot, mustJump);
    }

    [UnmanagedCallersOnly]
    private static unsafe void HookBuyUpdate(nint state, nint bot)
    {
        var original = s_buyUpdateOriginal;
        if (original == null)
        {
            return;
        }

        var runtime = s_active;
        try
        {
            if (runtime is not null
                && runtime._active
                && runtime.TryResolveBotSlot(bot, out var slot))
            {
                if (runtime._state.IsReplaying(slot))
                {
                    return;
                }

                if (runtime._state.GetBuyPlan(slot) is not { } plan)
                {
                    original(state, bot);
                    return;
                }

                var initialDelay = *(byte*)(state + BuyInitialDelayOffset);
                if (initialDelay != 0 && runtime._lastBuyInitialDelay[slot] == 0)
                {
                    runtime.ApplyBuyPlan(slot, plan);
                    *(byte*)(state + BuyDoneOffset) = 1;
                }

                runtime._lastBuyInitialDelay[slot] = initialDelay;
            }
        }
        catch
        {
            if (runtime is not null)
            {
                Interlocked.Increment(ref runtime._errors);
            }
        }

        original(state, bot);
    }

    private void ApplyBuyPlan(int slot, BotControllerStateStore.BuyPlan plan)
    {
        if (_clients.GetGameClient(new PlayerSlot((byte)slot)) is not
            {
                IsValid: true,
                IsInGame: true,
                IsFakeClient: true,
            } client)
        {
            return;
        }

        if (!plan.Skip)
        {
            foreach (var alias in plan.Items)
            {
                client.FakeCommand($"buy {alias}");
            }
        }

        Interlocked.Increment(ref _buyPlansApplied);
    }

    private unsafe bool TryResolveBotSlot(nint bot, out int slot)
    {
        slot = -1;
        if (bot == 0 || _pawnBotOffset <= 0)
        {
            return false;
        }

        if (_botSlots.TryGetValue(bot, out slot))
        {
            return true;
        }

        foreach (var client in _clients.GetGameClients(inGame: true))
        {
            var candidate = client.Slot.AsPrimitive();
            if (candidate is < 0 or >= BotControllerStateStore.MaximumSlots
                || !BotIdentityRegistry.IsBot(client.IsFakeClient, candidate)
                || client.GetPlayerController()?.GetPlayerPawn() is not
                {
                    IsValidEntity: true,
                } pawn
                || *(nint*)(pawn.GetAbsPtr() + _pawnBotOffset) != bot)
            {
                continue;
            }

            slot = candidate;
            _botSlots[bot] = slot;
            return true;
        }

        return false;
    }

    private bool TrySwitchToLockTarget(int slot, BotLockTarget target)
    {
        if (!TryMapLockTarget(target, out var gearSlot)
            || !TryGetManagedBotPawn(slot, out var pawn)
            || pawn.GetWeaponBySlot(gearSlot) is not { IsValidEntity: true } weapon)
        {
            return false;
        }

        pawn.SwitchWeapon(weapon);
        return true;
    }

    private bool TryGetManagedBotPawn(int slot, out IPlayerPawn pawn)
    {
        pawn = null!;
        if (slot is < 0 or >= BotControllerStateStore.MaximumSlots
            || _clients.GetGameClient(new PlayerSlot((byte)slot)) is not
            {
                IsValid: true,
            } client
            || !BotIdentityRegistry.IsBot(client.IsFakeClient, slot)
            || client.GetPlayerController()?.GetPlayerPawn() is not
            {
                IsValidEntity: true,
            } current)
        {
            return false;
        }

        pawn = current;
        return true;
    }

    private bool TryFindWeaponByDefinition(
        IBasePlayerPawn pawn,
        int definitionIndex,
        out IBaseWeapon weapon)
    {
        weapon = null!;
        var player = pawn.AsPlayer();
        if (player is null)
        {
            return false;
        }

        if (definitionIndex == IBotController.KnifeDefinition
            && player.GetWeaponBySlot(GearSlot.Knife) is { IsValidEntity: true } knife)
        {
            weapon = knife;
            return true;
        }

        if (player.GetWeaponService() is not { } weapons)
        {
            return false;
        }

        foreach (var handle in weapons.GetMyWeapons())
        {
            if (handle.IsValid()
                && _entities.FindEntityByHandle<IBaseWeapon>(handle) is
                {
                    IsValidEntity: true,
                } candidate
                && candidate.ItemDefinitionIndex == definitionIndex)
            {
                weapon = candidate;
                return true;
            }
        }

        return false;
    }

    private static int BotActiveWeaponDefForPawn(IBasePlayerPawn pawn)
    {
        if (pawn.AsPlayer()?.GetActiveWeapon() is not { IsValidEntity: true } weapon)
        {
            return -1;
        }

        return weapon.Slot == GearSlot.Knife
            ? IBotController.KnifeDefinition
            : weapon.ItemDefinitionIndex;
    }

    private void ResolveOffsets()
    {
        _pawnBotOffset = RequiredOffset("CCSPlayerPawn", "m_pBot");
        _actualMoveTypeOffset = RequiredOffset("CBaseEntity", "m_nActualMoveType");
        _movementOffsets = new MovementOffsets(
            RequiredOffset("CCSPlayer_MovementServices", "m_vecLadderNormal"),
            RequiredOffset("CCSPlayer_MovementServices", "m_bDucked"),
            RequiredOffset("CCSPlayer_MovementServices", "m_flDuckAmount"),
            RequiredOffset("CCSPlayer_MovementServices", "m_flDuckSpeed"),
            RequiredOffset("CCSPlayer_MovementServices", "m_bDesiresDuck"),
            RequiredOffset("CCSPlayer_MovementServices", "m_bDucking"));
        _botAiTickedOffset = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? 1552
            : 1544;
    }

    private int RequiredOffset(string className, string fieldName)
    {
        var offset = _schema.GetNetVarOffset(className, fieldName);
        if (offset <= 0)
        {
            throw new InvalidDataException(
                $"Schema field {className}::{fieldName} resolved to invalid offset {offset}.");
        }

        return offset;
    }

    private void ClearTransientState()
    {
        Array.Clear(_lockSnapshots);
        Array.Clear(_lastInjectionMasks);
        Array.Clear(_hasInjections);
        Array.Clear(_lastBuyInitialDelay);
        foreach (var injections in _injections)
        {
            lock (injections)
            {
                injections.Clear();
            }
        }
    }

    private int CountSlots(Func<int, bool> predicate)
    {
        var count = 0;
        for (var slot = 0; slot < BotControllerStateStore.MaximumSlots; slot++)
        {
            if (predicate(slot))
            {
                count++;
            }
        }

        return count;
    }

    private static bool TryMapLockTarget(BotLockTarget target, out GearSlot gearSlot)
    {
        gearSlot = target switch
        {
            BotLockTarget.Slot1 => GearSlot.Rifle,
            BotLockTarget.Slot2 => GearSlot.Pistol,
            BotLockTarget.Slot3 => GearSlot.Knife,
            BotLockTarget.Slot4 => GearSlot.Grenades,
            BotLockTarget.Slot5 => GearSlot.C4,
            _ => GearSlot.Invalid,
        };
        return gearSlot != GearSlot.Invalid;
    }

    private static bool TryMapGearSlot(GearSlot gearSlot, out BotLockTarget target)
    {
        target = gearSlot switch
        {
            GearSlot.Rifle => BotLockTarget.Slot1,
            GearSlot.Pistol => BotLockTarget.Slot2,
            GearSlot.Knife => BotLockTarget.Slot3,
            GearSlot.Grenades => BotLockTarget.Slot4,
            GearSlot.C4 => BotLockTarget.Slot5,
            _ => BotLockTarget.None,
        };
        return target != BotLockTarget.None;
    }

    private static bool IsFiniteProfile(BotProfileData profile)
        => float.IsFinite(profile.Aggression)
            && float.IsFinite(profile.Skill)
            && float.IsFinite(profile.Teamwork)
            && float.IsFinite(profile.ReactionTime)
            && float.IsFinite(profile.AttackDelay)
            && float.IsFinite(profile.LookAccelAtk)
            && float.IsFinite(profile.LookStiffAtk)
            && float.IsFinite(profile.LookDampAtk);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float ReadFloat(nint pointer, int offset)
        => offset > 0 ? *(float*)(pointer + offset) : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe byte ReadByte(nint pointer, int offset)
        => offset > 0 ? *(byte*)(pointer + offset) : (byte)0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void WriteFloat(nint pointer, int offset, float value)
    {
        if (offset > 0)
        {
            *(float*)(pointer + offset) = value;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void WriteByte(nint pointer, int offset, byte value)
    {
        if (offset > 0)
        {
            *(byte*)(pointer + offset) = value;
        }
    }

    private static float NormalizeAngle(float angle)
    {
        angle = (angle + 180f) % 360f;
        if (angle < 0)
        {
            angle += 360f;
        }

        return angle - 180f;
    }

    private readonly record struct MovementOffsets(
        int LadderNormal,
        int Ducked,
        int DuckAmount,
        int DuckSpeed,
        int DesiresDuck,
        int Ducking);

    private enum InjectionPhase : byte
    {
        PendingPress,
        Holding,
        PendingRelease,
    }

    private readonly record struct UsercmdInjection(
        long Id,
        ulong ButtonMask,
        int DurationMs,
        InjectionPhase Phase,
        long ExpiresAt);
}
