namespace SharpGameModes.Domain;

public enum TeamAssignment
{
    Unassigned,
    Spectator,
    Terrorist,
    CounterTerrorist,
}

public sealed record PlayerBalanceCandidate(string Id, double Rating, TeamAssignment CurrentTeam);

public sealed record TeamBalancePlan(
    IReadOnlyDictionary<string, TeamAssignment> Assignments,
    double CounterTerroristRating,
    double TerroristRating)
{
    public int CounterTerroristCount => Assignments.Count(pair => pair.Value == TeamAssignment.CounterTerrorist);
    public int TerroristCount => Assignments.Count(pair => pair.Value == TeamAssignment.Terrorist);
}

public static class TeamBalancer
{
    public static TeamBalancePlan CreatePlan(
        IEnumerable<PlayerBalanceCandidate> players,
        int counterTerroristRatio = 1,
        int terroristRatio = 1)
    {
        ArgumentNullException.ThrowIfNull(players);
        if (counterTerroristRatio <= 0 || terroristRatio <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(counterTerroristRatio), "Team ratios must be positive.");
        }

        var active = players
            .Where(player => player.CurrentTeam is TeamAssignment.CounterTerrorist or TeamAssignment.Terrorist)
            .OrderByDescending(player => NormalizeRating(player.Rating))
            .ThenBy(player => player.Id, StringComparer.Ordinal)
            .ToArray();

        var currentCt = active.Count(player => player.CurrentTeam == TeamAssignment.CounterTerrorist);
        var targetCt = CalculateTargetCounterTerroristCount(
            active.Length,
            currentCt,
            counterTerroristRatio,
            terroristRatio);
        var targetT = active.Length - targetCt;

        var assignments = new Dictionary<string, TeamAssignment>(StringComparer.Ordinal);
        var ctCount = 0;
        var tCount = 0;
        var ctRating = 0.0;
        var tRating = 0.0;

        foreach (var player in active)
        {
            var rating = NormalizeRating(player.Rating);
            var assignCt = tCount >= targetT
                || (ctCount < targetCt && (ctRating < tRating || (ctRating.Equals(tRating) && ctCount <= tCount)));

            if (assignCt)
            {
                assignments.Add(player.Id, TeamAssignment.CounterTerrorist);
                ctCount++;
                ctRating += rating;
            }
            else
            {
                assignments.Add(player.Id, TeamAssignment.Terrorist);
                tCount++;
                tRating += rating;
            }
        }

        OptimizeRatingByPairSwaps(active, assignments);
        return CreateResult(active, assignments);
    }

    public static TeamBalancePlan CreateMinimumMovementPlan(
        IEnumerable<PlayerBalanceCandidate> players,
        int counterTerroristRatio = 1,
        int terroristRatio = 1,
        int allowedCountDeviation = 0)
    {
        ArgumentNullException.ThrowIfNull(players);
        if (counterTerroristRatio <= 0 || terroristRatio <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(counterTerroristRatio), "Team ratios must be positive.");
        }

        if (allowedCountDeviation is < 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(allowedCountDeviation),
                "Allowed count deviation must be between 0 and 64.");
        }

        var active = players
            .Where(player => player.CurrentTeam is TeamAssignment.CounterTerrorist or TeamAssignment.Terrorist)
            .OrderBy(player => player.Id, StringComparer.Ordinal)
            .ToArray();
        var assignments = active.ToDictionary(
            player => player.Id,
            player => player.CurrentTeam,
            StringComparer.Ordinal);
        var currentCt = active.Count(player => player.CurrentTeam == TeamAssignment.CounterTerrorist);
        var targetCt = CalculateTargetCounterTerroristCount(
            active.Length,
            currentCt,
            counterTerroristRatio,
            terroristRatio);
        var overflow = currentCt - targetCt;
        var movesRequired = Math.Max(0, Math.Abs(overflow) - allowedCountDeviation);
        var sourceTeam = overflow > 0
            ? TeamAssignment.CounterTerrorist
            : TeamAssignment.Terrorist;
        var targetTeam = overflow > 0
            ? TeamAssignment.Terrorist
            : TeamAssignment.CounterTerrorist;

        for (var move = 0; move < movesRequired; move++)
        {
            var best = active
                .Where(player => assignments[player.Id] == sourceTeam)
                .OrderBy(player => RatingDifferenceAfterMove(active, assignments, player.Id, targetTeam))
                .ThenBy(player => player.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (best is null)
            {
                break;
            }

            assignments[best.Id] = targetTeam;
        }

        return CreateResult(active, assignments);
    }

    public static int CalculateTargetCounterTerroristCount(
        int totalPlayers,
        int currentCounterTerrorists,
        int counterTerroristRatio = 1,
        int terroristRatio = 1)
    {
        if (totalPlayers < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalPlayers));
        }

        if (counterTerroristRatio <= 0 || terroristRatio <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(counterTerroristRatio), "Team ratios must be positive.");
        }

        if (totalPlayers == 0)
        {
            return 0;
        }

        var desiredCt = totalPlayers * counterTerroristRatio
            / (double)(counterTerroristRatio + terroristRatio);
        var minimumCt = totalPlayers >= 2 ? 1 : 0;
        var maximumCt = totalPlayers >= 2 ? totalPlayers - 1 : totalPlayers;
        return Enumerable.Range(minimumCt, maximumCt - minimumCt + 1)
            .OrderBy(candidate => Math.Abs(candidate - desiredCt))
            .ThenBy(candidate => Math.Abs(candidate - currentCounterTerrorists))
            .ThenBy(candidate => candidate)
            .First();
    }

    private static double RatingDifferenceAfterMove(
        IReadOnlyCollection<PlayerBalanceCandidate> players,
        IReadOnlyDictionary<string, TeamAssignment> assignments,
        string movingPlayerId,
        TeamAssignment targetTeam)
    {
        var ctCount = 0;
        var tCount = 0;
        var ctRating = 0.0;
        var tRating = 0.0;
        foreach (var player in players)
        {
            var team = player.Id == movingPlayerId ? targetTeam : assignments[player.Id];
            if (team == TeamAssignment.CounterTerrorist)
            {
                ctCount++;
                ctRating += NormalizeRating(player.Rating);
            }
            else
            {
                tCount++;
                tRating += NormalizeRating(player.Rating);
            }
        }

        return ctCount == 0 || tCount == 0
            ? double.MaxValue
            : Math.Abs(ctRating / ctCount - tRating / tCount);
    }

    private static TeamBalancePlan CreateResult(
        IReadOnlyCollection<PlayerBalanceCandidate> players,
        IReadOnlyDictionary<string, TeamAssignment> assignments)
    {
        var ctRating = players
            .Where(player => assignments[player.Id] == TeamAssignment.CounterTerrorist)
            .Sum(player => NormalizeRating(player.Rating));
        var tRating = players
            .Where(player => assignments[player.Id] == TeamAssignment.Terrorist)
            .Sum(player => NormalizeRating(player.Rating));
        return new TeamBalancePlan(assignments, ctRating, tRating);
    }

    private static void OptimizeRatingByPairSwaps(
        IReadOnlyCollection<PlayerBalanceCandidate> players,
        IDictionary<string, TeamAssignment> assignments)
    {
        while (true)
        {
            var counterTerrorists = players
                .Where(player => assignments[player.Id] == TeamAssignment.CounterTerrorist)
                .OrderBy(player => player.Id, StringComparer.Ordinal)
                .ToArray();
            var terrorists = players
                .Where(player => assignments[player.Id] == TeamAssignment.Terrorist)
                .OrderBy(player => player.Id, StringComparer.Ordinal)
                .ToArray();
            if (counterTerrorists.Length == 0 || terrorists.Length == 0)
            {
                return;
            }

            var ctRating = counterTerrorists.Sum(player => NormalizeRating(player.Rating));
            var tRating = terrorists.Sum(player => NormalizeRating(player.Rating));
            var currentDifference = Math.Abs(
                ctRating / counterTerrorists.Length - tRating / terrorists.Length);
            (PlayerBalanceCandidate Ct, PlayerBalanceCandidate T, double Difference)? best = null;

            foreach (var counterTerrorist in counterTerrorists)
            {
                foreach (var terrorist in terrorists)
                {
                    var counterTerroristRating = NormalizeRating(counterTerrorist.Rating);
                    var terroristRating = NormalizeRating(terrorist.Rating);
                    var difference = Math.Abs(
                        (ctRating - counterTerroristRating + terroristRating)
                            / counterTerrorists.Length
                        - (tRating - terroristRating + counterTerroristRating)
                            / terrorists.Length);
                    if (difference + 1e-12 >= currentDifference
                        || best is not null && difference + 1e-12 >= best.Value.Difference)
                    {
                        continue;
                    }

                    best = (counterTerrorist, terrorist, difference);
                }
            }

            if (best is null)
            {
                return;
            }

            assignments[best.Value.Ct.Id] = TeamAssignment.Terrorist;
            assignments[best.Value.T.Id] = TeamAssignment.CounterTerrorist;
        }
    }

    private static double NormalizeRating(double rating)
        => double.IsFinite(rating) && rating > 0 ? rating : 1.0;
}
