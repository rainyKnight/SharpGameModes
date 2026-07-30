namespace SharpGameModes.Domain.Tests;

public sealed class ZombieRoundRulesTests
{
    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(8, 2)]
    [InlineData(64, 16)]
    public void CalculateInitialZombieCount_MatchesProductionRatio(int players, int expected)
    {
        var actual = ZombieRoundRules.CalculateInitialZombieCount(players, 1, 0.25, 0);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CalculateInitialZombieCount_AppliesMaximumWithoutInfectingEveryone()
    {
        Assert.Equal(3, ZombieRoundRules.CalculateInitialZombieCount(20, 1, 0.25, 3));
        Assert.Equal(1, ZombieRoundRules.CalculateInitialZombieCount(2, 8, 1, 0));
    }

    [Theory]
    [InlineData(0, 2, 0, 30, ZombieRoundOutcome.ZombiesWin)]
    [InlineData(0, 2, 1, 30, ZombieRoundOutcome.None)]
    [InlineData(3, 0, 0, 30, ZombieRoundOutcome.HumansWin)]
    [InlineData(3, 2, 0, 0, ZombieRoundOutcome.HumansWin)]
    [InlineData(3, 2, 0, 30, ZombieRoundOutcome.None)]
    public void Evaluate_AccountsForPendingCorpseInfections(
        int humans,
        int zombies,
        int pending,
        int seconds,
        ZombieRoundOutcome expected)
    {
        Assert.Equal(expected, ZombieRoundRules.Evaluate(humans, zombies, pending, seconds));
    }
}
