namespace SharpGameModes.Domain;

public enum ZombieRoundOutcome
{
    None,
    HumansWin,
    ZombiesWin,
}

public static class ZombieRoundRules
{
    public static int CalculateInitialZombieCount(
        int playerCount,
        int minimumInitialZombies,
        double initialZombieRatio,
        int maximumInitialZombies)
    {
        if (playerCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(playerCount), "At least two players are required.");
        }

        if (minimumInitialZombies < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumInitialZombies));
        }

        if (!double.IsFinite(initialZombieRatio) || initialZombieRatio <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialZombieRatio));
        }

        if (maximumInitialZombies < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumInitialZombies));
        }

        var ratioCount = (int)Math.Ceiling(playerCount * initialZombieRatio);
        var count = Math.Max(minimumInitialZombies, ratioCount);
        if (maximumInitialZombies > 0)
        {
            count = Math.Min(count, maximumInitialZombies);
        }

        return Math.Clamp(count, 1, playerCount - 1);
    }

    public static ZombieRoundOutcome Evaluate(
        int aliveHumans,
        int activeZombies,
        int pendingCorpseInfections,
        int secondsRemaining)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(aliveHumans);
        ArgumentOutOfRangeException.ThrowIfNegative(activeZombies);
        ArgumentOutOfRangeException.ThrowIfNegative(pendingCorpseInfections);

        if (secondsRemaining <= 0)
        {
            return ZombieRoundOutcome.HumansWin;
        }

        if (aliveHumans == 0 && activeZombies > 0 && pendingCorpseInfections == 0)
        {
            return ZombieRoundOutcome.ZombiesWin;
        }

        if (activeZombies == 0 && aliveHumans > 0)
        {
            return ZombieRoundOutcome.HumansWin;
        }

        return ZombieRoundOutcome.None;
    }
}
