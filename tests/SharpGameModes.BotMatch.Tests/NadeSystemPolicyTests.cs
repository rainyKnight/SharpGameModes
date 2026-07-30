using SharpGameModes.BotMatch;

namespace SharpGameModes.BotMatch.Tests;

public sealed class NadeSystemPolicyTests
{
    [Theory]
    [InlineData("off", 0)]
    [InlineData("less", 1)]
    [InlineData("normal", 2)]
    [InlineData("more", 3)]
    [InlineData("max", 4)]
    public void Modes_RoundTrip(string value, int expectedValue)
    {
        var expected = (BotNadeMode)expectedValue;
        Assert.True(NadeSystemPolicy.TryParseMode(value, out var mode));
        Assert.Equal(expected, mode);
        Assert.Equal(value, NadeSystemPolicy.FormatMode(mode));
    }

    [Fact]
    public void LessMode_EnforcesPerTypeAndTotalLimits()
    {
        var counter = new NadeRoundCounter(Flash: 1, Smoke: 1, HE: 0, Molotov: 0);

        Assert.True(NadeSystemPolicy.LessModeAllows(counter, "flash", flashLimit: 2));
        Assert.False(NadeSystemPolicy.LessModeAllows(counter, "smoke", flashLimit: 2));
        Assert.True(NadeSystemPolicy.LessModeAllows(counter, "he", flashLimit: 2));
        Assert.True(NadeSystemPolicy.LessModeAllows(counter, "incgrenade", flashLimit: 2));
        Assert.False(
            NadeSystemPolicy.LessModeAllows(
                new NadeRoundCounter(2, 1, 1, 0),
                "molotov",
                flashLimit: 2));
    }

    [Theory]
    [InlineData(0f, 100f, 0f, true)]
    [InlineData(0f, -100f, 0f, false)]
    [InlineData(90f, 0f, 100f, true)]
    [InlineData(90f, 0f, -100f, false)]
    [InlineData(45f, 0f, 0f, true)]
    public void DirectionGate_UsesForwardHemisphere(
        float yaw,
        float velocityX,
        float velocityY,
        bool expected)
        => Assert.Equal(
            expected,
            NadeSystemPolicy.FacesThrowDirection(yaw, velocityX, velocityY));

    [Theory]
    [InlineData(1, 1, 1f)]
    [InlineData(4, 5, 1f)]
    [InlineData(3, 4, 0.9f)]
    [InlineData(2, 3, 0.8f)]
    [InlineData(1, 5, 0.1f)]
    [InlineData(0, 5, 0f)]
    public void FlashRatio_MatchesUpstreamMatrix(
        int blindable,
        int total,
        float expected)
        => Assert.Equal(
            expected,
            NadeSystemPolicy.GetFlashRatioThreshold(blindable, total));

    [Theory]
    [InlineData(true, false, false, 800)]
    [InlineData(false, true, false, 500)]
    [InlineData(false, false, true, 1300)]
    [InlineData(false, false, false, 1200)]
    public void SpendCap_MatchesEconomyRules(
        bool pistol,
        bool poor,
        bool counterTerrorist,
        int expected)
        => Assert.Equal(
            expected,
            NadeSystemPolicy.GetRoundSpendCap(
                pistol,
                poor,
                counterTerrorist));
}
