namespace SharpGameModes.BotMatch.Tests;

public sealed class BotProfileValidationPolicyTests
{
    [Fact]
    public void ExactSelectedSource_AllowsCuratedDatabaseBelow64KiB()
    {
        Assert.True(
            BotProfileValidationPolicy.TryValidate(
                resolvedDatabaseBytes: 26_914,
                expectedDatabaseBytes: 26_914,
                out var error));
        Assert.Empty(error);
    }

    [Fact]
    public void ExactSelectedSource_RejectsDifferentResolvedDatabase()
    {
        Assert.False(
            BotProfileValidationPolicy.TryValidate(
                resolvedDatabaseBytes: 26_913,
                expectedDatabaseBytes: 26_914,
                out var error));
        Assert.Contains("selected source contains 26914 bytes", error);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(26_914, false)]
    [InlineData(65_535, false)]
    [InlineData(65_536, true)]
    public void VpkOnlyFallback_KeepsConservativeMinimum(
        int resolvedDatabaseBytes,
        bool expected)
    {
        Assert.Equal(
            expected,
            BotProfileValidationPolicy.TryValidate(
                resolvedDatabaseBytes,
                expectedDatabaseBytes: 0,
                out _));
    }
}
