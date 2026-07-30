namespace SharpGameModes.BotMatch;

internal enum BotNadeMode
{
    Off,
    Less,
    Normal,
    More,
    Max,
}

internal readonly record struct NadeRoundCounter(
    int Flash,
    int Smoke,
    int HE,
    int Molotov)
{
    public int Total => Flash + Smoke + HE + Molotov;

    public NadeRoundCounter Increment(string grenadeType)
        => NormalizeType(grenadeType) switch
        {
            "flash" => this with { Flash = Flash + 1 },
            "smoke" => this with { Smoke = Smoke + 1 },
            "he" => this with { HE = HE + 1 },
            "molotov" => this with { Molotov = Molotov + 1 },
            _ => this,
        };

    private static string NormalizeType(string grenadeType)
        => grenadeType.Equals("incgrenade", StringComparison.OrdinalIgnoreCase)
            ? "molotov"
            : grenadeType.ToLowerInvariant();
}

internal static class NadeSystemPolicy
{
    public static bool TryParseMode(string? value, out BotNadeMode mode)
    {
        mode = value?.Trim().ToLowerInvariant() switch
        {
            "off" => BotNadeMode.Off,
            "less" => BotNadeMode.Less,
            "normal" => BotNadeMode.Normal,
            "more" => BotNadeMode.More,
            "max" => BotNadeMode.Max,
            _ => (BotNadeMode)(-1),
        };
        return Enum.IsDefined(mode);
    }

    public static string FormatMode(BotNadeMode mode)
        => mode switch
        {
            BotNadeMode.Off => "off",
            BotNadeMode.Less => "less",
            BotNadeMode.Normal => "normal",
            BotNadeMode.More => "more",
            BotNadeMode.Max => "max",
            _ => "normal",
        };

    public static bool LessModeAllows(
        NadeRoundCounter counter,
        string grenadeType,
        int flashLimit)
    {
        if (counter.Total >= 4)
        {
            return false;
        }

        return NormalizeType(grenadeType) switch
        {
            "flash" => counter.Flash < Math.Max(0, flashLimit),
            "smoke" => counter.Smoke < 1,
            "he" => counter.HE < 1,
            "molotov" => counter.Molotov < 1,
            _ => false,
        };
    }

    public static bool FacesThrowDirection(
        float eyeYaw,
        float velocityX,
        float velocityY)
    {
        var velocityLength = MathF.Sqrt(
            (velocityX * velocityX) + (velocityY * velocityY));
        if (velocityLength <= 0f)
        {
            return true;
        }

        var yawRadians = eyeYaw * MathF.PI / 180f;
        var dot = (MathF.Cos(yawRadians) * velocityX / velocityLength)
            + (MathF.Sin(yawRadians) * velocityY / velocityLength);
        return dot >= 0f;
    }

    public static float GetFlashRatioThreshold(int blindable, int total)
    {
        if (total <= 0 || blindable <= 0)
        {
            return 0f;
        }

        if (blindable == total || (blindable == 4 && total == 5))
        {
            return 1f;
        }

        return (blindable, total) switch
        {
            (3, 4) => 0.9f,
            (2, 3) => 0.8f,
            (3, 5) => 0.7f,
            (2, 4) or (1, 2) => 0.6f,
            (2, 5) => 0.5f,
            (1, 3) => 0.3f,
            (1, 4) => 0.2f,
            (1, 5) => 0.1f,
            _ => 0f,
        };
    }

    public static int GetRoundSpendCap(
        bool pistolRound,
        bool poor,
        bool counterTerrorist)
    {
        if (pistolRound)
        {
            return 800;
        }

        if (poor)
        {
            return 500;
        }

        return counterTerrorist ? 1_300 : 1_200;
    }

    public static float GetNoInformationProbability(
        BotNadeMode mode,
        string grenadeType)
    {
        var normalized = NormalizeType(grenadeType);
        if (normalized == "flash")
        {
            return mode == BotNadeMode.More ? 1f : 0.8f;
        }

        if (normalized == "he")
        {
            return mode == BotNadeMode.More ? 0.5f : 0.2f;
        }

        if (normalized == "molotov")
        {
            return mode == BotNadeMode.More ? 0.8f : 0.6f;
        }

        return 1f;
    }

    public static string NormalizeType(string grenadeType)
        => grenadeType.Equals("incgrenade", StringComparison.OrdinalIgnoreCase)
            ? "molotov"
            : grenadeType.ToLowerInvariant();
}
