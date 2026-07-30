namespace SharpGameModes.Domain;

public sealed record TeamDeathmatchScoreUpdate(
    bool Counted,
    int TerroristScore,
    int CounterTerroristScore,
    TeamAssignment? Winner)
{
    public int TerroristScoreBeforeRoundAward
        => Winner == TeamAssignment.Terrorist
            ? Math.Max(0, TerroristScore - 1)
            : TerroristScore;

    public int CounterTerroristScoreBeforeRoundAward
        => Winner == TeamAssignment.CounterTerrorist
            ? Math.Max(0, CounterTerroristScore - 1)
            : CounterTerroristScore;
}

public sealed class TeamDeathmatchScore
{
    public TeamDeathmatchScore(int scoreLimit)
    {
        if (scoreLimit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(scoreLimit));
        }

        ScoreLimit = scoreLimit;
    }

    public int ScoreLimit { get; }
    public int TerroristScore { get; private set; }
    public int CounterTerroristScore { get; private set; }
    public TeamAssignment? Winner { get; private set; }

    public TeamDeathmatchScoreUpdate RegisterKill(TeamAssignment killer, TeamAssignment victim)
    {
        if (Winner is not null || !IsPlayingTeam(killer) || !IsPlayingTeam(victim) || killer == victim)
        {
            return Snapshot(counted: false);
        }

        if (killer == TeamAssignment.CounterTerrorist)
        {
            CounterTerroristScore++;
        }
        else
        {
            TerroristScore++;
        }

        if (CounterTerroristScore >= ScoreLimit)
        {
            Winner = TeamAssignment.CounterTerrorist;
        }
        else if (TerroristScore >= ScoreLimit)
        {
            Winner = TeamAssignment.Terrorist;
        }

        return Snapshot(counted: true);
    }

    public void Reset()
    {
        TerroristScore = 0;
        CounterTerroristScore = 0;
        Winner = null;
    }

    private TeamDeathmatchScoreUpdate Snapshot(bool counted)
        => new(counted, TerroristScore, CounterTerroristScore, Winner);

    private static bool IsPlayingTeam(TeamAssignment team)
        => team is TeamAssignment.Terrorist or TeamAssignment.CounterTerrorist;
}
