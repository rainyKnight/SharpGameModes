namespace SharpGameModes.BotMatch;

internal enum BotAimMode
{
    Mixed,
    Head,
    Body,
}

internal readonly record struct BotAimCoordinates(float X, float Y, float Z);

internal static class BotAimPolicy
{
    private static readonly AimPoint[] AimPoints =
    [
        new("HEAD", 1.00f, 0f),
        new("NECK", 0.97f, 0f),
        new("JAW", 0.92f, 0f),
        new("CHEST", 0.82f, 0f),
        new("GUT", 0.67f, 0f),
        new("PELVIS", 0.60f, 0f),
        new("LEFT_CHEST", 0.82f, -8f),
        new("RIGHT_CHEST", 0.82f, 8f),
        new("LEFT_SHOULDER", 0.92f, -8f),
        new("RIGHT_SHOULDER", 0.92f, 8f),
        new("LEFT_GUT", 0.67f, -7f),
        new("RIGHT_GUT", 0.67f, 7f),
        new("LEFT_THIGH", 0.38f, -5f),
        new("RIGHT_THIGH", 0.38f, 5f),
        new("LEFT_SHIN", 0.15f, -5f),
        new("RIGHT_SHIN", 0.15f, 5f),
        new("FEET", 5f, 0f, true),
    ];

    private static readonly int[] HeadPriority =
    [
        0, 1, 2,
        3, 4, 5,
        6, 7, 10, 11,
        8, 9,
        12, 13, 14, 15,
        16,
    ];

    private static readonly int[] JawPriority =
    [
        2, 1, 0,
        3, 4, 5,
        6, 7, 10, 11,
        8, 9,
        12, 13, 14, 15,
        16,
    ];

    private static readonly int[] BodyPriority =
    [
        4, 5, 3,
        10, 11, 6, 7,
        8, 9,
        2, 1, 0,
        12, 13, 14, 15,
        16,
    ];

    public static bool TryParseMode(string? value, out BotAimMode mode)
    {
        mode = value?.Trim().ToLowerInvariant() switch
        {
            "head" => BotAimMode.Head,
            "body" => BotAimMode.Body,
            "mixed" => BotAimMode.Mixed,
            _ => (BotAimMode)(-1),
        };
        return mode is BotAimMode.Head or BotAimMode.Body or BotAimMode.Mixed;
    }

    public static string FormatMode(BotAimMode mode)
        => mode switch
        {
            BotAimMode.Head => "head",
            BotAimMode.Body => "body",
            _ => "mixed",
        };

    public static ReadOnlySpan<int> SelectPriority(BotAimMode mode, ushort itemDefinitionIndex)
        => mode switch
        {
            BotAimMode.Head when itemDefinitionIndex == 9 => BodyPriority,
            BotAimMode.Head => HeadPriority,
            BotAimMode.Body => BodyPriority,
            _ when IsBodyFirstWeapon(itemDefinitionIndex) => BodyPriority,
            _ => JawPriority,
        };

    public static string GetPointName(int index)
        => index is >= 0 and < 17 ? AimPoints[index].Name : "UNKNOWN";

    public static bool TryComputePoint(
        int index,
        float originX,
        float originY,
        float originZ,
        float eyeHeight,
        float yawDegrees,
        out BotAimCoordinates coordinates)
    {
        coordinates = default;
        if (index < 0 || index >= AimPoints.Length)
        {
            return false;
        }

        ref readonly var point = ref AimPoints[index];
        float x;
        float y;
        float z;
        if (point.AbsoluteRise)
        {
            x = originX;
            y = originY;
            z = originZ + point.Height;
        }
        else
        {
            var yawRadians = yawDegrees * MathF.PI / 180f;
            var rightX = MathF.Sin(yawRadians);
            var rightY = -MathF.Cos(yawRadians);
            x = originX + (rightX * point.Lateral);
            y = originY + (rightY * point.Lateral);
            z = originZ + (eyeHeight * point.Height);
        }

        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
        {
            return false;
        }

        coordinates = new BotAimCoordinates(x, y, z);
        return true;
    }

    private static bool IsBodyFirstWeapon(ushort itemDefinitionIndex)
        => itemDefinitionIndex is
            9   // AWP
            or 19  // P90
            or 25  // XM1014
            or 26  // PP-Bizon
            or 27  // MAG-7
            or 29  // Sawed-Off
            or 35  // Nova
            or 40  // SSG 08
            or 64; // R8 Revolver

    private readonly record struct AimPoint(
        string Name,
        float Height,
        float Lateral,
        bool AbsoluteRise = false);
}
