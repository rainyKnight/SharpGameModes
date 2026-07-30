using SharpGameModes.Domain;

namespace SharpGameModes.Domain.Tests;

public sealed class RatingMatchTrackerTests
{
    private static readonly TrackedPlayer Ct1 = new(1, "CT 1", TrackedTeam.CounterTerrorist, true);
    private static readonly TrackedPlayer Ct2 = new(2, "CT 2", TrackedTeam.CounterTerrorist, true);
    private static readonly TrackedPlayer Ct3 = new(3, "CT 3", TrackedTeam.CounterTerrorist, true);
    private static readonly TrackedPlayer T1 = new(11, "T 1", TrackedTeam.Terrorist, true);
    private static readonly TrackedPlayer T2 = new(12, "T 2", TrackedTeam.Terrorist, true);

    [Fact]
    public void TracksDamageEntryAssistTradeAndKastLikeLegacyAtl()
    {
        var tracker = new RatingMatchTracker(5);
        tracker.StartRound([Ct1, Ct2, T1, T2]);

        tracker.RegisterDamage(Ct1, T1, 80);
        tracker.RegisterDeath(T1 with { IsAlive = false }, Ct1, Ct2, headshot: true, timestamp: 10);
        tracker.RegisterDeath(Ct1 with { IsAlive = false }, T2, null, headshot: false, timestamp: 13);
        tracker.EndRound(TrackedTeam.Terrorist, [Ct1, Ct2, T1, T2]);

        var completed = tracker.CompleteMatch().ToDictionary(player => player.SteamId);
        Assert.Equal(80, completed[Ct1.SteamId].Damage);
        Assert.Equal(1, completed[Ct1.SteamId].Kills);
        Assert.Equal(1, completed[Ct1.SteamId].Headshots);
        Assert.Equal(1, completed[Ct1.SteamId].EntryKills);
        Assert.Equal(1, completed[Ct2.SteamId].Assists);
        Assert.Equal(1, completed[T1.SteamId].EntryDeaths);
        Assert.Equal(1, completed[T1.SteamId].KastRounds);
        Assert.All(completed.Values, player => Assert.Equal(1, player.KastRounds));
    }

    [Fact]
    public void CountsOnlyFirstEntryAndOneMultiKillRound()
    {
        var tracker = new RatingMatchTracker();
        tracker.StartRound([Ct1, T1, T2]);
        tracker.RegisterDeath(T1 with { IsAlive = false }, Ct1, null, false, 1);
        tracker.RegisterDeath(T2 with { IsAlive = false }, Ct1, null, false, 2);
        tracker.EndRound(TrackedTeam.CounterTerrorist, [Ct1, T1, T2]);

        var completed = tracker.CompleteMatch().Single(player => player.SteamId == Ct1.SteamId);
        Assert.Equal(2, completed.Kills);
        Assert.Equal(1, completed.EntryKills);
        Assert.Equal(1, completed.MultiKillRounds);
    }

    [Fact]
    public void AwardsClutchOnlyWhenCandidateSurvivesAndTeamWins()
    {
        var tracker = new RatingMatchTracker();
        tracker.StartRound([Ct1, Ct2, Ct3, T1, T2]);
        tracker.RegisterDeath(Ct2 with { IsAlive = false }, T1, null, false, 1);
        tracker.RegisterDeath(Ct3 with { IsAlive = false }, T1, null, false, 2);
        tracker.RegisterDeath(T1 with { IsAlive = false }, Ct1, null, false, 3);
        tracker.RegisterDeath(T2 with { IsAlive = false }, Ct1, null, false, 4);
        tracker.EndRound(TrackedTeam.CounterTerrorist, [Ct1, Ct2, Ct3, T1, T2]);

        var completed = tracker.CompleteMatch().Single(player => player.SteamId == Ct1.SteamId);
        Assert.Equal(1, completed.ClutchesWon);
    }

    [Fact]
    public void TradeOutsideWindowDoesNotGrantKast()
    {
        var tracker = new RatingMatchTracker(5);
        tracker.StartRound([Ct1, T1, T2]);
        tracker.RegisterDeath(T1 with { IsAlive = false }, Ct1, null, false, 1);
        tracker.RegisterDeath(Ct1 with { IsAlive = false }, T2, null, false, 7);
        tracker.EndRound(TrackedTeam.Terrorist, [Ct1, T1, T2]);

        var completed = tracker.CompleteMatch().Single(player => player.SteamId == T1.SteamId);
        Assert.Equal(0, completed.KastRounds);
    }

    [Fact]
    public void DiscardMatchRemovesIncompleteRounds()
    {
        var tracker = new RatingMatchTracker();
        tracker.StartRound([Ct1, T1]);
        tracker.RegisterDeath(T1 with { IsAlive = false }, Ct1, null, false, 1);
        tracker.EndRound(TrackedTeam.CounterTerrorist, [Ct1, T1]);

        tracker.DiscardMatch();

        Assert.Empty(tracker.CompleteMatch());
        Assert.Equal(0, tracker.MatchPlayerCount);
        Assert.False(tracker.IsRoundLive);
    }
}
