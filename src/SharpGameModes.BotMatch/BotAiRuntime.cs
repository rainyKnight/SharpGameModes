using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SharpGameModes.Contracts;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameEvents;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;

namespace SharpGameModes.BotMatch;

/// <summary>
/// Pure ModSharp, reversible port of CS2-Bot-Improver BotAI 1.8.7.
/// Every signature and original byte sequence is validated before any server
/// text page is changed, so activation is atomic.
/// </summary>
internal sealed class BotAiRuntime : IDisposable
{
    // CS2 build 14172: CCSBot::m_gameState begins at 0x5100.
    // Upstream previously used 0x5128, which is already inside CSGameState.
    private const int GameStateOffset = 0x5100;
    private const int GameStateRoundOverOffset = 0x08;
    private const int GameStateBombStateOffset = 0x0C;
    private const string WatchApproachCaveCompatibleSignature =
        "F3 0F 11 4D A8 E9 ? ? ? ? CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC";
    private const string LoopNullTargetSignature =
        "0F 2F C8 0F 83 ? ? ? ? F3 0F 10 A5 E8 FE FF FF";
    private const string LowSkillTargetSignature =
        "66 0F 1F 44 00 00 4C 89 E6 48 89 DF E8 ? ? ? ? E9 ? ? ? ?";
    private const string PlantedBombsiteHelperSignature =
        "83 7F 0C 02 75 0A 8B 47 68 C3 66 0F 1F 44 00 00 B8 FF FF FF FF C3";

    private readonly ILibraryModule _server;
    private readonly ISchemaManager _schema;
    private readonly ILogger _logger;
    private readonly List<AppliedPatch> _applied = [];
    private int _pawnBotOffset;
    private int _allowActiveOffset;
    private bool _active;
    private bool _bombPlanted;
    private long _bombStateCorrections;
    private long _errors;

    public BotAiRuntime(
        ISharedSystem shared,
        ILogger logger)
    {
        _server = shared.GetLibraryModuleManager().Server;
        _schema = shared.GetSchemaManager();
        _logger = logger;
    }

    public bool Activate()
    {
        if (_active)
        {
            return true;
        }

        var definitions = CurrentDefinitions;
        var planned = new List<PlannedPatch>(definitions.Count);
        try
        {
            _pawnBotOffset = ResolveOffset(
                "CCSPlayerPawn",
                "m_pBot");
            _allowActiveOffset = ResolveOffset(
                "CCSBot",
                "m_bAllowActive");

            var validationFailures = new List<string>();
            foreach (var (name, definition) in definitions)
            {
                try
                {
                    if (!BotAiPatchEncoding.TryParsePatch(
                            definition.patch,
                            out var patchBytes))
                    {
                        throw new InvalidDataException(
                            $"BotAI patch '{name}' has invalid replacement bytes.");
                    }

                    var (address, original) = ResolvePatchTarget(
                        name,
                        GetCompatibleSignature(
                            name,
                            definition.signature),
                        GetCompatibleExpectedOriginal(
                            name,
                            definition.expectedOriginal),
                        definition.patchOffset,
                        patchBytes.Length);

                    planned.Add(
                        new PlannedPatch(
                            name,
                            address,
                            original,
                            patchBytes));
                }
                catch (Exception exception) when (
                    exception is InvalidDataException
                        or ArgumentException
                        or OverflowException)
                {
                    validationFailures.Add(exception.Message);
                }
            }

            if (validationFailures.Count > 0)
            {
                throw new InvalidDataException(
                    $"BotAI atomic preflight rejected {validationFailures.Count}/{definitions.Count} patch(es):{Environment.NewLine}{string.Join(Environment.NewLine, validationFailures)}");
            }

            RelocateLinuxControlFlow(planned);

            foreach (var patch in planned)
            {
                if (!WriteBytes(patch.Address, patch.PatchBytes))
                {
                    throw new InvalidOperationException(
                        $"BotAI patch '{patch.Name}' could not make its text page writable.");
                }

                _applied.Add(
                    new AppliedPatch(
                        patch.Name,
                        patch.Address,
                        patch.OriginalBytes,
                        patch.PatchBytes));
            }

            _bombPlanted = false;
            _active = true;
            _logger.LogInformation(
                "Pure ModSharp BotAI 1.8.7 enabled atomically with {Applied}/{Expected} {Platform} patches; pawnBot=0x{PawnBot:X}, allowActive=0x{AllowActive:X}.",
                _applied.Count,
                definitions.Count,
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                    ? "Linux"
                    : "Windows",
                _pawnBotOffset,
                _allowActiveOffset);
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or InvalidOperationException
                or ArgumentException
                or OverflowException)
        {
            Interlocked.Increment(ref _errors);
            RestoreAppliedPatches();
            _logger.LogError(
                exception,
                "Failed to enable pure ModSharp BotAI; every partial patch was restored.");
            return false;
        }
    }

    public void Deactivate()
    {
        if (!_active && _applied.Count == 0)
        {
            return;
        }

        _active = false;
        _bombPlanted = false;
        RestoreAppliedPatches();
        _logger.LogInformation(
            "Pure ModSharp BotAI disabled and all patches restored. Bomb-state corrections {Corrections}; errors {Errors}.",
            Interlocked.Read(ref _bombStateCorrections),
            Interlocked.Read(ref _errors));
    }

    public void ResetMap() => _bombPlanted = false;

    public void HandleGameEvent(IGameEvent gameEvent)
    {
        if (!_active)
        {
            return;
        }

        switch (gameEvent.Name)
        {
            case "round_start":
                _bombPlanted = false;
                break;
            case "bomb_planted":
                _bombPlanted = true;
                break;
            case "bomb_defused":
            case "bomb_exploded":
                _bombPlanted = false;
                break;
            case "player_spawn":
                UpdateSpawnedBotBombState(
                    gameEvent.GetPlayerController("userid"));
                break;
        }
    }

    public string GetStatus()
        => _active
            ? $"BotAI active: patches {_applied.Count}/{CurrentDefinitions.Count}, bomb-state corrections {Interlocked.Read(ref _bombStateCorrections)}, errors {Interlocked.Read(ref _errors)}."
            : $"BotAI inactive: patches 0/{CurrentDefinitions.Count}, errors {Interlocked.Read(ref _errors)}.";

    public void Dispose() => Deactivate();

    private unsafe void UpdateSpawnedBotBombState(
        IPlayerController? controller)
    {
        try
        {
            if (_bombPlanted
                || controller is not { IsValidEntity: true }
                || controller.Team is not (CStrikeTeam.TE or CStrikeTeam.CT)
                || controller.GetGameClient() is not { } client
                || !BotIdentityRegistry.IsBot(
                    client.IsFakeClient,
                    client.Slot.AsPrimitive())
                || controller.GetPawn()?.AsPlayerPawn()
                    is not { IsValidEntity: true } pawn)
            {
                return;
            }

            var botPointer = *(nint*)(
                pawn.GetAbsPtr() + _pawnBotOffset);
            if (botPointer == 0
                || *(byte*)(botPointer + _allowActiveOffset) == 0)
            {
                return;
            }

            var gameState = botPointer + GameStateOffset;
            if (*(byte*)(gameState + GameStateRoundOverOffset) != 0)
            {
                return;
            }

            var bombState = (int*)(
                gameState + GameStateBombStateOffset);
            if (*bombState == 0)
            {
                return;
            }

            *bombState = 0;
            Interlocked.Increment(ref _bombStateCorrections);
        }
        catch
        {
            Interlocked.Increment(ref _errors);
        }
    }

    private void RestoreAppliedPatches()
    {
        for (var index = _applied.Count - 1; index >= 0; index--)
        {
            var patch = _applied[index];
            try
            {
                if (!WriteBytes(
                        patch.Address,
                        patch.OriginalBytes))
                {
                    Interlocked.Increment(ref _errors);
                    _logger.LogError(
                        "Failed to restore BotAI patch {Patch} at 0x{Address:X}.",
                        patch.Name,
                        patch.Address);
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or ArgumentException)
            {
                Interlocked.Increment(ref _errors);
                _logger.LogError(
                    exception,
                    "Failed to restore BotAI patch {Patch} at 0x{Address:X}.",
                    patch.Name,
                    patch.Address);
            }
        }

        _applied.Clear();
    }

    private int ResolveOffset(string className, string fieldName)
    {
        var offset = _schema.GetNetVarOffset(className, fieldName);
        if (offset <= 0)
        {
            throw new InvalidDataException(
                $"Schema field {className}::{fieldName} resolved to invalid offset {offset}.");
        }

        return offset;
    }

    private void RelocateLinuxControlFlow(
        List<PlannedPatch> planned)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        var patches = planned.ToDictionary(
            patch => patch.Name,
            StringComparer.Ordinal);

        RelocateEnterApproachBody(patches);
        RelocateWatchApproachPoints(patches);
        RelocateWatchApproachLoopEntry(patches);
        RelocateLowSkillJump(patches);
        RelocatePlantedBombsiteCall(patches);
        RelocateOnBombPlantedJump(patches);
    }

    private void RelocateEnterApproachBody(
        IReadOnlyDictionary<string, PlannedPatch> patches)
    {
        var cave = GetPlannedPatch(
            patches,
            "Vision_AlwaysEnterApproachBody_Cave");
        var origin = GetPlannedPatch(
            patches,
            "Vision_AlwaysEnterApproachBody");
        var activePlayerTarget = ReadNearBranchTarget(
            origin.Name,
            origin.Address,
            origin.OriginalBytes,
            0x0F,
            0x85);
        var noPlayerJump = ReadBytes(
            origin.Address + origin.OriginalBytes.Length,
            5);
        var noPlayerTarget = ReadNearBranchTarget(
            $"{origin.Name} fallthrough",
            origin.Address + origin.OriginalBytes.Length,
            noPlayerJump,
            0xE9);

        var caveBytes = new byte[]
        {
            0x48, 0x83, 0x7B, 0x18, 0x00,
            0x0F, 0x84, 0x00, 0x00, 0x00, 0x00,
            0xE9, 0x00, 0x00, 0x00, 0x00,
        };
        WriteRelative32(
            cave.Name,
            caveBytes,
            7,
            cave.Address + 5,
            6,
            noPlayerTarget);
        WriteRelative32(
            cave.Name,
            caveBytes,
            12,
            cave.Address + 11,
            5,
            activePlayerTarget);
        ReplacePatchBytes(cave, caveBytes);
        ReplacePatchBytes(
            origin,
            CreateNearJump(
                origin.Name,
                origin.Address,
                cave.Address,
                origin.PatchBytes.Length));
    }

    private void RelocateWatchApproachPoints(
        IReadOnlyDictionary<string, PlannedPatch> patches)
    {
        var cave = GetPlannedPatch(
            patches,
            "Vision_AlwaysWatchApproachPoints_Cave");
        var origin = GetPlannedPatch(
            patches,
            "Vision_AlwaysWatchApproachPoints");
        var emptyTarget = ReadNearBranchTarget(
            origin.Name,
            origin.Address,
            origin.OriginalBytes,
            0x0F,
            0x84);
        var populatedTarget = origin.Address
            + origin.OriginalBytes.Length;

        var caveBytes = new byte[]
        {
            0x48, 0x83, 0x7B, 0x18, 0x00,
            0x0F, 0x84, 0x00, 0x00, 0x00, 0x00,
            0xE9, 0x00, 0x00, 0x00, 0x00,
        };
        WriteRelative32(
            cave.Name,
            caveBytes,
            7,
            cave.Address + 5,
            6,
            emptyTarget);
        WriteRelative32(
            cave.Name,
            caveBytes,
            12,
            cave.Address + 11,
            5,
            populatedTarget);
        ReplacePatchBytes(cave, caveBytes);
        ReplacePatchBytes(
            origin,
            CreateNearJump(
                origin.Name,
                origin.Address,
                cave.Address,
                origin.PatchBytes.Length));
    }

    private void RelocateWatchApproachLoopEntry(
        IReadOnlyDictionary<string, PlannedPatch> patches)
    {
        var cave = GetPlannedPatch(
            patches,
            "Vision_AlwaysWatchApproachPoints_LoopEntry_Cave");
        var origin = GetPlannedPatch(
            patches,
            "Vision_AlwaysWatchApproachPoints_LoopEntry");
        var nullBranchSignature = ResolveSupportAddress(
            "Vision loop null-target branch",
            LoopNullTargetSignature,
            origin.Address,
            0x1000);
        var nullBranchBytes = ReadBytes(
            nullBranchSignature,
            9);
        var nullTarget = ReadNearBranchTarget(
            "Vision loop null-target branch",
            nullBranchSignature + 3,
            nullBranchBytes.AsSpan(3),
            0x0F,
            0x83);
        var continueTarget = origin.Address + 18;

        var caveBytes = new byte[]
        {
            0x49, 0x8B, 0x7F, 0x10,
            0x48, 0x85, 0xFF,
            0x0F, 0x84, 0x00, 0x00, 0x00, 0x00,
            0x80, 0xBA, 0x24, 0x06, 0x00, 0x00, 0x02,
            0x75, 0x05,
            0xBE, 0x03, 0x00, 0x00, 0x00,
            0xE9, 0x00, 0x00, 0x00, 0x00,
        };
        WriteRelative32(
            cave.Name,
            caveBytes,
            9,
            cave.Address + 7,
            6,
            nullTarget);
        WriteRelative32(
            cave.Name,
            caveBytes,
            28,
            cave.Address + 27,
            5,
            continueTarget);
        ReplacePatchBytes(cave, caveBytes);
        ReplacePatchBytes(
            origin,
            CreateNearJump(
                origin.Name,
                origin.Address,
                cave.Address,
                origin.PatchBytes.Length));
    }

    private void RelocateLowSkillJump(
        IReadOnlyDictionary<string, PlannedPatch> patches)
    {
        var patch = GetPlannedPatch(
            patches,
            "LowSKill_JumpChance0");
        var targetSignature = ResolveSupportAddress(
            "LowSkill dodge target",
            LowSkillTargetSignature,
            patch.Address,
            0x100);
        var target = targetSignature + 6;
        var bytes = new byte[] { 0xEB, 0x00 };
        if (!BotAiPatchEncoding.TryWriteRelative8(
                bytes,
                1,
                patch.Address,
                2,
                target))
        {
            throw new InvalidDataException(
                $"BotAI patch '{patch.Name}' could not encode its validated short-jump target 0x{target:X}.");
        }

        ReplacePatchBytes(patch, bytes);
    }

    private void RelocatePlantedBombsiteCall(
        IReadOnlyDictionary<string, PlannedPatch> patches)
    {
        var patch = GetPlannedPatch(
            patches,
            "TBot_BombsiteSearch_UseKnownPlantedSite");
        var target = ResolveSupportAddress(
            "CSGameState::GetPlantedBombsite",
            PlantedBombsiteHelperSignature);
        var bytes = new byte[]
        {
            0xE8, 0x00, 0x00, 0x00, 0x00,
        };
        WriteRelative32(
            patch.Name,
            bytes,
            1,
            patch.Address,
            5,
            target);
        ReplacePatchBytes(patch, bytes);
    }

    private static void RelocateOnBombPlantedJump(
        IReadOnlyDictionary<string, PlannedPatch> patches)
    {
        var patch = GetPlannedPatch(
            patches,
            "OnBombPlanted_AllBotsLearnSite");
        var target = ReadNearBranchTarget(
            patch.Name,
            patch.Address,
            patch.OriginalBytes,
            0x0F,
            0x84);
        ReplacePatchBytes(
            patch,
            CreateNearJump(
                patch.Name,
                patch.Address,
                target,
                patch.PatchBytes.Length));
    }

    private nint ResolveSupportAddress(
        string name,
        string signature,
        nint nearAddress = 0,
        long maximumDistance = long.MaxValue)
    {
        var matches = _server.FindPatternMulti(signature);
        var candidates = nearAddress == 0
            ? matches
            : matches.Where(
                    address => Math.Abs(
                        (long)address - (long)nearAddress)
                        <= maximumDistance)
                .ToList();
        if (candidates.Count != 1)
        {
            throw new InvalidDataException(
                $"BotAI support signature '{name}' resolved to {candidates.Count} valid candidate(s), expected exactly one.");
        }

        return candidates[0];
    }

    private static PlannedPatch GetPlannedPatch(
        IReadOnlyDictionary<string, PlannedPatch> patches,
        string name)
        => patches.TryGetValue(name, out var patch)
            ? patch
            : throw new InvalidDataException(
                $"BotAI relocation dependency '{name}' is missing.");

    private static nint ReadNearBranchTarget(
        string name,
        nint address,
        ReadOnlySpan<byte> instruction,
        params byte[] opcode)
    {
        if (instruction.Length < opcode.Length + sizeof(int)
            || !instruction[..opcode.Length].SequenceEqual(opcode)
            || !BotAiPatchEncoding.TryReadRelative32Target(
                address,
                instruction,
                opcode.Length,
                opcode.Length + sizeof(int),
                out var target))
        {
            throw new InvalidDataException(
                $"BotAI relocation for '{name}' found an invalid near branch at 0x{address:X}.");
        }

        return target;
    }

    private static byte[] CreateNearJump(
        string name,
        nint address,
        nint target,
        int length)
    {
        if (length < 5)
        {
            throw new InvalidDataException(
                $"BotAI patch '{name}' has no room for a near jump.");
        }

        var bytes = Enumerable.Repeat(
                (byte)0x90,
                length)
            .ToArray();
        bytes[0] = 0xE9;
        WriteRelative32(
            name,
            bytes,
            1,
            address,
            5,
            target);
        return bytes;
    }

    private static void WriteRelative32(
        string name,
        Span<byte> bytes,
        int displacementOffset,
        nint instructionAddress,
        int instructionLength,
        nint target)
    {
        if (!BotAiPatchEncoding.TryWriteRelative32(
                bytes,
                displacementOffset,
                instructionAddress,
                instructionLength,
                target))
        {
            throw new InvalidDataException(
                $"BotAI patch '{name}' could not encode target 0x{target:X} from 0x{instructionAddress:X}.");
        }
    }

    private static void ReplacePatchBytes(
        PlannedPatch patch,
        ReadOnlySpan<byte> replacement)
    {
        if (replacement.Length != patch.PatchBytes.Length)
        {
            throw new InvalidDataException(
                $"BotAI relocated patch '{patch.Name}' changed length from {patch.PatchBytes.Length} to {replacement.Length}.");
        }

        replacement.CopyTo(patch.PatchBytes);
    }

    private (nint Address, byte[] OriginalBytes) ResolvePatchTarget(
        string name,
        string signature,
        string expectedOriginal,
        int patchOffset,
        int patchLength)
    {
        var matches = _server.FindPatternMulti(signature);
        var valid = new List<(nint Address, byte[] OriginalBytes)>();
        foreach (var signatureAddress in matches)
        {
            var address = signatureAddress + patchOffset;
            var original = ReadBytes(address, patchLength);
            if (BotAiPatchEncoding.MatchesExpected(
                    original,
                    expectedOriginal))
            {
                valid.Add((address, original));
            }
        }

        if (valid.Count == 1)
        {
            return valid[0];
        }

        if (matches.Count == 0)
        {
            throw new InvalidDataException(
                $"BotAI signature '{name}' was not found.");
        }

        if (valid.Count == 0)
        {
            var candidates = string.Join(
                "; ",
                matches.Select(
                    signatureAddress =>
                    {
                        var address = signatureAddress + patchOffset;
                        return $"0x{address:X}=[{FormatBytes(ReadBytes(address, patchLength))}]";
                    }));
            throw new InvalidDataException(
                $"BotAI patch '{name}' expected [{expectedOriginal}], but none of {matches.Count} signature candidate(s) matched: {candidates}.");
        }

        throw new InvalidDataException(
            $"BotAI patch '{name}' remained ambiguous: {valid.Count} of {matches.Count} signature candidate(s) matched the expected original bytes.");
    }

    private static string GetCompatibleSignature(
        string name,
        string upstreamSignature)
        => RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && name == "Vision_AlwaysWatchApproachPoints_Cave"
                ? WatchApproachCaveCompatibleSignature
                : upstreamSignature;

    private static string GetCompatibleExpectedOriginal(
        string name,
        string upstreamExpectedOriginal)
        => RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && name == "TBot_BombsiteSearch_UseKnownPlantedSite"
                ? "E8 ? ? ? ?"
                : upstreamExpectedOriginal;

    private static byte[] ReadBytes(nint address, int length)
    {
        if (address == 0 || length <= 0)
        {
            throw new ArgumentException(
                "BotAI memory read requires a valid address and length.");
        }

        var bytes = new byte[length];
        Marshal.Copy(address, bytes, 0, length);
        return bytes;
    }

    private static bool WriteBytes(
        nint address,
        ReadOnlySpan<byte> bytes)
    {
        if (!BotAiMemoryProtection.TryMakeWritable(
                address,
                bytes.Length,
                out var protection)
            || protection is null)
        {
            return false;
        }

        using (protection)
        {
            var copy = bytes.ToArray();
            Marshal.Copy(copy, 0, address, copy.Length);
        }

        return true;
    }

    private static string FormatBytes(IEnumerable<byte> bytes)
        => string.Join(
            " ",
            bytes.Select(value => value.ToString("X2")));

    private static IReadOnlyDictionary<
        string,
        (
            string signature,
            string patch,
            string expectedOriginal,
            int patchOffset)> CurrentDefinitions
        => RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? LinuxPatchDefinitions.All
            : WindowsPatchDefinitions.All;

    private sealed record PlannedPatch(
        string Name,
        nint Address,
        byte[] OriginalBytes,
        byte[] PatchBytes);

    private sealed record AppliedPatch(
        string Name,
        nint Address,
        byte[] OriginalBytes,
        byte[] PatchBytes);
}
