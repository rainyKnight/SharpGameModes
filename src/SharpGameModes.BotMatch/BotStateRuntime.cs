using Microsoft.Extensions.Logging;
using SharpGameModes.Contracts;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameEvents;
using Sharp.Shared.HookParams;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace SharpGameModes.BotMatch;

/// <summary>
/// Pure ModSharp implementation of the core BotState behavior. CCSBot is not
/// exposed as a managed ModSharp object, so all native fields are resolved by
/// Source 2 schema name at activation rather than by build-specific offsets.
/// </summary>
internal sealed class BotStateRuntime : IDisposable
{
    private const float NormalSmokeLength = 50f;
    private const float ExpandedSmokeLength = 500f;
    private const float ReloadInterruptCooldown = 0.75f;

    private readonly IModSharp _modSharp;
    private readonly IHookManager _hooks;
    private readonly ISchemaManager _schema;
    private readonly IConVarManager _conVars;
    private readonly IClientManager _clients;
    private readonly BotControllerRuntime _controller;
    private readonly BotStateFlashRuntime _flash;
    private readonly ILogger _logger;
    private readonly Random _random = new();
    private readonly BotBrainSnapshot?[] _snapshots = new BotBrainSnapshot?[64];
    private readonly bool[] _hasPreviousAttack = new bool[64];
    private readonly bool[] _previousAttack = new bool[64];
    private readonly bool[] _hasFiredThisAttack = new bool[64];
    private readonly sbyte[] _lastLateralDirection = new sbyte[64];
    private readonly bool[] _hasPreviousInAir = new bool[64];
    private readonly bool[] _previousInAir = new bool[64];
    private readonly sbyte[] _lastForwardDirection = new sbyte[64];
    private readonly float[] _ladderExitTime = new float[64];
    private readonly float[] _doorCooldownEnd = new float[64];
    private readonly bool[] _cachedInAir = new bool[64];
    private readonly bool[] _cachedNearLadder = new bool[64];
    private readonly bool[] _stuckTracking = new bool[64];
    private readonly float[] _stuckStartTime = new float[64];
    private readonly Vector[] _stuckStartPosition = new Vector[64];
    private readonly bool[] _stuckJumpDone = new bool[64];
    private readonly int[] _stuckJumpCount = new int[64];
    private readonly float[] _stuckMaximumSpeed = new float[64];
    private readonly bool[] _idleTracking = new bool[64];
    private readonly float[] _idleStartTime = new float[64];
    private readonly float[] _lastRepathTime = new float[64];
    private readonly float[] _reloadInterruptCooldown = new float[64];
    private BotStateOffsets _offsets;
    private IConVar? _smokeConVar;
    private Guid _maintenanceTimer;
    private float _nextGunReequipAt;
    private float _hurtSmokeUntil;
    private float _defuseSmokeCycleStartedAt;
    private bool _bombDefuseActive;
    private bool _smokeExpanded;
    private bool _offsetsResolved;
    private bool _postThinkInstalled;
    private bool _active;
    private long _thinkCalls;
    private long _botsUpdated;
    private long _weaponFireAdjustments;
    private long _stuckRecoveries;
    private long _idleRepaths;
    private long _reloadInterrupts;
    private long _gunReequips;
    private long _inspectInjections;
    private long _smokeExpansions;
    private long _hookErrors;
    private int _firstUpdateLogged;

    public BotStateRuntime(
        ISharedSystem shared,
        IClientManager clients,
        BotControllerRuntime controller,
        ILogger logger)
    {
        _modSharp = shared.GetModSharp();
        _hooks = shared.GetHookManager();
        _schema = shared.GetSchemaManager();
        _conVars = shared.GetConVarManager();
        _clients = clients;
        _controller = controller;
        _flash = new BotStateFlashRuntime(shared, clients, logger);
        _logger = logger;
        Array.Fill(_lastRepathTime, -999f);
    }

    public bool Activate()
    {
        if (_active)
        {
            return true;
        }

        try
        {
            ResolveOffsets();
            ResetTransientState();
            _smokeConVar = _conVars.FindConVar("bot_max_visible_smoke_length")
                ?? _conVars.FindConVar("bot_max_visible_smoke_length", useIterator: true);
            _hooks.PlayerPostThink.InstallForward(OnPlayerPostThink);
            _postThinkInstalled = true;
            _active = true;
            if (!_flash.Activate())
            {
                _logger.LogWarning(
                    "BotState core is active, but flash avoidance is unavailable.");
            }

            _maintenanceTimer = _modSharp.PushTimer(
                MaintenanceTick,
                0.1,
                GameTimerFlags.Repeatable);
            _logger.LogInformation(
                "Pure ModSharp BotState core enabled with schema-resolved CCSBot fields (pawnBot=0x{PawnBot:X}, safeTime=0x{SafeTime:X}, repathTimer=0x{RepathTimer:X}).",
                _offsets.PawnBot,
                _offsets.SafeTime,
                _offsets.RepathTimer);
            return true;
        }
        catch (Exception exception)
        {
            _active = false;
            StopMaintenanceTimer();
            _flash.Deactivate();
            if (_postThinkInstalled)
            {
                _hooks.PlayerPostThink.RemoveForward(OnPlayerPostThink);
                _postThinkInstalled = false;
            }

            _logger.LogError(
                exception,
                "Failed to enable pure ModSharp BotState core because one or more schema fields could not be resolved.");
            return false;
        }
    }

    public void Deactivate()
    {
        if (!_active && !_snapshots.Any(snapshot => snapshot is not null))
        {
            ResetTransientState();
            return;
        }

        if (_active)
        {
            _active = false;
            StopMaintenanceTimer();
            _flash.Deactivate();
            if (_postThinkInstalled)
            {
                _hooks.PlayerPostThink.RemoveForward(OnPlayerPostThink);
                _postThinkInstalled = false;
            }
        }

        SetSmokeLength(expanded: false);
        RestoreAllSnapshots();
        ResetTransientState();
        _logger.LogInformation(
            "Pure ModSharp BotState core disabled. Think calls {ThinkCalls}, bot updates {BotUpdates}, fire adjustments {FireAdjustments}, stuck recoveries {StuckRecoveries}, idle repaths {IdleRepaths}, reload interrupts {ReloadInterrupts}, gun reequips {GunReequips}, inspect injections {InspectInjections}, smoke expansions {SmokeExpansions}, hook errors {HookErrors}.",
            Interlocked.Read(ref _thinkCalls),
            Interlocked.Read(ref _botsUpdated),
            Interlocked.Read(ref _weaponFireAdjustments),
            Interlocked.Read(ref _stuckRecoveries),
            Interlocked.Read(ref _idleRepaths),
            Interlocked.Read(ref _reloadInterrupts),
            Interlocked.Read(ref _gunReequips),
            Interlocked.Read(ref _inspectInjections),
            Interlocked.Read(ref _smokeExpansions),
            Interlocked.Read(ref _hookErrors));
    }

    public void HandleGameEvent(IGameEvent gameEvent)
    {
        if (!_active)
        {
            return;
        }

        try
        {
            _flash.HandleGameEvent(gameEvent);
            switch (gameEvent.Name)
            {
                case "round_start":
                    ResetRoundState();
                    break;
                case "player_hurt" when gameEvent is IEventPlayerHurt playerHurt:
                    HandlePlayerHurt(playerHurt);
                    break;
                case "player_death" when gameEvent is IEventPlayerDeath playerDeath:
                    HandlePlayerDeath(playerDeath);
                    break;
                case "weapon_fire" when gameEvent is IEventWeaponFired weaponFired:
                    HandleWeaponFire(weaponFired);
                    break;
                case "bomb_planted":
                    HandleBombPlanted();
                    break;
                case "bomb_begindefuse":
                    HandleBombBeginDefuse(gameEvent);
                    break;
                case "bomb_abortdefuse":
                case "bomb_defused":
                case "bomb_exploded":
                    StopDefuseSmoke();
                    break;
                case "door_open":
                case "door_close":
                    HandleDoorEvent(gameEvent);
                    break;
            }
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _hookErrors);
            _logger.LogWarning(exception, "BotState event handler failed for {EventName}.", gameEvent.Name);
        }
    }

    public void Release(IGameClient client)
    {
        var slot = client.Slot.AsPrimitive();
        if (slot is < 0 or >= 64)
        {
            return;
        }

        _snapshots[slot] = null;
        _flash.Release(slot);
        ResetSlotState(slot);
    }

    public bool FlashDebugEnabled => _flash.DebugEnabled;

    public void SetFlashDebug(bool enabled) => _flash.SetDebug(enabled);

    public bool InjectInspect(int slot)
    {
        if (!_controller.InjectButton(slot, UserCommandButtons.LookAtWeapon))
        {
            return false;
        }

        Interlocked.Increment(ref _inspectInjections);
        return true;
    }

    public void QueueInspect(int slot)
        => _modSharp.InvokeFrameAction(
            () =>
            {
                if (_active)
                {
                    InjectInspect(slot);
                }
            });

    public void Dispose() => Deactivate();

    private void OnPlayerPostThink(IPlayerThinkForwardParams parameters)
    {
        if (!_active)
        {
            return;
        }

        Interlocked.Increment(ref _thinkCalls);
        try
        {
            var slot = parameters.Client.Slot.AsPrimitive();
            if (slot is < 0 or >= 64
                || !BotIdentityRegistry.IsBot(parameters.Client.IsFakeClient, slot)
                || IsTakenOver(parameters.Controller)
                || !parameters.Pawn.IsValidEntity
                || !TryGetBotPointer(parameters.Pawn, out var botPointer))
            {
                return;
            }

            EnsureSnapshot(
                slot,
                parameters.Client.UserId.AsPrimitive(),
                botPointer);
            ApplyBotThink(slot, parameters.Pawn, botPointer);
            Interlocked.Increment(ref _botsUpdated);
            if (Interlocked.CompareExchange(ref _firstUpdateLogged, 1, 0) == 0)
            {
                _logger.LogInformation(
                    "BotState first end-to-end CCSBot update applied to slot {Slot}.",
                    slot);
            }
        }
        catch
        {
            Interlocked.Increment(ref _hookErrors);
        }
    }

    private unsafe void ApplyBotThink(int slot, IPlayerPawn pawn, nint botPointer)
    {
        var now = _modSharp.GetGlobals().CurTime;
        _flash.ProcessBot(slot, pawn, now);
        InterruptReload(slot, pawn, botPointer, now);
        WriteBool(botPointer, _offsets.IsSleeping, false);
        WriteBool(botPointer, _offsets.AllowActive, true);
        WriteBool(botPointer, _offsets.IsRapidFiring, true);
        WriteFloat(botPointer, _offsets.PeripheralTimestamp, 0f);
        WriteFloat(botPointer, _offsets.FireWeaponTimestamp, 0f);
        WriteTimer(botPointer, _offsets.AlertTimer, 600f, now + 600f, 1f);
        WriteTimer(botPointer, _offsets.IgnoreEnemiesTimer, 0f, 0f, 1f);
        WriteTimer(botPointer, _offsets.PanicTimer, 0f, 0f, 1f);
        WriteTimer(botPointer, _offsets.SurpriseTimer, 0f, 0f, 1f);
        WriteBool(botPointer, _offsets.IsEnemySniperVisible, true);
        WriteTimer(botPointer, _offsets.SawEnemySniperTimer, 600f, now + 600f, 1f);
        WriteBool(botPointer, _offsets.IsWaitingBehindFriend, false);
        WriteTimer(botPointer, _offsets.PoliteTimer, 0f, 0f, 1f);
        WriteFloat(botPointer, _offsets.SafeTime, 0f);
        WriteBool(botPointer, _offsets.HasVisitedEnemySpawn, true);

        var attacking = ReadBool(botPointer, _offsets.IsAttacking);
        if (attacking && _hasFiredThisAttack[slot])
        {
            _hasFiredThisAttack[slot] = false;
            var weaponIndex = pawn.GetActiveWeapon()?.ItemDefinitionIndex ?? 0;
            var lastDirection = _lastLateralDirection[slot];
            if (weaponIndex is 9 or 40 && lastDirection != 0)
            {
                var velocity = pawn.GetAbsVelocity();
                var yaw = pawn.GetEyeAngles().Y * MathF.PI / 180f;
                var rightX = -MathF.Sin(yaw);
                var rightY = MathF.Cos(yaw);
                velocity.X += rightX * -lastDirection * 250f;
                velocity.Y += rightY * -lastDirection * 250f;
                pawn.SetAbsVelocity(velocity);
                ResetLookAround(botPointer);
            }
        }

        if (attacking)
        {
            WriteBool(botPointer, _offsets.EyeAnglesUnderPathFinderControl, false);
            WriteFloat(botPointer, _offsets.InhibitLookAroundTimestamp, 0f);
        }

        if (ReadBool(botPointer, _offsets.IsAimingAtEnemy) && !attacking)
        {
            WriteBool(botPointer, _offsets.IsAttacking, true);
        }

        if (_hasPreviousAttack[slot] && _previousAttack[slot] && !attacking)
        {
            WriteBool(botPointer, _offsets.IsCrouching, false);
        }

        _hasPreviousAttack[slot] = true;
        _previousAttack[slot] = attacking;

        var velocityNow = pawn.GetAbsVelocity();
        if (!attacking)
        {
            _hasFiredThisAttack[slot] = false;
            var yaw = pawn.GetEyeAngles().Y * MathF.PI / 180f;
            var lateralX = -MathF.Sin(yaw);
            var lateralY = MathF.Cos(yaw);
            var lateralSpeed = (velocityNow.X * lateralX) + (velocityNow.Y * lateralY);
            if (MathF.Abs(lateralSpeed) > 10f)
            {
                _lastLateralDirection[slot] = lateralSpeed > 0f ? (sbyte)1 : (sbyte)-1;
            }
        }

        var ladderNormal = new Vector();
        if (pawn.AsPlayer()?.GetPlayerMovementService() is { } movementService)
        {
            var movementPointer = movementService.GetAbsPtr();
            if (movementPointer != 0)
            {
                ladderNormal = *(Vector*)(
                    movementPointer + _offsets.LadderNormal);
            }
        }

        var nearLadder = BotStatePolicy.IsNearLadder(
            pawn.MoveType == MoveType.Ladder,
            ladderNormal.X,
            ladderNormal.Y,
            ladderNormal.Z);
        if (nearLadder)
        {
            _ladderExitTime[slot] = now;
        }

        var inLadderCooldown = nearLadder || now - _ladderExitTime[slot] < 5f;
        var inAir = !inLadderCooldown
            && (pawn.GroundEntity is null || !pawn.GroundEntity.IsValidEntity);
        var previousInAir = _hasPreviousInAir[slot] && _previousInAir[slot];

        if (now < _doorCooldownEnd[slot])
        {
            _hasPreviousInAir[slot] = true;
            _previousInAir[slot] = inAir;
            return;
        }

        var eyeYaw = pawn.GetEyeAngles().Y * MathF.PI / 180f;
        var forwardX = MathF.Cos(eyeYaw);
        var forwardY = MathF.Sin(eyeYaw);
        var forwardSpeed = (velocityNow.X * forwardX) + (velocityNow.Y * forwardY);
        if (MathF.Abs(forwardSpeed) >= 20f)
        {
            _lastForwardDirection[slot] = forwardSpeed > 0f ? (sbyte)1 : (sbyte)-1;
        }

        if (inAir)
        {
            if (!IsDefusing(pawn))
            {
                WriteBool(botPointer, _offsets.IsCrouching, true);
            }

            if (!attacking)
            {
                var direction = MathF.Abs(forwardSpeed) >= 20f
                    ? MathF.Sign(forwardSpeed)
                    : _lastForwardDirection[slot] == 0
                        ? 1f
                        : _lastForwardDirection[slot];
                var targetSpeed = direction * 215f;
                var delta = targetSpeed - forwardSpeed;
                if ((targetSpeed > 0f && delta > 0f) || (targetSpeed < 0f && delta < 0f))
                {
                    const float accelerationPerTick = 12f * 0.015625f;
                    var addSpeed = delta * accelerationPerTick;
                    velocityNow.X += forwardX * addSpeed;
                    velocityNow.Y += forwardY * addSpeed;
                    pawn.SetAbsVelocity(velocityNow);
                }
            }
        }

        if (previousInAir && !inAir)
        {
            WriteBool(botPointer, _offsets.IsCrouching, false);
        }

        _hasPreviousInAir[slot] = true;
        _previousInAir[slot] = inAir;
        _cachedInAir[slot] = inAir;
        _cachedNearLadder[slot] = nearLadder;
        HandleStuckAndIdle(slot, pawn, botPointer, attacking, now);
    }

    private TimerAction MaintenanceTick()
    {
        if (!_active)
        {
            return TimerAction.Stop;
        }

        try
        {
            var now = _modSharp.GetGlobals().CurTime;
            _flash.Prune(now);
            var defuseExpansion = _bombDefuseActive
                && ((now - _defuseSmokeCycleStartedAt) % 5f) >= 3.5f;
            SetSmokeLength(now < _hurtSmokeUntil || defuseExpansion);

            if (now >= _nextGunReequipAt)
            {
                _nextGunReequipAt = now + 1f;
                ReequipGunsForActiveBots();
            }
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _hookErrors);
            _logger.LogWarning(exception, "BotState maintenance tick failed.");
        }

        return TimerAction.Continue;
    }

    private void StopMaintenanceTimer()
    {
        if (_maintenanceTimer == Guid.Empty)
        {
            return;
        }

        _modSharp.StopTimer(_maintenanceTimer);
        _maintenanceTimer = Guid.Empty;
    }

    private void SetSmokeLength(bool expanded)
    {
        if (_smokeExpanded == expanded)
        {
            return;
        }

        _smokeExpanded = expanded;
        if (_smokeConVar is null)
        {
            return;
        }

        _smokeConVar.SetString(
            (expanded ? ExpandedSmokeLength : NormalSmokeLength)
            .ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (expanded)
        {
            Interlocked.Increment(ref _smokeExpansions);
        }
    }

    private void StopDefuseSmoke()
    {
        _bombDefuseActive = false;
        _defuseSmokeCycleStartedAt = 0f;
        SetSmokeLength(_modSharp.GetGlobals().CurTime < _hurtSmokeUntil);
    }

    private void InterruptReload(int slot, IPlayerPawn pawn, nint botPointer, float now)
    {
        if (now < _reloadInterruptCooldown[slot]
            || !ReadBool(botPointer, _offsets.IsEnemyVisible)
            || pawn.GetActiveWeapon() is not { IsValidEntity: true, InReload: true }
            || !_controller.TryGetPreferredGun(slot, requireLoadedAmmo: true, out var targetWeapon)
            || !_controller.SwitchBotWeapon(slot, 9001))
        {
            return;
        }

        _reloadInterruptCooldown[slot] = now + ReloadInterruptCooldown;
        Interlocked.Increment(ref _reloadInterrupts);
        _modSharp.InvokeFrameAction(
            () =>
            {
                if (_active)
                {
                    _controller.SwitchBotWeapon(slot, targetWeapon);
                }
            });
    }

    private void ReequipGunsForActiveBots()
    {
        var players = _clients.GetGameClients(inGame: true)
            .Where(client => client is { IsValid: true, IsHltv: false })
            .Select(client => (Client: client, Controller: client.GetPlayerController()))
            .Where(player => player.Controller?.Team is CStrikeTeam.CT or CStrikeTeam.TE)
            .ToArray();

        foreach (var player in players)
        {
            var controller = player.Controller!;
            if (!TryGetManagedBot(player.Client, out var slot, out var pawn, out _)
                || pawn is not { IsAlive: true }
                || IsDefusing(pawn)
                || pawn.GetActiveWeapon() is not { IsValidEntity: true, Slot: GearSlot.Knife }
                || !players.Any(enemy =>
                    enemy.Controller!.Team != controller.Team
                    && enemy.Controller.GetPlayerPawn() is { IsAlive: true })
                || !_controller.TryGetPreferredGun(
                    slot,
                    requireLoadedAmmo: false,
                    out var targetWeapon)
                || !_controller.SwitchBotWeapon(slot, targetWeapon))
            {
                continue;
            }

            Interlocked.Increment(ref _gunReequips);
        }
    }

    private void HandlePlayerHurt(IEventPlayerHurt gameEvent)
    {
        if (!TryGetManagedBot(
                gameEvent.VictimController,
                gameEvent.VictimPawn,
                out _,
                out _,
                out _))
        {
            return;
        }

        var now = _modSharp.GetGlobals().CurTime;
        if (now < _hurtSmokeUntil)
        {
            return;
        }

        _hurtSmokeUntil = now + 1f;
        SetSmokeLength(expanded: true);
    }

    private void HandlePlayerDeath(IEventPlayerDeath gameEvent)
    {
        if (gameEvent.VictimController is not { Team: CStrikeTeam.CT or CStrikeTeam.TE } victim
            || gameEvent.KillerController is null
            || gameEvent.KillerController == victim
            || !TryGetManagedBot(
                gameEvent.KillerController,
                gameEvent.KillerPawn,
                out var killerSlot,
                out _,
                out _)
            || _random.NextDouble() >= 0.10)
        {
            return;
        }

        var victimSlot = victim.PlayerSlot.AsPrimitive();
        var victimTeamSurvives = _clients.GetGameClients(inGame: true)
            .Where(client => client is { IsValid: true, IsHltv: false })
            .Any(client =>
                client.Slot.AsPrimitive() != victimSlot
                && client.GetPlayerController() is { } teammate
                && teammate.Team == victim.Team
                && teammate.GetPlayerPawn() is { IsAlive: true });
        if (victimTeamSurvives)
        {
            QueueInspect(killerSlot);
        }
    }

    private void HandleStuckAndIdle(
        int slot,
        IPlayerPawn pawn,
        nint botPointer,
        bool attacking,
        float now)
    {
        var velocity = pawn.GetAbsVelocity();
        var speed = MathF.Sqrt((velocity.X * velocity.X) + (velocity.Y * velocity.Y));
        var position = pawn.GetAbsOrigin();
        if (ReadBool(botPointer, _offsets.IsStuck))
        {
            WriteBool(botPointer, _offsets.IsRunning, true);
            WriteFloat(botPointer, _offsets.JumpTimestamp, 0f);
            WriteTimer(botPointer, _offsets.StuckJumpTimer, 0f, now, 1f);
            _idleTracking[slot] = false;

            if (!_stuckTracking[slot])
            {
                _stuckTracking[slot] = true;
                _stuckStartTime[slot] = now;
                _stuckStartPosition[slot] = position;
                _stuckJumpDone[slot] = false;
                _stuckMaximumSpeed[slot] = 0f;
            }

            _stuckMaximumSpeed[slot] = MathF.Max(_stuckMaximumSpeed[slot], speed);
            var start = _stuckStartPosition[slot];
            var dx = position.X - start.X;
            var dy = position.Y - start.Y;
            var displacement = MathF.Sqrt((dx * dx) + (dy * dy));
            if (BotStatePolicy.ShouldRecoverStuck(
                    now - _stuckStartTime[slot],
                    _stuckMaximumSpeed[slot],
                    displacement)
                && !_stuckJumpDone[slot])
            {
                WriteBool(botPointer, _offsets.IsCrouching, false);
                _stuckJumpDone[slot] = true;
                var side = _stuckJumpCount[slot]++ % 2 == 0 ? 1f : -1f;
                var backwardYaw = (pawn.GetEyeAngles().Y * MathF.PI / 180f)
                    + MathF.PI
                    + (30f * MathF.PI / 180f * side);
                velocity.X = MathF.Cos(backwardYaw) * 100f;
                velocity.Y = MathF.Sin(backwardYaw) * 100f;
                pawn.SetAbsVelocity(velocity);
                WriteTimer(botPointer, _offsets.RepathTimer, 0f, now, 1f);
                ResetLookAround(botPointer);
                _stuckStartTime[slot] = now;
                _stuckStartPosition[slot] = position;
                _stuckMaximumSpeed[slot] = 0f;
                Interlocked.Increment(ref _stuckRecoveries);
            }

            return;
        }

        _stuckTracking[slot] = false;
        _stuckJumpDone[slot] = false;
        _stuckMaximumSpeed[slot] = 0f;
        if (speed < 5f)
        {
            if (!_idleTracking[slot])
            {
                _idleTracking[slot] = true;
                _idleStartTime[slot] = now;
            }

            if (now - _idleStartTime[slot] >= 5f
                && now - _lastRepathTime[slot] >= 5f
                && !attacking
                && !IsDefusing(pawn))
            {
                WriteBool(botPointer, _offsets.IsCrouching, false);
                _lastRepathTime[slot] = now;
                WriteTimer(botPointer, _offsets.RepathTimer, 0f, now, 1f);
                ResetLookAround(botPointer);
                Interlocked.Increment(ref _idleRepaths);
            }
        }
        else
        {
            _idleTracking[slot] = false;
        }

        if (string.Equals(
                _modSharp.GetMapName(),
                "de_inferno",
                StringComparison.OrdinalIgnoreCase))
        {
            var dx = position.X - 285f;
            var dy = position.Y - 450f;
            if (MathF.Sqrt((dx * dx) + (dy * dy)) < 50f)
            {
                WriteTimer(botPointer, _offsets.RepathTimer, 0f, now, 1f);
            }
        }
    }

    private void HandleWeaponFire(IEventWeaponFired gameEvent)
    {
        if (!TryGetManagedBot(gameEvent.Controller, gameEvent.Pawn, out var slot, out var pawn, out var botPointer))
        {
            return;
        }

        _hasFiredThisAttack[slot] = true;
        var weaponIndex = pawn.GetActiveWeapon()?.ItemDefinitionIndex ?? 0;
        if (!_cachedInAir[slot] && !_cachedNearLadder[slot])
        {
            var velocity = pawn.GetAbsVelocity();
            var speed = MathF.Sqrt((velocity.X * velocity.X) + (velocity.Y * velocity.Y));
            switch (BotStatePolicy.GetFireMovement(weaponIndex))
            {
                case BotFireMovement.CapAt70 when speed > 70f:
                    var scale = 70f / speed;
                    velocity.X *= scale;
                    velocity.Y *= scale;
                    pawn.SetAbsVelocity(velocity);
                    Interlocked.Increment(ref _weaponFireAdjustments);
                    break;
                case BotFireMovement.Stop when speed > 0f:
                    velocity.X = 0f;
                    velocity.Y = 0f;
                    pawn.SetAbsVelocity(velocity);
                    Interlocked.Increment(ref _weaponFireAdjustments);
                    break;
            }
        }

        if (IsDefusing(pawn) || !ReadBool(botPointer, _offsets.IsAttacking))
        {
            return;
        }

        WriteBool(
            botPointer,
            _offsets.IsCrouching,
            _random.NextDouble() < BotStatePolicy.GetCombatCrouchChance(weaponIndex));
        WriteTimer(botPointer, _offsets.SneakTimer, 0f, 0f, 1f);
    }

    private void HandleBombPlanted()
    {
        var now = _modSharp.GetGlobals().CurTime;
        foreach (var client in _clients.GetGameClients(inGame: true))
        {
            if (!TryGetManagedBot(client, out _, out _, out var botPointer))
            {
                continue;
            }

            WriteTimer(botPointer, _offsets.HurryTimer, 40f, now + 40f, 1f);
            WriteBool(botPointer, _offsets.IsRunning, true);
        }
    }

    private void HandleBombBeginDefuse(IGameEvent gameEvent)
    {
        _bombDefuseActive = true;
        _defuseSmokeCycleStartedAt = _modSharp.GetGlobals().CurTime;
        var controller = gameEvent.GetPlayerController("userid");
        var pawn = gameEvent.GetPlayerPawn("userid");
        if (!TryGetManagedBot(controller, pawn, out _, out var botPawn, out var botPointer))
        {
            return;
        }

        ResetLookAround(botPointer);
        var hasLivingEnemies = _clients.GetGameClients(inGame: true)
            .Select(client => client.GetPlayerController())
            .Any(player => player is { IsValidEntity: true }
                && player.Team is CStrikeTeam.CT or CStrikeTeam.TE
                && player.Team != controller!.Team
                && player.GetPlayerPawn() is { IsAlive: true });
        if (!hasLivingEnemies)
        {
            return;
        }

        var fakeChance = botPawn.GetItemService()?.HasDefuser == true ? 0.10 : 0.66;
        if (_random.NextDouble() >= fakeChance)
        {
            return;
        }

        var yaw = botPawn.GetEyeAngles().Y * MathF.PI / 180f;
        var side = _random.NextDouble() < 0.5 ? 1f : -1f;
        var velocity = botPawn.GetAbsVelocity();
        velocity.X += -MathF.Sin(yaw) * side * 150f;
        velocity.Y += MathF.Cos(yaw) * side * 150f;
        velocity.Z += 255f;
        botPawn.SetAbsVelocity(velocity);
        ResetLookAround(botPointer);
    }

    private void HandleDoorEvent(IGameEvent gameEvent)
    {
        var controller = gameEvent.GetPlayerController("userid");
        if (!TryGetManagedBot(controller, gameEvent.GetPlayerPawn("userid"), out var slot, out _, out _))
        {
            return;
        }

        _doorCooldownEnd[slot] = _modSharp.GetGlobals().CurTime + 1f;
    }

    private bool TryGetManagedBot(
        IGameClient client,
        out int slot,
        out IPlayerPawn pawn,
        out nint botPointer)
    {
        var controller = client.GetPlayerController();
        return TryGetManagedBot(
            controller,
            controller?.GetPlayerPawn(),
            out slot,
            out pawn,
            out botPointer);
    }

    private bool TryGetManagedBot(
        IPlayerController? controller,
        IPlayerPawn? pawn,
        out int slot,
        out IPlayerPawn botPawn,
        out nint botPointer)
    {
        slot = controller?.PlayerSlot.AsPrimitive() ?? -1;
        botPawn = null!;
        botPointer = 0;
        if (controller is not { IsValidEntity: true }
            || pawn is not { IsValidEntity: true }
            || slot is < 0 or >= 64
            || !BotIdentityRegistry.IsBot(controller.IsFakeClient, slot)
            || IsTakenOver(controller)
            || !TryGetBotPointer(pawn, out botPointer))
        {
            return false;
        }

        botPawn = pawn;
        return true;
    }

    private unsafe bool TryGetBotPointer(IPlayerPawn pawn, out nint botPointer)
    {
        botPointer = *(nint*)(pawn.GetAbsPtr() + _offsets.PawnBot);
        return botPointer != 0;
    }

    private unsafe bool IsTakenOver(IPlayerController controller)
        => ReadBool(controller.GetAbsPtr(), _offsets.HasBeenControlledByPlayer);

    private void EnsureSnapshot(int slot, int userId, nint botPointer)
    {
        if (_snapshots[slot] is { } snapshot
            && snapshot.UserId == userId
            && snapshot.BotPointer == botPointer)
        {
            return;
        }

        _snapshots[slot] = CaptureSnapshot(userId, botPointer);
        ResetSlotState(slot);
    }

    private BotBrainSnapshot CaptureSnapshot(int userId, nint botPointer)
        => new(
            userId,
            botPointer,
            ReadFloat(botPointer, _offsets.SafeTime),
            ReadBool(botPointer, _offsets.HasVisitedEnemySpawn),
            ReadBool(botPointer, _offsets.IsSleeping),
            ReadBool(botPointer, _offsets.AllowActive),
            ReadBool(botPointer, _offsets.IsRapidFiring),
            ReadFloat(botPointer, _offsets.PeripheralTimestamp),
            ReadFloat(botPointer, _offsets.FireWeaponTimestamp),
            ReadTimer(botPointer, _offsets.AlertTimer),
            ReadTimer(botPointer, _offsets.IgnoreEnemiesTimer),
            ReadTimer(botPointer, _offsets.PanicTimer),
            ReadTimer(botPointer, _offsets.SurpriseTimer),
            ReadBool(botPointer, _offsets.IsEnemySniperVisible),
            ReadTimer(botPointer, _offsets.SawEnemySniperTimer),
            ReadBool(botPointer, _offsets.IsWaitingBehindFriend),
            ReadTimer(botPointer, _offsets.PoliteTimer),
            ReadBool(botPointer, _offsets.EyeAnglesUnderPathFinderControl),
            ReadFloat(botPointer, _offsets.InhibitLookAroundTimestamp),
            ReadBool(botPointer, _offsets.IsAttacking),
            ReadBool(botPointer, _offsets.IsCrouching),
            ReadBool(botPointer, _offsets.IsRunning),
            ReadFloat(botPointer, _offsets.JumpTimestamp),
            ReadTimer(botPointer, _offsets.StuckJumpTimer),
            ReadTimer(botPointer, _offsets.RepathTimer),
            ReadTimer(botPointer, _offsets.SneakTimer),
            ReadTimer(botPointer, _offsets.HurryTimer),
            ReadInt(botPointer, _offsets.CheckedHidingSpotCount),
            ReadFloat(botPointer, _offsets.LookAroundStateTimestamp));

    private void RestoreAllSnapshots()
    {
        if (!_offsetsResolved)
        {
            Array.Clear(_snapshots);
            return;
        }

        for (var slot = 0; slot < _snapshots.Length; slot++)
        {
            if (_snapshots[slot] is not { } snapshot)
            {
                continue;
            }

            try
            {
                if (_clients.GetGameClient(new PlayerSlot((byte)slot)) is not { IsValid: true } client
                    || client.UserId.AsPrimitive() != snapshot.UserId
                    || client.GetPlayerController()?.GetPlayerPawn() is not { IsValidEntity: true } pawn
                    || !TryGetBotPointer(pawn, out var currentPointer)
                    || currentPointer != snapshot.BotPointer)
                {
                    continue;
                }

                RestoreSnapshot(snapshot);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to restore BotState snapshot for slot {Slot}.", slot);
            }
        }

        Array.Clear(_snapshots);
    }

    private void RestoreSnapshot(BotBrainSnapshot snapshot)
    {
        var pointer = snapshot.BotPointer;
        WriteFloat(pointer, _offsets.SafeTime, snapshot.SafeTime);
        WriteBool(pointer, _offsets.HasVisitedEnemySpawn, snapshot.HasVisitedEnemySpawn);
        WriteBool(pointer, _offsets.IsSleeping, snapshot.IsSleeping);
        WriteBool(pointer, _offsets.AllowActive, snapshot.AllowActive);
        WriteBool(pointer, _offsets.IsRapidFiring, snapshot.IsRapidFiring);
        WriteFloat(pointer, _offsets.PeripheralTimestamp, snapshot.PeripheralTimestamp);
        WriteFloat(pointer, _offsets.FireWeaponTimestamp, snapshot.FireWeaponTimestamp);
        WriteTimer(pointer, _offsets.AlertTimer, snapshot.AlertTimer);
        WriteTimer(pointer, _offsets.IgnoreEnemiesTimer, snapshot.IgnoreEnemiesTimer);
        WriteTimer(pointer, _offsets.PanicTimer, snapshot.PanicTimer);
        WriteTimer(pointer, _offsets.SurpriseTimer, snapshot.SurpriseTimer);
        WriteBool(pointer, _offsets.IsEnemySniperVisible, snapshot.IsEnemySniperVisible);
        WriteTimer(pointer, _offsets.SawEnemySniperTimer, snapshot.SawEnemySniperTimer);
        WriteBool(pointer, _offsets.IsWaitingBehindFriend, snapshot.IsWaitingBehindFriend);
        WriteTimer(pointer, _offsets.PoliteTimer, snapshot.PoliteTimer);
        WriteBool(
            pointer,
            _offsets.EyeAnglesUnderPathFinderControl,
            snapshot.EyeAnglesUnderPathFinderControl);
        WriteFloat(
            pointer,
            _offsets.InhibitLookAroundTimestamp,
            snapshot.InhibitLookAroundTimestamp);
        WriteBool(pointer, _offsets.IsAttacking, snapshot.IsAttacking);
        WriteBool(pointer, _offsets.IsCrouching, snapshot.IsCrouching);
        WriteBool(pointer, _offsets.IsRunning, snapshot.IsRunning);
        WriteFloat(pointer, _offsets.JumpTimestamp, snapshot.JumpTimestamp);
        WriteTimer(pointer, _offsets.StuckJumpTimer, snapshot.StuckJumpTimer);
        WriteTimer(pointer, _offsets.RepathTimer, snapshot.RepathTimer);
        WriteTimer(pointer, _offsets.SneakTimer, snapshot.SneakTimer);
        WriteTimer(pointer, _offsets.HurryTimer, snapshot.HurryTimer);
        WriteInt(pointer, _offsets.CheckedHidingSpotCount, snapshot.CheckedHidingSpotCount);
        WriteFloat(pointer, _offsets.LookAroundStateTimestamp, snapshot.LookAroundStateTimestamp);
    }

    private void ResetLookAround(nint botPointer)
    {
        WriteFloat(botPointer, _offsets.InhibitLookAroundTimestamp, 0f);
        WriteInt(botPointer, _offsets.CheckedHidingSpotCount, 0);
        WriteFloat(botPointer, _offsets.LookAroundStateTimestamp, 0f);
    }

    private unsafe bool IsDefusing(IPlayerPawn pawn)
        => ReadBool(pawn.GetAbsPtr(), _offsets.IsDefusing);

    private void ResolveOffsets()
    {
        _offsets = new BotStateOffsets
        {
            PawnBot = Offset("CCSPlayerPawn", "m_pBot"),
            LadderNormal = Offset(
                "CCSPlayer_MovementServices",
                "m_vecLadderNormal"),
            HasBeenControlledByPlayer = Offset(
                "CCSPlayerController",
                "m_bHasBeenControlledByPlayerThisRound"),
            IsDefusing = Offset("CCSPlayerPawn", "m_bIsDefusing"),
            SafeTime = Offset("CCSBot", "m_safeTime"),
            HasVisitedEnemySpawn = Offset("CCSBot", "m_hasVisitedEnemySpawn"),
            IsSleeping = Offset("CCSBot", "m_bIsSleeping"),
            AllowActive = Offset("CCSBot", "m_bAllowActive"),
            IsRapidFiring = Offset("CCSBot", "m_isRapidFiring"),
            PeripheralTimestamp = Offset("CCSBot", "m_peripheralTimestamp"),
            FireWeaponTimestamp = Offset("CCSBot", "m_fireWeaponTimestamp"),
            AlertTimer = Offset("CCSBot", "m_alertTimer"),
            IgnoreEnemiesTimer = Offset("CCSBot", "m_ignoreEnemiesTimer"),
            PanicTimer = Offset("CCSBot", "m_panicTimer"),
            SurpriseTimer = Offset("CCSBot", "m_surpriseTimer"),
            IsEnemySniperVisible = Offset("CCSBot", "m_isEnemySniperVisible"),
            SawEnemySniperTimer = Offset("CCSBot", "m_sawEnemySniperTimer"),
            IsWaitingBehindFriend = Offset("CCSBot", "m_isWaitingBehindFriend"),
            PoliteTimer = Offset("CCSBot", "m_politeTimer"),
            EyeAnglesUnderPathFinderControl = Offset(
                "CCSBot",
                "m_bEyeAnglesUnderPathFinderControl"),
            InhibitLookAroundTimestamp = Offset("CCSBot", "m_inhibitLookAroundTimestamp"),
            IsAimingAtEnemy = Offset("CCSBot", "m_isAimingAtEnemy"),
            IsAttacking = Offset("CCSBot", "m_isAttacking"),
            IsEnemyVisible = Offset("CCSBot", "m_isEnemyVisible"),
            IsCrouching = Offset("CBot", "m_isCrouching"),
            IsStuck = Offset("CCSBot", "m_isStuck"),
            IsRunning = Offset("CBot", "m_isRunning"),
            JumpTimestamp = Offset("CBot", "m_jumpTimestamp"),
            StuckJumpTimer = Offset("CCSBot", "m_stuckJumpTimer"),
            RepathTimer = Offset("CCSBot", "m_repathTimer"),
            SneakTimer = Offset("CCSBot", "m_sneakTimer"),
            HurryTimer = Offset("CCSBot", "m_hurryTimer"),
            CheckedHidingSpotCount = Offset("CCSBot", "m_checkedHidingSpotCount"),
            LookAroundStateTimestamp = Offset("CCSBot", "m_lookAroundStateTimestamp"),
            TimerDuration = Offset("CountdownTimer", "m_duration"),
            TimerTimestamp = Offset("CountdownTimer", "m_timestamp"),
            TimerTimescale = Offset("CountdownTimer", "m_timescale"),
        };
        _offsetsResolved = true;
    }

    private int Offset(string className, string fieldName)
    {
        var offset = _schema.GetNetVarOffset(className, fieldName);
        if (offset <= 0)
        {
            throw new InvalidDataException(
                $"Schema field {className}::{fieldName} resolved to invalid offset {offset}.");
        }

        return offset;
    }

    private void ResetRoundState()
    {
        _hurtSmokeUntil = 0f;
        _bombDefuseActive = false;
        _defuseSmokeCycleStartedAt = 0f;
        _nextGunReequipAt = 0f;
        SetSmokeLength(expanded: false);
        for (var slot = 0; slot < 64; slot++)
        {
            ResetSlotState(slot);
        }
    }

    private void ResetTransientState()
    {
        ResetRoundState();
        Array.Clear(_snapshots);
    }

    private void ResetSlotState(int slot)
    {
        _hasPreviousAttack[slot] = false;
        _previousAttack[slot] = false;
        _hasFiredThisAttack[slot] = false;
        _lastLateralDirection[slot] = 0;
        _hasPreviousInAir[slot] = false;
        _previousInAir[slot] = false;
        _lastForwardDirection[slot] = 0;
        _ladderExitTime[slot] = 0f;
        _doorCooldownEnd[slot] = 0f;
        _cachedInAir[slot] = false;
        _cachedNearLadder[slot] = false;
        _stuckTracking[slot] = false;
        _stuckStartTime[slot] = 0f;
        _stuckStartPosition[slot] = default;
        _stuckJumpDone[slot] = false;
        _stuckJumpCount[slot] = 0;
        _stuckMaximumSpeed[slot] = 0f;
        _idleTracking[slot] = false;
        _idleStartTime[slot] = 0f;
        _lastRepathTime[slot] = -999f;
        _reloadInterruptCooldown[slot] = 0f;
    }

    private unsafe TimerState ReadTimer(nint pointer, int timerOffset)
        => new(
            ReadFloat(pointer, timerOffset + _offsets.TimerDuration),
            ReadFloat(pointer, timerOffset + _offsets.TimerTimestamp),
            ReadFloat(pointer, timerOffset + _offsets.TimerTimescale));

    private void WriteTimer(
        nint pointer,
        int timerOffset,
        float duration,
        float timestamp,
        float timescale)
        => WriteTimer(pointer, timerOffset, new TimerState(duration, timestamp, timescale));

    private void WriteTimer(nint pointer, int timerOffset, TimerState timer)
    {
        WriteFloat(pointer, timerOffset + _offsets.TimerDuration, timer.Duration);
        WriteFloat(pointer, timerOffset + _offsets.TimerTimestamp, timer.Timestamp);
        WriteFloat(pointer, timerOffset + _offsets.TimerTimescale, timer.Timescale);
    }

    private static unsafe bool ReadBool(nint pointer, int offset)
        => *(byte*)(pointer + offset) != 0;

    private static unsafe void WriteBool(nint pointer, int offset, bool value)
        => *(byte*)(pointer + offset) = value ? (byte)1 : (byte)0;

    private static unsafe float ReadFloat(nint pointer, int offset)
        => *(float*)(pointer + offset);

    private static unsafe void WriteFloat(nint pointer, int offset, float value)
        => *(float*)(pointer + offset) = value;

    private static unsafe int ReadInt(nint pointer, int offset)
        => *(int*)(pointer + offset);

    private static unsafe void WriteInt(nint pointer, int offset, int value)
        => *(int*)(pointer + offset) = value;

    private readonly record struct TimerState(float Duration, float Timestamp, float Timescale);

    private sealed record BotBrainSnapshot(
        int UserId,
        nint BotPointer,
        float SafeTime,
        bool HasVisitedEnemySpawn,
        bool IsSleeping,
        bool AllowActive,
        bool IsRapidFiring,
        float PeripheralTimestamp,
        float FireWeaponTimestamp,
        TimerState AlertTimer,
        TimerState IgnoreEnemiesTimer,
        TimerState PanicTimer,
        TimerState SurpriseTimer,
        bool IsEnemySniperVisible,
        TimerState SawEnemySniperTimer,
        bool IsWaitingBehindFriend,
        TimerState PoliteTimer,
        bool EyeAnglesUnderPathFinderControl,
        float InhibitLookAroundTimestamp,
        bool IsAttacking,
        bool IsCrouching,
        bool IsRunning,
        float JumpTimestamp,
        TimerState StuckJumpTimer,
        TimerState RepathTimer,
        TimerState SneakTimer,
        TimerState HurryTimer,
        int CheckedHidingSpotCount,
        float LookAroundStateTimestamp);

    private struct BotStateOffsets
    {
        public int PawnBot;
        public int LadderNormal;
        public int HasBeenControlledByPlayer;
        public int IsDefusing;
        public int SafeTime;
        public int HasVisitedEnemySpawn;
        public int IsSleeping;
        public int AllowActive;
        public int IsRapidFiring;
        public int PeripheralTimestamp;
        public int FireWeaponTimestamp;
        public int AlertTimer;
        public int IgnoreEnemiesTimer;
        public int PanicTimer;
        public int SurpriseTimer;
        public int IsEnemySniperVisible;
        public int SawEnemySniperTimer;
        public int IsWaitingBehindFriend;
        public int PoliteTimer;
        public int EyeAnglesUnderPathFinderControl;
        public int InhibitLookAroundTimestamp;
        public int IsAimingAtEnemy;
        public int IsAttacking;
        public int IsEnemyVisible;
        public int IsCrouching;
        public int IsStuck;
        public int IsRunning;
        public int JumpTimestamp;
        public int StuckJumpTimer;
        public int RepathTimer;
        public int SneakTimer;
        public int HurryTimer;
        public int CheckedHidingSpotCount;
        public int LookAroundStateTimestamp;
        public int TimerDuration;
        public int TimerTimestamp;
        public int TimerTimescale;
    }
}
