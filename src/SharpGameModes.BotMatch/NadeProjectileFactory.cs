using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Managers;
using Sharp.Shared.Types;

namespace SharpGameModes.BotMatch;

/// <summary>
/// Pure ModSharp bridge to CS2's armed grenade constructors. Creating a raw
/// smoke/HE/molotov entity is insufficient because the engine constructor
/// initializes fuse and detonation state.
/// </summary>
internal sealed class NadeProjectileFactory
{
    private const string SmokeLinux =
        "55 4C 89 C1 48 89 E5 41 57 49 89 FF 41 56 45 89 CE 41 55";
    private const string SmokeWindows =
        "48 8B C4 48 89 58 ? 48 89 68 ? 48 89 70 ? 57 41 56 41 57 48 81 EC ? ? ? ? 48 8B B4 24 ? ? ? ? 4D 8B F8";
    private const string HeLinux =
        "55 4C 89 C1 48 89 E5 41 57 49 89 FF 41 56 49 89 D6 48 89 F2 48 89 FE 41 55";
    private const string HeWindows =
        "48 89 ? 24 ? 48 89 ? 24 ? 48 89 ? 24 ? 57 48 83 EC ? 48 8B ? 24 ? 49 8B F8 4C 8B C2 0F 29 ? 24 ? 48 8B D1 48 8B D9 48 8D 0D ? ? ? ? 4C 8B CD E8 ? ? ? ? F3 0F 10 0D ? ? ? ? 48 8B C8 48 8B F0 E8 ? ? ? ? 48 8B D7 48 8B CE";
    private const string MolotovLinux =
        "55 48 8D 05 ? ? ? ? 48 89 E5 41 57 41 56 41 55 41 54 49 89 FC 53 48 81 EC ? ? ? ? 4C 8D 35";
    private const string MolotovWindows =
        "48 8B C4 48 89 58 10 4C 89 40 18 48 89 48 08";

    private readonly ISharedSystem _shared;
    private readonly IEntityManager _entities;
    private readonly ISchemaManager _schema;
    private readonly ILogger _logger;
    private nint _smokeCreate;
    private nint _heCreate;
    private nint _molotovCreate;
    private Offsets _offsets;
    private bool _active;
    private long _flashSpawns;
    private long _smokeSpawns;
    private long _heSpawns;
    private long _molotovSpawns;
    private long _errors;

    public NadeProjectileFactory(ISharedSystem shared, ILogger logger)
    {
        _shared = shared;
        _entities = shared.GetEntityManager();
        _schema = shared.GetSchemaManager();
        _logger = logger;
    }

    public bool Activate()
    {
        if (_active)
        {
            return true;
        }

        try
        {
            var signatureIsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var server = _shared.GetLibraryModuleManager().Server;
            _smokeCreate = server.FindPatternExactly(
                signatureIsWindows ? SmokeWindows : SmokeLinux);
            _heCreate = server.FindPatternExactly(
                signatureIsWindows ? HeWindows : HeLinux);
            _molotovCreate = server.FindPatternExactly(
                signatureIsWindows ? MolotovWindows : MolotovLinux);
            if (_smokeCreate == nint.Zero
                || _heCreate == nint.Zero
                || _molotovCreate == nint.Zero)
            {
                throw new InvalidDataException(
                    $"Grenade factories unresolved: smoke=0x{_smokeCreate:X}, HE=0x{_heCreate:X}, molotov=0x{_molotovCreate:X}.");
            }

            _offsets = new Offsets
            {
                Team = Offset("CBaseEntity", "m_iTeamNum"),
                OriginalThrower = Offset(
                    "CBaseCSGrenadeProjectile",
                    "m_hOriginalThrower"),
                Elasticity = Offset("CBaseGrenade", "m_flElasticity"),
                InitialPosition = Offset(
                    "CBaseCSGrenadeProjectile",
                    "m_vInitialPosition"),
                InitialVelocity = Offset(
                    "CBaseCSGrenadeProjectile",
                    "m_vInitialVelocity"),
            };
            _active = true;
            _logger.LogInformation(
                "Pure ModSharp NadeSystem factories resolved: smoke=0x{Smoke:X}, HE=0x{HE:X}, molotov=0x{Molotov:X}.",
                _smokeCreate,
                _heCreate,
                _molotovCreate);
            return true;
        }
        catch (Exception exception)
        {
            _smokeCreate = nint.Zero;
            _heCreate = nint.Zero;
            _molotovCreate = nint.Zero;
            _active = false;
            _logger.LogError(
                exception,
                "Failed to resolve pure ModSharp NadeSystem factories.");
            return false;
        }
    }

    public void Deactivate()
    {
        if (!_active)
        {
            return;
        }

        _active = false;
        _smokeCreate = nint.Zero;
        _heCreate = nint.Zero;
        _molotovCreate = nint.Zero;
        _logger.LogInformation(
            "Pure ModSharp NadeSystem factories disabled. Spawned flash {Flash}, smoke {Smoke}, HE {HE}, molotov {Molotov}; errors {Errors}.",
            Interlocked.Read(ref _flashSpawns),
            Interlocked.Read(ref _smokeSpawns),
            Interlocked.Read(ref _heSpawns),
            Interlocked.Read(ref _molotovSpawns),
            Interlocked.Read(ref _errors));
    }

    public unsafe IBaseGrenadeProjectile? Spawn(
        IPlayerPawn pawn,
        CStrikeTeam team,
        string grenadeType,
        Vector origin,
        Vector velocity)
    {
        if (!_active || !pawn.IsValidEntity)
        {
            return null;
        }

        try
        {
            var normalized = NadeSystemPolicy.NormalizeType(grenadeType);
            IBaseGrenadeProjectile? projectile;
            switch (normalized)
            {
                case "flash":
                case "decoy":
                    projectile = SpawnFlash(pawn, team, origin, velocity);
                    Interlocked.Increment(ref _flashSpawns);
                    return projectile;
                case "smoke":
                    projectile = Wrap(
                        ((delegate* unmanaged<
                            Vector*,
                            Vector*,
                            Vector*,
                            Vector*,
                            nint,
                            int,
                            int,
                            nint>)_smokeCreate)(
                            &origin,
                            &origin,
                            &velocity,
                            &velocity,
                            pawn.GetAbsPtr(),
                            45,
                            (int)team));
                    Configure(projectile, pawn, team);
                    Interlocked.Increment(ref _smokeSpawns);
                    return projectile;
                case "he":
                    projectile = Wrap(
                        ((delegate* unmanaged<
                            Vector*,
                            Vector*,
                            Vector*,
                            Vector*,
                            nint,
                            int,
                            nint>)_heCreate)(
                            &origin,
                            &origin,
                            &velocity,
                            &velocity,
                            pawn.GetAbsPtr(),
                            44));
                    Configure(projectile, pawn, team);
                    Interlocked.Increment(ref _heSpawns);
                    return projectile;
                case "molotov":
                    var itemDefinition = team == CStrikeTeam.CT ? 48 : 46;
                    projectile = Wrap(
                        ((delegate* unmanaged<
                            Vector*,
                            Vector*,
                            Vector*,
                            Vector*,
                            nint,
                            int,
                            nint>)_molotovCreate)(
                            &origin,
                            &origin,
                            &velocity,
                            &velocity,
                            pawn.GetAbsPtr(),
                            itemDefinition));
                    Configure(projectile, pawn, team);
                    Interlocked.Increment(ref _molotovSpawns);
                    return projectile;
                default:
                    return null;
            }
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _errors);
            _logger.LogWarning(
                exception,
                "NadeSystem failed to spawn {GrenadeType}.",
                grenadeType);
            return null;
        }
    }

    private IBaseGrenadeProjectile? SpawnFlash(
        IPlayerPawn pawn,
        CStrikeTeam team,
        Vector origin,
        Vector velocity)
    {
        var flash = _entities.CreateEntityByName<IBaseGrenadeProjectile>(
            "flashbang_projectile");
        if (flash is null)
        {
            return null;
        }

        Configure(flash, pawn, team);
        WriteVector(flash.GetAbsPtr(), _offsets.InitialPosition, origin);
        WriteVector(flash.GetAbsPtr(), _offsets.InitialVelocity, velocity);
        WriteFloat(flash.GetAbsPtr(), _offsets.Elasticity, 0.33f);
        var angles = AnglesFromVelocity(velocity);
        flash.Teleport(origin, angles, velocity);
        flash.DispatchSpawn();
        flash.Teleport(origin, angles, velocity);
        return flash;
    }

    private void Configure(
        IBaseGrenadeProjectile? projectile,
        IPlayerPawn pawn,
        CStrikeTeam team)
    {
        if (projectile is not { IsValidEntity: true })
        {
            throw new InvalidOperationException("Grenade constructor returned null.");
        }

        WriteByte(projectile.GetAbsPtr(), _offsets.Team, (byte)team);
        projectile.ThrowerEntityHandle = pawn.RefHandle.As<IPlayerPawn>();
        WriteUInt(
            projectile.GetAbsPtr(),
            _offsets.OriginalThrower,
            pawn.RefHandle.GetValue());
        projectile.SetOwner(pawn);
    }

    private IBaseGrenadeProjectile? Wrap(nint pointer)
        => pointer == nint.Zero
            ? null
            : _entities.MakeEntityFromPointer<IBaseGrenadeProjectile>(pointer);

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

    private static Vector AnglesFromVelocity(Vector velocity)
    {
        var yaw = MathF.Atan2(velocity.Y, velocity.X) * (180f / MathF.PI);
        var horizontal = MathF.Sqrt(
            velocity.X * velocity.X + velocity.Y * velocity.Y);
        var pitch = -MathF.Atan2(velocity.Z, horizontal)
            * (180f / MathF.PI);
        return new Vector(pitch, yaw, 0f);
    }

    private static unsafe void WriteByte(nint pointer, int offset, byte value)
        => *(byte*)(pointer + offset) = value;

    private static unsafe void WriteUInt(nint pointer, int offset, uint value)
        => *(uint*)(pointer + offset) = value;

    private static unsafe void WriteFloat(nint pointer, int offset, float value)
        => *(float*)(pointer + offset) = value;

    private static unsafe void WriteVector(
        nint pointer,
        int offset,
        Vector value)
        => *(Vector*)(pointer + offset) = value;

    private struct Offsets
    {
        public int Team;
        public int OriginalThrower;
        public int Elasticity;
        public int InitialPosition;
        public int InitialVelocity;
    }
}
