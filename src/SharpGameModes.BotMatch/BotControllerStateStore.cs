using SharpGameModes.Contracts;

namespace SharpGameModes.BotMatch;

internal sealed class BotControllerStateStore
{
    public const int MaximumSlots = 64;
    public const int MaximumSubticksPerTick = 36;

    private readonly bool[] _allLocks = new bool[MaximumSlots];
    private readonly bool[] _aimLocks = new bool[MaximumSlots];
    private readonly bool[] _jumpLocks = new bool[MaximumSlots];
    private readonly BotLockTarget[] _weaponLocks = new BotLockTarget[MaximumSlots];
    private readonly RecordingSlot[] _recordings =
        Enumerable.Range(0, MaximumSlots).Select(_ => new RecordingSlot()).ToArray();
    private readonly ReplaySlot[] _replays =
        Enumerable.Range(0, MaximumSlots).Select(_ => new ReplaySlot()).ToArray();
    private readonly BuyPlan?[] _buyPlans = new BuyPlan?[MaximumSlots];

    public bool Lock(int slot, BotLockKind kind)
    {
        if (!ValidSlot(slot) || _replays[slot].Playing)
        {
            return false;
        }

        switch (kind)
        {
            case BotLockKind.All:
                _allLocks[slot] = true;
                return true;
            case BotLockKind.Aim:
                _aimLocks[slot] = true;
                return true;
            case BotLockKind.Jump:
                _jumpLocks[slot] = true;
                return true;
            default:
                return false;
        }
    }

    public bool LockWeapon(int slot, BotLockTarget target)
    {
        if (!ValidSlot(slot)
            || _replays[slot].Playing
            || target is < BotLockTarget.Slot1 or > BotLockTarget.Slot5)
        {
            return false;
        }

        _weaponLocks[slot] = target;
        return true;
    }

    public bool Unlock(int slot, BotLockKind kind)
    {
        if (!ValidSlot(slot))
        {
            return false;
        }

        switch (kind)
        {
            case BotLockKind.All:
                _allLocks[slot] = false;
                return true;
            case BotLockKind.Aim:
                _aimLocks[slot] = false;
                return true;
            case BotLockKind.Weapon:
                _weaponLocks[slot] = BotLockTarget.None;
                return true;
            case BotLockKind.Jump:
                _jumpLocks[slot] = false;
                return true;
            default:
                return false;
        }
    }

    public bool UnlockAll(BotLockKind kind)
    {
        switch (kind)
        {
            case BotLockKind.All:
                Array.Clear(_allLocks);
                return true;
            case BotLockKind.Aim:
                Array.Clear(_aimLocks);
                return true;
            case BotLockKind.Weapon:
                Array.Clear(_weaponLocks);
                return true;
            case BotLockKind.Jump:
                Array.Clear(_jumpLocks);
                return true;
            default:
                return false;
        }
    }

    public bool IsLocked(int slot, BotLockKind kind)
        => ValidSlot(slot) && kind switch
        {
            BotLockKind.All => _allLocks[slot],
            BotLockKind.Aim => _aimLocks[slot],
            BotLockKind.Weapon => _weaponLocks[slot] != BotLockTarget.None,
            BotLockKind.Jump => _jumpLocks[slot],
            _ => false,
        };

    public BotLockTarget GetWeaponLock(int slot)
        => ValidSlot(slot) ? _weaponLocks[slot] : BotLockTarget.None;

    public bool StartRecord(int slot)
    {
        if (!ValidSlot(slot))
        {
            return false;
        }

        _recordings[slot].Clear();
        _recordings[slot].Recording = true;
        return true;
    }

    public bool StopRecord(int slot)
    {
        if (!ValidSlot(slot))
        {
            return false;
        }

        _recordings[slot].Recording = false;
        return true;
    }

    public bool IsRecording(int slot)
        => ValidSlot(slot) && _recordings[slot].Recording;

    public int RecordedTickCount(int slot)
        => ValidSlot(slot) ? _recordings[slot].Ticks.Count : -1;

    public void CapturePre(
        int slot,
        BotMovementSnapshot snapshot,
        IReadOnlyList<BotSubtickMove> subticks,
        BotReplayCommandFrame command,
        BotReplayMovementExtra movementExtra)
    {
        if (!IsRecording(slot))
        {
            return;
        }

        var recording = _recordings[slot];
        recording.PendingPre = snapshot;
        recording.PendingSubticks.Clear();
        recording.PendingSubticks.AddRange(subticks.Take(MaximumSubticksPerTick));
        recording.PendingCommand = command;
        recording.PendingMovementExtra = movementExtra;
        recording.HasPending = true;
    }

    public void CapturePost(int slot, BotMovementSnapshot snapshot, int weaponDefinitionIndex)
    {
        if (!IsRecording(slot))
        {
            return;
        }

        var recording = _recordings[slot];
        var subtickCount = recording.HasPending
            ? recording.PendingSubticks.Count
            : 0;
        recording.Ticks.Add(
            new BotReplayTick
            {
                Pre = recording.HasPending ? recording.PendingPre : snapshot,
                Post = snapshot,
                WeaponDefIndex = weaponDefinitionIndex,
                NumSubtick = (uint)subtickCount,
            });
        if (recording.HasPending)
        {
            recording.Subticks.AddRange(recording.PendingSubticks);
            recording.Commands.Add(recording.PendingCommand);
            recording.MovementExtras.Add(recording.PendingMovementExtra);
        }
        else
        {
            recording.Commands.Add(default);
            recording.MovementExtras.Add(default);
        }

        recording.PendingSubticks.Clear();
        recording.HasPending = false;
    }

    public (BotReplayTick[] Ticks, BotSubtickMove[] Subticks) GetRecordedMotion(int slot)
    {
        if (!ValidSlot(slot))
        {
            return ([], []);
        }

        var recording = _recordings[slot];
        return (recording.Ticks.ToArray(), recording.Subticks.ToArray());
    }

    public bool LoadReplay(
        int slot,
        BotReplayTick[]? ticks,
        BotSubtickMove[]? subticks,
        BotReplayCommandFrame[]? commands = null,
        BotReplayMovementExtra[]? movementExtras = null)
    {
        if (!ValidSlot(slot)
            || ticks is not { Length: > 0 }
            || subticks is null
            || commands is null
            || movementExtras is null
            || commands.Length is not (0) && commands.Length != ticks.Length
            || movementExtras.Length is not (0) && movementExtras.Length != ticks.Length)
        {
            return false;
        }

        var expectedSubticks = 0L;
        var offsets = new int[ticks.Length + 1];
        for (var index = 0; index < ticks.Length; index++)
        {
            if (ticks[index].NumSubtick > MaximumSubticksPerTick)
            {
                return false;
            }

            offsets[index] = checked((int)expectedSubticks);
            expectedSubticks += ticks[index].NumSubtick;
            if (expectedSubticks > subticks.Length)
            {
                return false;
            }
        }

        if (expectedSubticks != subticks.Length)
        {
            return false;
        }

        offsets[^1] = subticks.Length;
        var replay = _replays[slot];
        if (replay.Playing)
        {
            return false;
        }

        replay.Ticks = ticks.ToArray();
        replay.Subticks = subticks.ToArray();
        replay.Commands = commands.Length == 0
            ? new BotReplayCommandFrame[ticks.Length]
            : commands.ToArray();
        replay.MovementExtras = movementExtras.Length == 0
            ? new BotReplayMovementExtra[ticks.Length]
            : movementExtras.ToArray();
        replay.SubtickOffsets = offsets;
        replay.Cursor = 0;
        replay.Loop = false;
        return true;
    }

    public bool TransferRecordingToReplay(int sourceSlot, int destinationSlot)
    {
        if (!ValidSlot(sourceSlot)
            || !ValidSlot(destinationSlot)
            || _recordings[sourceSlot].Ticks.Count == 0)
        {
            return false;
        }

        var recording = _recordings[sourceSlot];
        return LoadReplay(
            destinationSlot,
            recording.Ticks.ToArray(),
            recording.Subticks.ToArray(),
            recording.Commands.ToArray(),
            recording.MovementExtras.ToArray());
    }

    public bool SetReplayPawn(int slot, nint pawn)
    {
        if (!ValidSlot(slot) || pawn == 0 || _replays[slot].Playing)
        {
            return false;
        }

        _replays[slot].Pawn = pawn;
        return true;
    }

    public bool StartReplay(int slot, bool loop)
    {
        if (!ValidSlot(slot) || _replays[slot].Ticks.Length == 0)
        {
            return false;
        }

        var replay = _replays[slot];
        replay.Cursor = 0;
        replay.Loop = loop;
        replay.Playing = true;
        return true;
    }

    public bool StopReplay(int slot)
    {
        if (!ValidSlot(slot))
        {
            return false;
        }

        var replay = _replays[slot];
        replay.Playing = false;
        replay.Cursor = 0;
        replay.Pawn = 0;
        return true;
    }

    public int ReplayCursor(int slot)
        => ValidSlot(slot) && _replays[slot].Playing
            ? _replays[slot].Cursor
            : -1;

    public int ReplayTotal(int slot)
        => ValidSlot(slot) ? _replays[slot].Ticks.Length : 0;

    public bool IsReplaying(int slot)
        => ValidSlot(slot) && _replays[slot].Playing;

    public bool TryGetReplayFrame(
        int slot,
        out BotReplayTick tick,
        out ArraySegment<BotSubtickMove> subticks,
        out BotReplayCommandFrame command,
        out BotReplayMovementExtra movementExtra,
        out nint pawn)
    {
        tick = default;
        subticks = default;
        command = default;
        movementExtra = default;
        pawn = 0;
        if (!IsReplaying(slot))
        {
            return false;
        }

        var replay = _replays[slot];
        if (replay.Cursor < 0 || replay.Cursor >= replay.Ticks.Length)
        {
            return false;
        }

        var begin = replay.SubtickOffsets[replay.Cursor];
        var end = replay.SubtickOffsets[replay.Cursor + 1];
        tick = replay.Ticks[replay.Cursor];
        subticks = new ArraySegment<BotSubtickMove>(
            replay.Subticks,
            begin,
            end - begin);
        command = replay.Commands[replay.Cursor];
        movementExtra = replay.MovementExtras[replay.Cursor];
        pawn = replay.Pawn;
        return true;
    }

    public bool TryGetReplayTick(int slot, out BotReplayTick tick)
    {
        tick = default;
        if (!IsReplaying(slot))
        {
            return false;
        }

        var replay = _replays[slot];
        var index = Math.Clamp(replay.Cursor - 1, 0, replay.Ticks.Length - 1);
        tick = replay.Ticks[index];
        return true;
    }

    public bool AdvanceReplay(int slot)
    {
        if (!IsReplaying(slot))
        {
            return false;
        }

        var replay = _replays[slot];
        replay.Cursor++;
        if (replay.Cursor < replay.Ticks.Length)
        {
            return true;
        }

        if (replay.Loop)
        {
            replay.Cursor = 0;
            return true;
        }

        replay.Playing = false;
        replay.Cursor = 0;
        replay.Pawn = 0;
        return false;
    }

    public bool SetBuyPlan(int slot, string? aliases, bool skip)
    {
        if (!ValidSlot(slot))
        {
            return false;
        }

        var items = skip
            ? []
            : (aliases ?? string.Empty)
                .Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(IsSafeBuyAlias)
                .Take(32)
                .ToArray();
        _buyPlans[slot] = new BuyPlan(items, skip);
        return true;
    }

    public bool ClearBuyPlan(int slot)
    {
        if (!ValidSlot(slot))
        {
            return false;
        }

        _buyPlans[slot] = null;
        return true;
    }

    public bool ClearAllBuyPlans()
    {
        Array.Clear(_buyPlans);
        return true;
    }

    public int BuyPlanItemCount(int slot)
        => ValidSlot(slot) && _buyPlans[slot] is { } plan
            ? plan.Items.Length
            : -1;

    public BuyPlan? GetBuyPlan(int slot)
        => ValidSlot(slot) ? _buyPlans[slot] : null;

    public void Clear()
    {
        Array.Clear(_allLocks);
        Array.Clear(_aimLocks);
        Array.Clear(_jumpLocks);
        Array.Clear(_weaponLocks);
        Array.Clear(_buyPlans);
        foreach (var recording in _recordings)
        {
            recording.Clear();
        }

        foreach (var replay in _replays)
        {
            replay.Clear();
        }
    }

    private static bool ValidSlot(int slot)
        => slot is >= 0 and < MaximumSlots;

    private static bool IsSafeBuyAlias(string alias)
        => alias.Length is > 0 and <= 64
            && alias.All(character =>
                char.IsAsciiLetterOrDigit(character)
                || character is '_' or '-');

    internal sealed record BuyPlan(string[] Items, bool Skip);

    private sealed class RecordingSlot
    {
        public bool Recording;
        public bool HasPending;
        public BotMovementSnapshot PendingPre;
        public BotReplayCommandFrame PendingCommand;
        public BotReplayMovementExtra PendingMovementExtra;
        public List<BotSubtickMove> PendingSubticks { get; } = new(MaximumSubticksPerTick);
        public List<BotReplayTick> Ticks { get; } = new(4096);
        public List<BotSubtickMove> Subticks { get; } = new(4096);
        public List<BotReplayCommandFrame> Commands { get; } = new(4096);
        public List<BotReplayMovementExtra> MovementExtras { get; } = new(4096);

        public void Clear()
        {
            Recording = false;
            HasPending = false;
            PendingSubticks.Clear();
            Ticks.Clear();
            Subticks.Clear();
            Commands.Clear();
            MovementExtras.Clear();
        }
    }

    private sealed class ReplaySlot
    {
        public bool Playing;
        public bool Loop;
        public int Cursor;
        public nint Pawn;
        public BotReplayTick[] Ticks = [];
        public BotSubtickMove[] Subticks = [];
        public int[] SubtickOffsets = [0];
        public BotReplayCommandFrame[] Commands = [];
        public BotReplayMovementExtra[] MovementExtras = [];

        public void Clear()
        {
            Playing = false;
            Loop = false;
            Cursor = 0;
            Pawn = 0;
            Ticks = [];
            Subticks = [];
            SubtickOffsets = [0];
            Commands = [];
            MovementExtras = [];
        }
    }
}
