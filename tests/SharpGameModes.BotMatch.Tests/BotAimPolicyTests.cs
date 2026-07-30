using SharpGameModes.BotMatch;

namespace SharpGameModes.BotMatch.Tests;

public sealed class BotAimPolicyTests
{
    [Theory]
    [InlineData("mixed", 0)]
    [InlineData(" HEAD ", 1)]
    [InlineData("Body", 2)]
    public void TryParseMode_AcceptsSupportedValues(string value, int expected)
    {
        Assert.True(BotAimPolicy.TryParseMode(value, out var actual));
        Assert.Equal((BotAimMode)expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData(null)]
    public void TryParseMode_RejectsUnsupportedValues(string? value)
        => Assert.False(BotAimPolicy.TryParseMode(value, out _));

    [Fact]
    public void MixedMode_UsesBodyPriorityForSpreadWeaponsAndJawForRifles()
    {
        Assert.Equal(4, BotAimPolicy.SelectPriority(BotAimMode.Mixed, 9)[0]);
        Assert.Equal(4, BotAimPolicy.SelectPriority(BotAimMode.Mixed, 35)[0]);
        Assert.Equal(2, BotAimPolicy.SelectPriority(BotAimMode.Mixed, 7)[0]);
    }

    [Fact]
    public void HeadMode_KeepsUpstreamAwpBodyException()
    {
        Assert.Equal(4, BotAimPolicy.SelectPriority(BotAimMode.Head, 9)[0]);
        Assert.Equal(0, BotAimPolicy.SelectPriority(BotAimMode.Head, 7)[0]);
    }

    [Fact]
    public void ComputePoint_RotatesLateralOffsetInEnemyLocalFrame()
    {
        Assert.True(BotAimPolicy.TryComputePoint(
            7,
            100f,
            200f,
            10f,
            64f,
            90f,
            out var point));

        Assert.Equal(108f, point.X, 3);
        Assert.Equal(200f, point.Y, 3);
        Assert.Equal(62.48f, point.Z, 3);
    }

    [Fact]
    public void ComputePoint_FeetUsesAbsoluteRise()
    {
        Assert.True(BotAimPolicy.TryComputePoint(
            16,
            100f,
            200f,
            10f,
            64f,
            45f,
            out var point));

        Assert.Equal(new BotAimCoordinates(100f, 200f, 15f), point);
    }
}
