using Microsoft.Extensions.Logging;
using SharpGameModes.Contracts;
using Sharp.Shared;
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace SharpGameModes.BotMatch;

/// <summary>
/// Tracks flashbang projectiles through ModSharp entity notifications and
/// pre-rolls the upstream BotState avoidance decision the first time a bot has
/// both FOV and world line of sight. This avoids the upstream per-tick global
/// entity scan.
/// </summary>
internal sealed class BotStateFlashRuntime : IEntityListener
{
    private const float FlashFuseSeconds = 1.5f;

    private readonly IModSharp _modSharp;
    private readonly IEntityManager _entities;
    private readonly IPhysicsQueryManager _traces;
    private readonly ISchemaManager _schema;
    private readonly IClientManager _clients;
    private readonly ILogger _logger;
    private readonly Random _random = new();
    private readonly Dictionary<int, TrackedFlash> _flashes = [];
    private readonly Dictionary<FlashKey, FlashDecision> _decisions = [];
    private readonly HashSet<FlashKey> _rolled = [];
    private int _hasBeenControlledByPlayerOffset;
    private int _blindStartTimeOffset;
    private int _blindUntilTimeOffset;
    private bool _listenerInstalled;
    private bool _active;
    private bool _debug;
    private long _rayTraces;
    private long _decisionsMade;
    private long _avoidsApplied;
    private long _errors;

    public BotStateFlashRuntime(ISharedSystem shared, IClientManager clients, ILogger logger)
    {
        _modSharp = shared.GetModSharp();
        _entities = shared.GetEntityManager();
        _traces = shared.GetPhysicsQueryManager();
        _schema = shared.GetSchemaManager();
        _clients = clients;
        _logger = logger;
    }

    public int ListenerVersion => IEntityListener.ApiVersion;
    public int ListenerPriority => 10;
    public bool DebugEnabled => _debug;

    public bool Activate()
    {
        if (_active)
        {
            return true;
        }

        try
        {
            _hasBeenControlledByPlayerOffset = Offset(
                "CCSPlayerController",
                "m_bHasBeenControlledByPlayerThisRound");
            _blindStartTimeOffset = Offset("CCSPlayerPawnBase", "m_blindStartTime");
            _blindUntilTimeOffset = Offset("CCSPlayerPawnBase", "m_blindUntilTime");
            Reset();
            _entities.InstallEntityListener(this);
            _listenerInstalled = true;
            _active = true;
            foreach (var entity in _entities.GetAllEntitiesByClassname("flashbang_projectile"))
            {
                TrackFlash(entity);
            }

            return true;
        }
        catch (Exception exception)
        {
            _active = false;
            RemoveListener();
            Reset();
            _logger.LogError(
                exception,
                "Failed to enable pure ModSharp BotState flash avoidance.");
            return false;
        }
    }

    public void Deactivate()
    {
        _active = false;
        RemoveListener();
        Reset();
        _logger.LogInformation(
            "Pure ModSharp BotState flash avoidance disabled. Traces {Traces}, decisions {Decisions}, avoids {Avoids}, errors {Errors}.",
            Interlocked.Read(ref _rayTraces),
            Interlocked.Read(ref _decisionsMade),
            Interlocked.Read(ref _avoidsApplied),
            Interlocked.Read(ref _errors));
    }

    public void SetDebug(bool enabled)
    {
        _debug = enabled;
        _logger.LogInformation(
            "BotState flash debug changed to {Enabled}.",
            enabled);
    }

    public void HandleGameEvent(IGameEvent gameEvent)
    {
        if (!_active)
        {
            return;
        }

        if (gameEvent.Name == "round_start")
        {
            Reset();
            return;
        }

        if (gameEvent.Name != "player_blind")
        {
            return;
        }

        try
        {
            HandlePlayerBlind(gameEvent);
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _errors);
            _logger.LogWarning(exception, "BotState flash blind handler failed.");
        }
    }

    public void ProcessBot(int slot, IPlayerPawn pawn, float now)
    {
        if (!_active || _flashes.Count == 0)
        {
            return;
        }

        try
        {
            foreach (var (flashIndex, flash) in _flashes)
            {
                if (now > flash.DetonateAt)
                {
                    continue;
                }

                var key = new FlashKey(slot, flashIndex);
                if (_rolled.Contains(key)
                    || _entities.FindEntityByIndex<IBaseEntity>((EntityIndex)flashIndex) is not
                    {
                        IsValidEntity: true,
                    } entity)
                {
                    continue;
                }

                var target = entity.GetAbsOrigin();
                var eye = pawn.GetEyePosition();
                var angles = pawn.GetEyeAngles();
                if (!BotStatePolicy.IsWithinFlashFov(
                        eye.X,
                        eye.Y,
                        eye.Z,
                        angles.X,
                        angles.Y,
                        target.X,
                        target.Y,
                        target.Z))
                {
                    continue;
                }

                Interlocked.Increment(ref _rayTraces);
                var trace = _traces.TraceLineNoPlayers(
                    eye,
                    target,
                    UsefulInteractionLayers.BrushOnly,
                    CollisionGroupType.Default,
                    TraceQueryFlag.Static);
                if (trace.StartInSolid || trace.Fraction < 0.999f)
                {
                    continue;
                }

                _rolled.Add(key);
                var millisecondsLeft = (flash.DetonateAt - now) * 1000f;
                var probability = BotStatePolicy.GetFlashAvoidChance(millisecondsLeft);
                var avoided = _random.NextDouble() <= probability;
                _decisions[key] = new FlashDecision(flash.DetonateAt, avoided);
                Interlocked.Increment(ref _decisionsMade);
                if (_debug)
                {
                    BroadcastDebug(
                        $"[BotState/Flash] bot slot {slot} saw flash {flashIndex} at t-{millisecondsLeft:F0}ms, probability {probability:P0}, decision {(avoided ? "avoid" : "blind")}.");
                }
            }
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _errors);
            if (_debug)
            {
                _logger.LogWarning(exception, "BotState flash processing failed for slot {Slot}.", slot);
            }
        }
    }

    public void Prune(float now)
    {
        if (!_active || _decisions.Count == 0)
        {
            return;
        }

        var expired = _decisions
            .Where(pair => now - pair.Value.DetonateAt > 2f)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in expired)
        {
            _decisions.Remove(key);
            _rolled.Remove(key);
        }
    }

    public void Release(int slot)
    {
        var keys = _rolled.Where(key => key.BotSlot == slot).ToArray();
        foreach (var key in keys)
        {
            _rolled.Remove(key);
            _decisions.Remove(key);
        }
    }

    public void OnEntitySpawned(IBaseEntity entity)
    {
        if (_active)
        {
            TrackFlash(entity);
        }
    }

    public void OnEntityDeleted(IBaseEntity entity)
    {
        if (!_active)
        {
            return;
        }

        var index = entity.Index.AsPrimitive();
        _flashes.Remove(index);
    }

    private unsafe void HandlePlayerBlind(IGameEvent gameEvent)
    {
        var controller = gameEvent.GetPlayerController("userid");
        var pawn = gameEvent.GetPlayerPawn("userid");
        var slot = controller?.PlayerSlot.AsPrimitive() ?? -1;
        if (controller is not { IsValidEntity: true }
            || pawn is not { IsValidEntity: true }
            || slot is < 0 or >= 64
            || !BotIdentityRegistry.IsBot(controller.IsFakeClient, slot)
            || ReadBool(controller.GetAbsPtr(), _hasBeenControlledByPlayerOffset))
        {
            return;
        }

        var now = _modSharp.GetGlobals().CurTime;
        FlashKey? matchedKey = null;
        var matched = default(FlashDecision);
        var bestDelta = float.MaxValue;
        foreach (var (key, decision) in _decisions)
        {
            if (key.BotSlot != slot)
            {
                continue;
            }

            var delta = MathF.Abs(decision.DetonateAt - now);
            if (delta < bestDelta && delta < 0.25f)
            {
                bestDelta = delta;
                matchedKey = key;
                matched = decision;
            }
        }

        if (matchedKey is not { } keyToRemove)
        {
            return;
        }

        _decisions.Remove(keyToRemove);
        _rolled.Remove(keyToRemove);
        if (!matched.Avoided)
        {
            return;
        }

        if (gameEvent.Editable)
        {
            gameEvent.SetFloat("blind_duration", 0f);
        }

        pawn.FlashDuration = 0f;
        pawn.FlashMaxAlpha = 0f;
        WriteFloat(pawn.GetAbsPtr(), _blindStartTimeOffset, 0f);
        WriteFloat(pawn.GetAbsPtr(), _blindUntilTimeOffset, 0f);
        Interlocked.Increment(ref _avoidsApplied);
        if (_debug)
        {
            BroadcastDebug(
                $"[BotState/Flash] removed flash {keyToRemove.FlashIndex} blindness from bot slot {slot}.");
        }
    }

    private void TrackFlash(IBaseEntity entity)
    {
        try
        {
            if (!entity.IsValidEntity
                || !entity.Classname.Equals(
                    "flashbang_projectile",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var index = entity.Index.AsPrimitive();
            var now = _modSharp.GetGlobals().CurTime;
            _flashes[index] = new TrackedFlash(now + FlashFuseSeconds);
            RemoveFlashDecisions(index);
            if (_debug)
            {
                BroadcastDebug($"[BotState/Flash] tracking flash {index}.");
            }
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _errors);
            if (_debug)
            {
                _logger.LogWarning(exception, "Failed to track a flashbang projectile.");
            }
        }
    }

    private void RemoveFlashDecisions(int flashIndex)
    {
        var keys = _rolled.Where(key => key.FlashIndex == flashIndex).ToArray();
        foreach (var key in keys)
        {
            _rolled.Remove(key);
            _decisions.Remove(key);
        }
    }

    private void BroadcastDebug(string message)
    {
        _logger.LogInformation("{Message}", message);
        foreach (var client in _clients.GetGameClients(inGame: true))
        {
            var slot = client.Slot.AsPrimitive();
            if (client is { IsValid: true, IsHltv: false }
                && !BotIdentityRegistry.IsBot(client.IsFakeClient, slot))
            {
                client.Print(HudPrintChannel.Console, message);
            }
        }
    }

    private void RemoveListener()
    {
        if (!_listenerInstalled)
        {
            return;
        }

        _entities.RemoveEntityListener(this);
        _listenerInstalled = false;
    }

    private void Reset()
    {
        _flashes.Clear();
        _decisions.Clear();
        _rolled.Clear();
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

    private static unsafe bool ReadBool(nint pointer, int offset)
        => *(byte*)(pointer + offset) != 0;

    private static unsafe void WriteFloat(nint pointer, int offset, float value)
        => *(float*)(pointer + offset) = value;

    private readonly record struct TrackedFlash(float DetonateAt);
    private readonly record struct FlashKey(int BotSlot, int FlashIndex);
    private readonly record struct FlashDecision(float DetonateAt, bool Avoided);
}
