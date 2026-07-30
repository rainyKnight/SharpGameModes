namespace SharpGameModes.Domain;

public enum RoundAutoChangeAction
{
    None,
    StartVote,
    ChangeMap,
}

public static class RoundAutoChangePolicy
{
    public static RoundAutoChangeAction Evaluate(
        int completedRounds,
        int voteStartRound,
        int changeAfterRound,
        bool voteOrNextMapAlreadyExists)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(completedRounds);
        ArgumentOutOfRangeException.ThrowIfLessThan(voteStartRound, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(changeAfterRound);

        if (changeAfterRound > 0 && completedRounds >= changeAfterRound)
        {
            return RoundAutoChangeAction.ChangeMap;
        }

        var currentRound = completedRounds + 1;
        return !voteOrNextMapAlreadyExists && currentRound >= voteStartRound
            ? RoundAutoChangeAction.StartVote
            : RoundAutoChangeAction.None;
    }
}
