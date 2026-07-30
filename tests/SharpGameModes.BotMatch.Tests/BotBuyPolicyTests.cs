using SharpGameModes.BotMatch;

namespace SharpGameModes.BotMatch.Tests;

public sealed class BotBuyPolicyTests
{
    [Theory]
    [InlineData("weapon_ak47", BotBuyTeam.Terrorist, 2700)]
    [InlineData("weapon_m4a1", BotBuyTeam.CounterTerrorist, 2900)]
    [InlineData("weapon_awp", BotBuyTeam.Terrorist, 4750)]
    [InlineData("weapon_awp", BotBuyTeam.CounterTerrorist, 4750)]
    [InlineData("item_defuser", BotBuyTeam.CounterTerrorist, 400)]
    public void PurchaseTableMatchesUpstream(
        string item,
        BotBuyTeam team,
        int expectedPrice)
    {
        Assert.True(
            BotBuyPolicy.TryGetPurchasePrice(
                item,
                team,
                armor: 0,
                out var price));
        Assert.Equal(expectedPrice, price);
    }

    [Theory]
    [InlineData("weapon_ak47", BotBuyTeam.CounterTerrorist)]
    [InlineData("weapon_m4a1", BotBuyTeam.Terrorist)]
    [InlineData("weapon_tec9", BotBuyTeam.CounterTerrorist)]
    [InlineData("item_defuser", BotBuyTeam.Terrorist)]
    public void TeamRestrictedPurchasesAreRejected(
        string item,
        BotBuyTeam team)
        => Assert.False(
            BotBuyPolicy.TryGetPurchasePrice(
                item,
                team,
                armor: 0,
                out _));

    [Fact]
    public void AssaultSuitUpgradeCostsOnlyHelmetDifference()
    {
        Assert.True(
            BotBuyPolicy.TryGetPurchasePrice(
                "item_assaultsuit",
                BotBuyTeam.CounterTerrorist,
                armor: 100,
                out var price));
        Assert.Equal(350, price);
    }

    [Fact]
    public void DefinitionMapCoversEveryPurchasableWeapon()
    {
        ushort[] definitions =
        [
            1, 2, 3, 4, 7, 8, 9, 10, 11, 13, 14, 16, 17, 19, 23,
            24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 38,
            39, 40, 60, 61, 63, 64,
        ];

        Assert.All(
            definitions,
            definition => Assert.True(
                BotBuyPolicy.TryGetWeaponName(definition, out _)));
    }

    [Theory]
    [InlineData("weapon_ak47", true)]
    [InlineData("weapon_mp9", true)]
    [InlineData("weapon_nova", true)]
    [InlineData("weapon_deagle", false)]
    [InlineData("weapon_knife", false)]
    public void PrimaryClassificationMatchesUpstream(
        string weapon,
        bool expected)
        => Assert.Equal(expected, BotBuyPolicy.IsPrimaryWeapon(weapon));

    [Theory]
    [InlineData(0, true)]
    [InlineData(12, true)]
    [InlineData(24, true)]
    [InlineData(27, true)]
    [InlineData(30, true)]
    [InlineData(1, false)]
    [InlineData(25, false)]
    public void FirstRoundDetectionIncludesRegulationAndOvertimeHalves(
        int roundsPlayed,
        bool expected)
        => Assert.Equal(
            expected,
            BotBuyPolicy.IsFirstRoundOfHalf(
                roundsPlayed,
                maxRounds: 24,
                overtimeMaxRounds: 6));

    [Theory]
    [InlineData(10, true)]
    [InlineData(22, true)]
    [InlineData(9, false)]
    [InlineData(23, false)]
    public void SecondToLastRoundDetectionMatchesUpstream(
        int roundsPlayed,
        bool expected)
        => Assert.Equal(
            expected,
            BotBuyPolicy.IsSecondToLastRoundOfHalf(
                roundsPlayed,
                maxRounds: 24));
}
