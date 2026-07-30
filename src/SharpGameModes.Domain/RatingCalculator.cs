namespace SharpGameModes.Domain;

public sealed record RatingFormula(
    double KastCoefficient = 0.0073,
    double KillCoefficient = 0.3591,
    double DeathCoefficient = -0.5329,
    double ImpactCoefficient = 0.2372,
    double DamageCoefficient = 0.0032,
    double RatingIntercept = 0.1587,
    double ImpactKillCoefficient = 2.13,
    double ImpactAssistCoefficient = 0.42,
    double ImpactIntercept = -0.41,
    double MultiKillImpactBonus = 0.15,
    double ClutchWinImpactBonus = 0.25,
    double EntryKillImpactBonus = 0.15,
    double EntryDeathImpactPenalty = 0.10,
    double MinRating = 0.0,
    double MaxRating = 3.0);

public sealed record CompletedMatchStatistics(
    int RoundsPlayed,
    int Kills,
    int Assists,
    int Deaths,
    int Damage,
    int KastRounds,
    int MultiKillRounds,
    int ClutchesWon,
    int EntryKills,
    int EntryDeaths);

public sealed record MatchRating(
    double Rating,
    double Impact,
    double Kast,
    double Adr,
    double KillsPerRound,
    double DeathsPerRound,
    double AssistsPerRound);

public static class RatingCalculator
{
    public static MatchRating Calculate(CompletedMatchStatistics statistics, RatingFormula? formula = null)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        formula ??= new RatingFormula();
        Validate(statistics, formula);

        var rounds = Math.Max(1, statistics.RoundsPlayed);
        var killsPerRound = statistics.Kills / (double)rounds;
        var deathsPerRound = statistics.Deaths / (double)rounds;
        var assistsPerRound = statistics.Assists / (double)rounds;
        var adr = statistics.Damage / (double)rounds;
        var kastPercent = statistics.KastRounds * 100.0 / rounds;
        var impact = formula.ImpactKillCoefficient * killsPerRound
            + formula.ImpactAssistCoefficient * assistsPerRound
            + formula.ImpactIntercept
            + statistics.MultiKillRounds / (double)rounds * formula.MultiKillImpactBonus
            + statistics.ClutchesWon / (double)rounds * formula.ClutchWinImpactBonus
            + statistics.EntryKills / (double)rounds * formula.EntryKillImpactBonus
            - statistics.EntryDeaths / (double)rounds * formula.EntryDeathImpactPenalty;

        var rating = formula.KastCoefficient * kastPercent
            + formula.KillCoefficient * killsPerRound
            + formula.DeathCoefficient * deathsPerRound
            + formula.ImpactCoefficient * impact
            + formula.DamageCoefficient * adr
            + formula.RatingIntercept;

        return new MatchRating(
            Math.Clamp(rating, formula.MinRating, formula.MaxRating),
            impact,
            statistics.KastRounds / (double)rounds,
            adr,
            killsPerRound,
            deathsPerRound,
            assistsPerRound);
    }

    private static void Validate(CompletedMatchStatistics statistics, RatingFormula formula)
    {
        if (statistics.RoundsPlayed < 0
            || statistics.Kills < 0
            || statistics.Assists < 0
            || statistics.Deaths < 0
            || statistics.Damage < 0
            || statistics.KastRounds < 0
            || statistics.MultiKillRounds < 0
            || statistics.ClutchesWon < 0
            || statistics.EntryKills < 0
            || statistics.EntryDeaths < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(statistics), "Match statistics cannot be negative.");
        }

        if (formula.MinRating > formula.MaxRating
            || !double.IsFinite(formula.MinRating)
            || !double.IsFinite(formula.MaxRating))
        {
            throw new ArgumentException("Rating bounds are invalid.", nameof(formula));
        }
    }
}
