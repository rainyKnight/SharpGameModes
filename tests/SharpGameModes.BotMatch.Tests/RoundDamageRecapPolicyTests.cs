namespace SharpGameModes.BotMatch.Tests;

public sealed class RoundDamageRecapPolicyTests
{
    [Theory]
    [InlineData("low", "BOT 难度：简单 [1/4]")]
    [InlineData("medium", "BOT 难度：普通 [2/4]")]
    [InlineData("hltvtop10", "BOT 难度：困难 [3/4]")]
    [InlineData("hltvtop37", "BOT 难度：困难 [3/4]")]
    [InlineData("high", "BOT 难度：噩梦 [4/4]")]
    public void FormatDifficultyAnnouncement_UsesFourTiersAndChinese(
        string difficultyTier,
        string expected)
    {
        Assert.Equal(
            expected,
            RoundDamageRecapPolicy.FormatDifficultyAnnouncement(
                difficultyTier));
    }

    [Fact]
    public void FormatDifficultyAnnouncement_ReportsUnknownTier()
    {
        Assert.Equal(
            "BOT 难度：未知",
            RoundDamageRecapPolicy.FormatDifficultyAnnouncement("invalid"));
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("CLASSIC", "classic")]
    [InlineData("pw", "pw")]
    public void TryParseStyle_AcceptsUpstreamNames(
        string value,
        string expected)
    {
        Assert.True(RoundDamageRecapPolicy.TryParseStyle(value, out var actual));
        Assert.Equal(expected, RoundDamageRecapPolicy.GetStyleName(actual));
    }

    [Theory]
    [InlineData("schinese")]
    [InlineData("tchinese")]
    public void ResolveStyle_AutoUsesPerfectWorldForChineseLanguages(
        string language)
    {
        Assert.Equal(
            DamageRecapStyle.PerfectWorld,
            RoundDamageRecapPolicy.ResolveStyle(
                DamageRecapStyle.Auto,
                language,
                perfectWorld: false));
    }

    [Fact]
    public void ResolveStyle_AutoUsesPerfectWorldClientFlagAsFallback()
    {
        Assert.Equal(
            DamageRecapStyle.PerfectWorld,
            RoundDamageRecapPolicy.ResolveStyle(
                DamageRecapStyle.Auto,
                "english",
                perfectWorld: true));
    }

    [Fact]
    public void Tracker_AggregatesSortsAndUsesLastKnownDeadHealth()
    {
        var tracker = new RoundDamageRecapTracker();
        tracker.RegisterDamage(1, 2, 30, 70);
        tracker.RegisterDamage(1, 2, 80, 0);
        tracker.RegisterDamage(2, 1, 40, 60);
        tracker.RegisterDamage(1, 3, 15, 85);

        var lines = tracker.BuildLines(
            1,
            3,
            [
                new DamageRecapParticipant(1, "Human", 3, true, 60),
                new DamageRecapParticipant(2, "Bot Bravo", 2, false, 0),
                new DamageRecapParticipant(3, "Bot Alpha", 2, true, 85),
            ]);

        Assert.Collection(
            lines,
            line =>
            {
                Assert.Equal("Bot Bravo", line.EnemyName);
                Assert.Equal(new DamageRecapEntry(110, 2, 0), line.Dealt);
                Assert.Equal(new DamageRecapEntry(40, 1, 60), line.Taken);
                Assert.Equal(0, line.RemainingHealth);
            },
            line =>
            {
                Assert.Equal("Bot Alpha", line.EnemyName);
                Assert.Equal(85, line.RemainingHealth);
            });
    }

    [Fact]
    public void Tracker_RemovePlayerRemovesBothDamageDirections()
    {
        var tracker = new RoundDamageRecapTracker();
        tracker.RegisterDamage(1, 2, 30, 70);
        tracker.RegisterDamage(2, 1, 40, 60);

        tracker.RemovePlayer(2);

        Assert.Equal(DamageRecapEntry.Empty, tracker.GetDamage(1, 2));
        Assert.Equal(DamageRecapEntry.Empty, tracker.GetDamage(2, 1));
    }

    [Fact]
    public void FormatLine_ClassicMatchesUpstreamShape()
    {
        var line = new DamageRecapLine(
            "Bot",
            new DamageRecapEntry(110, 2, 0),
            new DamageRecapEntry(9, 1, 91),
            0);

        var text = RoundDamageRecapPolicy.FormatLine(
            line,
            DamageRecapStyle.Classic);

        Assert.Contains("Bot [DEAD]", text);
        Assert.Contains("Dealt to: [110 in 2 hits]", text);
        Assert.Contains("Taken from: [9 in 1 hit]", text);
    }

    [Fact]
    public void FormatLine_PerfectWorldMatchesUpstreamValues()
    {
        var line = new DamageRecapLine(
            "Bot",
            new DamageRecapEntry(50, 2, 50),
            new DamageRecapEntry(20, 1, 80),
            50);

        var text = RoundDamageRecapPolicy.FormatLine(
            line,
            DamageRecapStyle.PerfectWorld);

        Assert.Contains("命中", text);
        Assert.Contains("2", text);
        Assert.Contains("50", text);
        Assert.Contains("Bot", text);
    }
}
