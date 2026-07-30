using SharpGameModes.Domain;

namespace SharpGameModes.PlayerData.Tests;

public sealed class RatingCalculatorTests
{
    [Fact]
    public void Calculate_MatchesLegacyFormulaSnapshot()
    {
        var statistics = new CompletedMatchStatistics(
            RoundsPlayed: 10,
            Kills: 8,
            Assists: 3,
            Deaths: 6,
            Damage: 700,
            KastRounds: 7,
            MultiKillRounds: 2,
            ClutchesWon: 1,
            EntryKills: 2,
            EntryDeaths: 1);

        var result = RatingCalculator.Calculate(statistics);

        Assert.Equal(1.215854, result.Rating, precision: 7);
        Assert.Equal(1.495, result.Impact, precision: 10);
        Assert.Equal(0.7, result.Kast, precision: 10);
        Assert.Equal(70.0, result.Adr, precision: 10);
    }

    [Fact]
    public void Calculate_ClampsToConfiguredBounds()
    {
        var statistics = new CompletedMatchStatistics(1, 100, 0, 0, 10000, 1, 1, 1, 1, 0);

        var result = RatingCalculator.Calculate(statistics);

        Assert.Equal(3.0, result.Rating);
    }

    [Fact]
    public void Calculate_RejectsNegativeStatistics()
    {
        var statistics = new CompletedMatchStatistics(1, -1, 0, 0, 0, 0, 0, 0, 0, 0);

        Assert.Throws<ArgumentOutOfRangeException>(() => RatingCalculator.Calculate(statistics));
    }
}
