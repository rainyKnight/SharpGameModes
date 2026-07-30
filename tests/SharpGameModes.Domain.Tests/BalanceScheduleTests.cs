namespace SharpGameModes.Domain.Tests;

public sealed class BalanceScheduleTests
{
    [Fact]
    public void RequiresRebalance_PreservesBalancedRosterAfterHalfTimeSwap()
    {
        string[] roster = ["1", "2", "3", "4"];

        var required = BalanceSchedule.RequiresRebalance(roster, roster, 2, 2);

        Assert.False(required);
    }

    [Fact]
    public void RequiresRebalance_PreservesOddRosterDuringEngineHalfTimeSwap()
    {
        string[] roster = ["1"];

        Assert.False(BalanceSchedule.RequiresRebalance(
            roster,
            roster,
            currentCounterTerrorists: 0,
            currentTerrorists: 1,
            preserveEngineTeamSwitch: true));
    }

    [Fact]
    public void RequiresRebalance_StillHandlesRosterChangeDuringEngineHalfTimeSwap()
    {
        string[] previous = ["1"];

        Assert.True(BalanceSchedule.RequiresRebalance(
            previous,
            ["1", "2"],
            currentCounterTerrorists: 0,
            currentTerrorists: 2,
            preserveEngineTeamSwitch: true));
    }

    [Fact]
    public void RequiresRebalance_DetectsRosterOrCountChanges()
    {
        string[] previous = ["1", "2", "3", "4"];

        Assert.True(BalanceSchedule.RequiresRebalance(previous, ["1", "2", "3", "5"], 2, 2));
        Assert.True(BalanceSchedule.RequiresRebalance(previous, previous, 3, 1));
    }

    [Fact]
    public void RequiresRebalance_RespectsUnequalRatios()
    {
        string[] roster = ["1", "2", "3", "4"];

        Assert.False(BalanceSchedule.RequiresRebalance(roster, roster, 1, 3, 1, 3));
        Assert.True(BalanceSchedule.RequiresRebalance(roster, roster, 2, 2, 1, 3));
    }
}
