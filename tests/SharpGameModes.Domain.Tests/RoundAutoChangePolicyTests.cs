namespace SharpGameModes.Domain.Tests;

public sealed class RoundAutoChangePolicyTests
{
    [Theory]
    [InlineData(0, RoundAutoChangeAction.None)]
    [InlineData(1, RoundAutoChangeAction.None)]
    [InlineData(2, RoundAutoChangeAction.StartVote)]
    [InlineData(5, RoundAutoChangeAction.None)]
    [InlineData(6, RoundAutoChangeAction.ChangeMap)]
    public void ZombieSchedule_StartsVoteAtThreeAndChangesAfterSix(
        int completedRounds,
        RoundAutoChangeAction expected)
    {
        var existingVote = completedRounds is 3 or 4 or 5;

        var actual = RoundAutoChangePolicy.Evaluate(completedRounds, 3, 6, existingVote);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ExistingNextMap_PreventsDuplicateVote()
    {
        Assert.Equal(
            RoundAutoChangeAction.None,
            RoundAutoChangePolicy.Evaluate(2, 3, 6, voteOrNextMapAlreadyExists: true));
    }
}
