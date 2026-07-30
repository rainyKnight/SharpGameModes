using SharpGameModes.Contracts;

namespace SharpGameModes.Domain.Tests;

public sealed class AutoTeamRuleTests
{
    private static readonly AutoTeamRuleDefaults Defaults = new(
        Enabled: true,
        LockTeamSelect: true,
        AutoAssignOnJoin: true,
        BalanceOnRoundStart: true,
        DisableNativeTeamBalance: true,
        CounterTerroristRatio: 1,
        TerroristRatio: 1,
        AllowedCountDeviation: 0,
        RoundRandomizeMode: RoundRandomizeMode.BalanceOnly,
        RoundStartBalanceDelaySeconds: 0.2,
        UsePlayerDataForBalance: true,
        BalanceHealthByRating: true);

    [Fact]
    public void Resolve_AppliesModeSnapshotOverDefaults()
    {
        var rule = AutoTeamRuleResolver.Resolve(
            Defaults,
            new AutoTeamRuleOverrides
            {
                CounterTerroristRatio = 1,
                TerroristRatio = 3,
                AllowedCountDeviation = 1,
                RoundRandomizeMode = "every_round",
            },
            playerDataAllowed: true,
            "ghost:example");

        Assert.Equal(1, rule.CounterTerroristRatio);
        Assert.Equal(3, rule.TerroristRatio);
        Assert.Equal(1, rule.AllowedCountDeviation);
        Assert.Equal(RoundRandomizeMode.EveryRound, rule.RoundRandomizeMode);
        Assert.True(rule.LockTeamSelect);
    }

    [Fact]
    public void Resolve_DisablesAllPlayerDataConsumersOnBlacklistedMap()
    {
        var rule = AutoTeamRuleResolver.Resolve(
            Defaults,
            new AutoTeamRuleOverrides
            {
                UsePlayerDataForBalance = true,
                BalanceHealthByRating = true,
            },
            playerDataAllowed: false,
            "classic:blacklisted");

        Assert.False(rule.UsePlayerDataForBalance);
        Assert.False(rule.BalanceHealthByRating);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("first_round")]
    public void Validate_RejectsUnknownRoundStrategy(string strategy)
    {
        var rule = new AutoTeamRuleOverrides { RoundRandomizeMode = strategy };

        Assert.Throws<InvalidDataException>(() => AutoTeamRuleResolver.Validate(rule, "test"));
    }
}
