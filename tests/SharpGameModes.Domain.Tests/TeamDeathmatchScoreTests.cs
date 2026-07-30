namespace SharpGameModes.Domain.Tests;

public sealed class TeamDeathmatchScoreTests
{
    [Fact]
    public void RegisterKill_CountsEnemyKillForKillerTeam()
    {
        var score = new TeamDeathmatchScore(100);

        var update = score.RegisterKill(TeamAssignment.CounterTerrorist, TeamAssignment.Terrorist);

        Assert.True(update.Counted);
        Assert.Equal(1, update.CounterTerroristScore);
        Assert.Equal(0, update.TerroristScore);
        Assert.Equal(1, update.CounterTerroristScoreBeforeRoundAward);
        Assert.Equal(0, update.TerroristScoreBeforeRoundAward);
        Assert.Null(update.Winner);
    }

    [Theory]
    [InlineData(TeamAssignment.CounterTerrorist, TeamAssignment.CounterTerrorist)]
    [InlineData(TeamAssignment.Terrorist, TeamAssignment.Terrorist)]
    [InlineData(TeamAssignment.Unassigned, TeamAssignment.Terrorist)]
    [InlineData(TeamAssignment.CounterTerrorist, TeamAssignment.Spectator)]
    public void RegisterKill_IgnoresNonEnemyKills(TeamAssignment killer, TeamAssignment victim)
    {
        var score = new TeamDeathmatchScore(100);

        var update = score.RegisterKill(killer, victim);

        Assert.False(update.Counted);
        Assert.Equal(0, update.CounterTerroristScore);
        Assert.Equal(0, update.TerroristScore);
    }

    [Fact]
    public void RegisterKill_LocksWinnerAtScoreLimit()
    {
        var score = new TeamDeathmatchScore(2);

        score.RegisterKill(TeamAssignment.Terrorist, TeamAssignment.CounterTerrorist);
        var winningUpdate = score.RegisterKill(TeamAssignment.Terrorist, TeamAssignment.CounterTerrorist);
        var ignoredUpdate = score.RegisterKill(TeamAssignment.CounterTerrorist, TeamAssignment.Terrorist);

        Assert.Equal(TeamAssignment.Terrorist, winningUpdate.Winner);
        Assert.Equal(1, winningUpdate.TerroristScoreBeforeRoundAward);
        Assert.Equal(0, winningUpdate.CounterTerroristScoreBeforeRoundAward);
        Assert.False(ignoredUpdate.Counted);
        Assert.Equal(2, ignoredUpdate.TerroristScore);
        Assert.Equal(0, ignoredUpdate.CounterTerroristScore);
    }

    [Fact]
    public void Reset_ClearsScoresAndWinner()
    {
        var score = new TeamDeathmatchScore(1);
        score.RegisterKill(TeamAssignment.CounterTerrorist, TeamAssignment.Terrorist);

        score.Reset();

        Assert.Equal(0, score.CounterTerroristScore);
        Assert.Equal(0, score.TerroristScore);
        Assert.Null(score.Winner);
    }
}
