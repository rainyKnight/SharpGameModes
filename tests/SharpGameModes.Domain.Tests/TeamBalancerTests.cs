namespace SharpGameModes.Domain.Tests;

public sealed class TeamBalancerTests
{
    [Fact]
    public void CreatePlan_BalancesCountsAndRatingDeterministically()
    {
        PlayerBalanceCandidate[] players =
        [
            new("4", 0.5, TeamAssignment.Terrorist),
            new("2", 1.5, TeamAssignment.CounterTerrorist),
            new("1", 2.0, TeamAssignment.CounterTerrorist),
            new("3", 1.0, TeamAssignment.Terrorist),
        ];

        var first = TeamBalancer.CreatePlan(players);
        var second = TeamBalancer.CreatePlan(players.Reverse());

        Assert.Equal(2, first.CounterTerroristCount);
        Assert.Equal(2, first.TerroristCount);
        Assert.Equal(first.Assignments, second.Assignments);
        Assert.Equal(first.CounterTerroristRating, first.TerroristRating, precision: 10);
    }

    [Fact]
    public void CreatePlan_DoesNotMoveSpectatorsOrUnassignedPlayers()
    {
        PlayerBalanceCandidate[] players =
        [
            new("active-ct", 1.0, TeamAssignment.CounterTerrorist),
            new("active-t", 1.0, TeamAssignment.Terrorist),
            new("spectator", 3.0, TeamAssignment.Spectator),
            new("unassigned", 3.0, TeamAssignment.Unassigned),
        ];

        var plan = TeamBalancer.CreatePlan(players);

        Assert.Equal(2, plan.Assignments.Count);
        Assert.DoesNotContain("spectator", plan.Assignments.Keys);
        Assert.DoesNotContain("unassigned", plan.Assignments.Keys);
    }

    [Fact]
    public void CreateMinimumMovementPlan_MovesOnlyRequiredPlayersAndUsesRatingTieBreak()
    {
        PlayerBalanceCandidate[] players =
        [
            new("ct-high", 2.0, TeamAssignment.CounterTerrorist),
            new("ct-mid", 1.2, TeamAssignment.CounterTerrorist),
            new("ct-low", 0.8, TeamAssignment.CounterTerrorist),
            new("t-low", 0.7, TeamAssignment.Terrorist),
        ];

        var plan = TeamBalancer.CreateMinimumMovementPlan(players);

        Assert.Equal(2, plan.CounterTerroristCount);
        Assert.Equal(2, plan.TerroristCount);
        Assert.Equal(
            1,
            players.Count(player => plan.Assignments[player.Id] != player.CurrentTeam));
    }

    [Fact]
    public void CreateMinimumMovementPlan_RespectsAllowedCountDeviation()
    {
        PlayerBalanceCandidate[] players =
        [
            new("ct-1", 1.0, TeamAssignment.CounterTerrorist),
            new("ct-2", 1.0, TeamAssignment.CounterTerrorist),
            new("ct-3", 1.0, TeamAssignment.CounterTerrorist),
            new("t-1", 1.0, TeamAssignment.Terrorist),
        ];

        var plan = TeamBalancer.CreateMinimumMovementPlan(players, allowedCountDeviation: 1);

        Assert.All(players, player => Assert.Equal(player.CurrentTeam, plan.Assignments[player.Id]));
    }

    [Theory]
    [InlineData(2, 1, 20)]
    [InlineData(3, 1, 20)]
    [InlineData(64, 1, 1)]
    public void CalculateTargetCounterTerroristCount_KeepsBothTeamsPopulated(
        int total,
        int ctRatio,
        int tRatio)
    {
        var targetCt = TeamBalancer.CalculateTargetCounterTerroristCount(total, 0, ctRatio, tRatio);

        Assert.InRange(targetCt, 1, total - 1);
    }
}
