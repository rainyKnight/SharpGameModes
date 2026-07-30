using SharpGameModes.BotMatch;

namespace SharpGameModes.BotMatch.Tests;

public sealed class BotStatePolicyTests
{
    [Theory]
    [InlineData(4, 1)]
    [InlineData(34, 1)]
    [InlineData(7, 2)]
    [InlineData(9, 2)]
    [InlineData(25, 0)]
    public void FireMovement_MatchesUpstreamWeaponGroups(
        ushort itemDefinitionIndex,
        int expected)
        => Assert.Equal(
            (BotFireMovement)expected,
            BotStatePolicy.GetFireMovement(itemDefinitionIndex));

    [Theory]
    [InlineData(28, 0.90)]
    [InlineData(7, 0.50)]
    [InlineData(1, 0.30)]
    [InlineData(4, 0.20)]
    [InlineData(64, 0.10)]
    [InlineData(9, 0.05)]
    [InlineData(17, 0.03)]
    [InlineData(42, 0.0)]
    public void CrouchChance_MatchesUpstreamWeaponGroups(
        ushort itemDefinitionIndex,
        double expected)
        => Assert.Equal(expected, BotStatePolicy.GetCombatCrouchChance(itemDefinitionIndex));

    [Theory]
    [InlineData(1.0f, 10f, 500f, true)]
    [InlineData(3.0f, 50f, 74.9f, true)]
    [InlineData(2.9f, 50f, 10f, false)]
    [InlineData(3.0f, 50f, 75f, false)]
    public void StuckRecovery_UsesTimeSpeedAndDisplacementThresholds(
        float elapsed,
        float maximumSpeed,
        float displacement,
        bool expected)
        => Assert.Equal(
            expected,
            BotStatePolicy.ShouldRecoverStuck(elapsed, maximumSpeed, displacement));

    [Theory]
    [InlineData(true, 0f, 0f, 0f, true)]
    [InlineData(false, 1f, 0f, 0f, true)]
    [InlineData(false, 0f, -1f, 0f, true)]
    [InlineData(false, 0f, 0f, 1f, true)]
    [InlineData(false, 0f, 0f, 0f, false)]
    public void NearLadder_MatchesUpstreamMoveTypeAndNormalChecks(
        bool ladderMoveType,
        float ladderNormalX,
        float ladderNormalY,
        float ladderNormalZ,
        bool expected)
        => Assert.Equal(
            expected,
            BotStatePolicy.IsNearLadder(
                ladderMoveType,
                ladderNormalX,
                ladderNormalY,
                ladderNormalZ));

    [Theory]
    [InlineData(150f, 0.05)]
    [InlineData(150.1f, 0.20)]
    [InlineData(250f, 0.20)]
    [InlineData(250.1f, 0.50)]
    [InlineData(400f, 0.50)]
    [InlineData(400.1f, 0.90)]
    [InlineData(600f, 0.90)]
    [InlineData(600.1f, 0.95)]
    public void FlashAvoidChance_MatchesUpstreamTimeTiers(
        float millisecondsLeft,
        double expected)
        => Assert.Equal(expected, BotStatePolicy.GetFlashAvoidChance(millisecondsLeft));

    [Theory]
    [InlineData(0f, 0f, 1f, 0f, 0f, true)]
    [InlineData(0f, 0f, 0.5735764f, 0.8191520f, 0f, true)]
    [InlineData(0f, 0f, 0.5591929f, 0.8290375f, 0f, false)]
    [InlineData(0f, 0f, -1f, 0f, 0f, false)]
    [InlineData(0f, 0f, 1f, 0f, 1f, true)]
    [InlineData(0f, 0f, 1f, 0f, 1.0355303f, false)]
    [InlineData(0f, 179f, -0.9998477f, -0.0174524f, 0f, true)]
    public void FlashFov_UsesUpstreamHorizontalAndVerticalLimits(
        float eyePitch,
        float eyeYaw,
        float targetX,
        float targetY,
        float targetZ,
        bool expected)
        => Assert.Equal(
            expected,
            BotStatePolicy.IsWithinFlashFov(
                0f,
                0f,
                0f,
                eyePitch,
                eyeYaw,
                targetX,
                targetY,
                targetZ));
}
