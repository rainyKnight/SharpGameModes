using System.Runtime.InteropServices;
using SharpGameModes.Contracts;

namespace SharpGameModes.BotMatch.Tests;

public sealed class BotControllerStateStoreTests
{
    [Fact]
    public void PublicLayoutsMatchUpstreamAbi16()
    {
        Assert.Equal(16, IBotController.CurrentAbiVersion);
        Assert.Equal(92, Marshal.SizeOf<BotMovementSnapshot>());
        Assert.Equal(192, Marshal.SizeOf<BotReplayTick>());
        Assert.Equal(28, Marshal.SizeOf<BotSubtickMove>());
        Assert.Equal(68, Marshal.SizeOf<BotReplayCommandFrame>());
        Assert.Equal(48, Marshal.SizeOf<BotReplayMovementExtra>());
        Assert.Equal(76, Marshal.SizeOf<BotProfileData>());
    }

    [Fact]
    public void LocksAreIndependentAndUnlockAllIsKindScoped()
    {
        var state = new BotControllerStateStore();

        Assert.True(state.Lock(3, BotLockKind.All));
        Assert.True(state.Lock(3, BotLockKind.Aim));
        Assert.True(state.Lock(4, BotLockKind.Jump));
        Assert.True(state.LockWeapon(3, BotLockTarget.Slot3));
        Assert.Equal(BotLockTarget.Slot3, state.GetWeaponLock(3));

        Assert.True(state.UnlockAll(BotLockKind.Aim));
        Assert.True(state.IsLocked(3, BotLockKind.All));
        Assert.False(state.IsLocked(3, BotLockKind.Aim));
        Assert.True(state.IsLocked(3, BotLockKind.Weapon));
        Assert.True(state.IsLocked(4, BotLockKind.Jump));
    }

    [Fact]
    public void RecordingCommitsParallelTickAndSubtickBuffers()
    {
        var state = new BotControllerStateStore();
        var pre = Snapshot(10);
        var post = Snapshot(20);
        BotSubtickMove[] subticks =
        [
            new() { When = 0.25f, Button = 1, Pressed = 1 },
            new() { When = 0.75f, AnalogForward = 0.4f },
        ];

        Assert.True(state.StartRecord(1));
        state.CapturePre(1, pre, subticks, default, default);
        state.CapturePost(1, post, 7);
        Assert.True(state.StopRecord(1));

        var motion = state.GetRecordedMotion(1);
        var tick = Assert.Single(motion.Ticks);
        Assert.Equal(2U, tick.NumSubtick);
        Assert.Equal(7, tick.WeaponDefIndex);
        Assert.Equal(10, tick.Pre.OriginX);
        Assert.Equal(20, tick.Post.OriginX);
        Assert.Equal(2, motion.Subticks.Length);
    }

    [Fact]
    public void ReplayRejectsMalformedParallelBuffers()
    {
        var state = new BotControllerStateStore();
        BotReplayTick[] ticks =
        [
            new() { NumSubtick = 2 },
        ];

        Assert.False(state.LoadReplay(2, ticks, [default], [], []));
        ticks[0].NumSubtick = 37;
        Assert.False(
            state.LoadReplay(
                2,
                ticks,
                new BotSubtickMove[37],
                [],
                []));
        ticks[0].NumSubtick = 0;
        Assert.False(
            state.LoadReplay(
                2,
                ticks,
                [],
                new BotReplayCommandFrame[2],
                []));
    }

    [Fact]
    public void TransferPreservesExtendedFramesAndLoopCursor()
    {
        var state = new BotControllerStateStore();
        Assert.True(state.StartRecord(1));
        state.CapturePre(
            1,
            Snapshot(1),
            [],
            new BotReplayCommandFrame
            {
                ForwardMove = 0.5f,
                Fields = (uint)BotReplayCommandFields.Movement,
            },
            new BotReplayMovementExtra
            {
                Fields = (uint)BotReplayMovementFields.JumpPressedTime,
                JumpPressedTime = 0.2f,
            });
        state.CapturePost(1, Snapshot(2), 9);

        Assert.True(state.TransferRecordingToReplay(1, 5));
        Assert.True(state.SetReplayPawn(5, (nint)1234));
        Assert.True(state.StartReplay(5, loop: true));
        Assert.True(
            state.TryGetReplayFrame(
                5,
                out var tick,
                out _,
                out var command,
                out var movement,
                out var pawn));
        Assert.Equal(9, tick.WeaponDefIndex);
        Assert.Equal(0.5f, command.ForwardMove);
        Assert.Equal(0.2f, movement.JumpPressedTime);
        Assert.Equal((nint)1234, pawn);

        Assert.True(state.AdvanceReplay(5));
        Assert.Equal(0, state.ReplayCursor(5));
        Assert.True(state.IsReplaying(5));
    }

    [Fact]
    public void ReplayRejectsNewLocksAndPawnRebinding()
    {
        var state = new BotControllerStateStore();
        BotReplayTick[] ticks =
        [
            new() { NumSubtick = 0 },
        ];

        Assert.True(state.LoadReplay(6, ticks, [], [], []));
        Assert.True(state.SetReplayPawn(6, (nint)1234));
        Assert.True(state.StartReplay(6, loop: false));

        Assert.False(state.Lock(6, BotLockKind.All));
        Assert.False(state.LockWeapon(6, BotLockTarget.Slot1));
        Assert.False(state.SetReplayPawn(6, (nint)5678));

        Assert.True(state.StopReplay(6));
        Assert.True(state.Lock(6, BotLockKind.All));
        Assert.True(state.LockWeapon(6, BotLockTarget.Slot1));
        Assert.True(state.SetReplayPawn(6, (nint)5678));
    }

    [Fact]
    public void BuyPlansSplitAliasesAndDropUnsafeTokens()
    {
        var state = new BotControllerStateStore();

        Assert.True(
            state.SetBuyPlan(
                7,
                "ak47, vesthelm;quit flashbang defuser",
                skip: false));
        var plan = Assert.IsType<BotControllerStateStore.BuyPlan>(state.GetBuyPlan(7));
        Assert.Equal(["ak47", "flashbang", "defuser"], plan.Items);
        Assert.False(plan.Skip);
        Assert.Equal(3, state.BuyPlanItemCount(7));

        Assert.True(state.SetBuyPlan(7, string.Empty, skip: true));
        Assert.True(state.GetBuyPlan(7)!.Skip);
        Assert.Equal(0, state.BuyPlanItemCount(7));
    }

    [Theory]
    [InlineData(null, "76561198000000000.json")]
    [InlineData("practice-a", "practice-a.json")]
    [InlineData("practice_b.json", "practice_b.json")]
    public void RecordingPathsStayInsideDedicatedDirectory(
        string? requested,
        string expectedFile)
    {
        Assert.True(
            BotMotionStore.TryResolvePath(
                "recordings",
                requested,
                76561198000000000,
                out var path));
        Assert.Equal(expectedFile, Path.GetFileName(path));
        Assert.Equal("recordings", Path.GetDirectoryName(path));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("demo;quit")]
    [InlineData("demo name")]
    public void RecordingPathsRejectTraversalAndCommands(string requested)
        => Assert.False(
            BotMotionStore.TryResolvePath(
                "recordings",
                requested,
                76561198000000000,
                out _));

    private static BotMovementSnapshot Snapshot(float originX)
        => new()
        {
            OriginX = originX,
            VelX = originX / 2,
            Pitch = 5,
            Yaw = 90,
            Buttons = 1,
            MoveType = 2,
            ActualMoveType = 2,
        };
}
