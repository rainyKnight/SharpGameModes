namespace SharpGameModes.BotMatch;

internal enum BotFireMovement
{
    Unchanged,
    CapAt70,
    Stop,
}

internal static class BotStatePolicy
{
    public static BotFireMovement GetFireMovement(ushort itemDefinitionIndex)
        => itemDefinitionIndex switch
        {
            3 or 4 or 17 or 30 or 32 or 34 or 36 or 63 => BotFireMovement.CapAt70,
            1 or 7 or 8 or 9 or 10 or 11 or 13 or 14 or 16 or 28 or 38 or 39 or 40 or 60 or 61
                => BotFireMovement.Stop,
            _ => BotFireMovement.Unchanged,
        };

    public static double GetCombatCrouchChance(ushort itemDefinitionIndex)
        => itemDefinitionIndex switch
        {
            3 or 4 or 32 or 36 => 0.20,
            1 or 61 => 0.30,
            2 or 11 or 30 or 38 or 63 or 64 => 0.10,
            17 or 26 or 34 => 0.03,
            9 or 19 or 23 or 24 or 25 or 27 or 29 or 35 or 40 => 0.05,
            7 or 8 or 10 or 13 or 14 or 16 or 39 or 60 => 0.50,
            28 => 0.90,
            _ => 0.0,
        };

    public static bool ShouldRecoverStuck(
        float elapsedSeconds,
        float maximumSpeed,
        float displacement)
        => (elapsedSeconds >= 1f && maximumSpeed <= 10f)
            || (elapsedSeconds >= 3f && maximumSpeed > 10f && displacement < 75f);

    public static bool IsNearLadder(
        bool ladderMoveType,
        float ladderNormalX,
        float ladderNormalY,
        float ladderNormalZ)
        => ladderMoveType
            || ladderNormalX != 0f
            || ladderNormalY != 0f
            || ladderNormalZ != 0f;

    public static double GetFlashAvoidChance(float millisecondsLeft)
        => millisecondsLeft switch
        {
            <= 150f => 0.05,
            <= 250f => 0.20,
            <= 400f => 0.50,
            <= 600f => 0.90,
            _ => 0.95,
        };

    public static bool IsWithinFlashFov(
        float eyeX,
        float eyeY,
        float eyeZ,
        float eyePitch,
        float eyeYaw,
        float targetX,
        float targetY,
        float targetZ,
        float horizontalDegrees = 110f,
        float verticalDegrees = 90f)
    {
        var dx = targetX - eyeX;
        var dy = targetY - eyeY;
        var dz = targetZ - eyeZ;
        var horizontalDistance = MathF.Sqrt((dx * dx) + (dy * dy));
        if (horizontalDistance < 0.001f && MathF.Abs(dz) < 0.001f)
        {
            return true;
        }

        var yawToTarget = MathF.Atan2(dy, dx) * 180f / MathF.PI;
        var pitchToTarget = -MathF.Atan2(dz, horizontalDistance) * 180f / MathF.PI;
        var yawDelta = NormalizeAngle(yawToTarget - eyeYaw);
        var pitchDelta = NormalizeAngle(pitchToTarget - eyePitch);
        return MathF.Abs(yawDelta) <= horizontalDegrees * 0.5f
            && MathF.Abs(pitchDelta) <= verticalDegrees * 0.5f;
    }

    private static float NormalizeAngle(float angle)
    {
        angle %= 360f;
        if (angle > 180f)
        {
            angle -= 360f;
        }
        else if (angle < -180f)
        {
            angle += 360f;
        }

        return angle;
    }
}
