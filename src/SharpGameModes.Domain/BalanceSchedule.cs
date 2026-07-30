namespace SharpGameModes.Domain;

public static class BalanceSchedule
{
    public static bool RequiresRebalance(
        IReadOnlyCollection<string>? previousRoster,
        IEnumerable<string> currentRoster,
        int currentCounterTerrorists,
        int currentTerrorists,
        int counterTerroristRatio = 1,
        int terroristRatio = 1,
        int allowedCountDeviation = 0,
        bool preserveEngineTeamSwitch = false)
    {
        ArgumentNullException.ThrowIfNull(currentRoster);
        if (counterTerroristRatio <= 0 || terroristRatio <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(counterTerroristRatio), "Team ratios must be positive.");
        }

        var roster = currentRoster.ToHashSet(StringComparer.Ordinal);
        if (allowedCountDeviation is < 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(allowedCountDeviation));
        }

        var targetCt = TeamBalancer.CalculateTargetCounterTerroristCount(
            roster.Count,
            currentCounterTerrorists,
            counterTerroristRatio,
            terroristRatio);
        var targetT = roster.Count - targetCt;

        return previousRoster is null
            || !roster.SetEquals(previousRoster)
            || !preserveEngineTeamSwitch
            && (Math.Abs(currentCounterTerrorists - targetCt) > allowedCountDeviation
                || Math.Abs(currentTerrorists - targetT) > allowedCountDeviation);
    }
}
