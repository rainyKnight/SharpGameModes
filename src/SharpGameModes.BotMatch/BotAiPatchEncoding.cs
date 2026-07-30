using System.Buffers.Binary;

namespace SharpGameModes.BotMatch;

internal static class BotAiPatchEncoding
{
    public static bool TryParsePatch(
        string value,
        out byte[] bytes)
    {
        var tokens = value.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries);
        bytes = new byte[tokens.Length];
        for (var index = 0; index < tokens.Length; index++)
        {
            if (tokens[index] == "?"
                || !byte.TryParse(
                    tokens[index],
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out bytes[index]))
            {
                bytes = [];
                return false;
            }
        }

        return bytes.Length > 0;
    }

    public static bool MatchesExpected(
        ReadOnlySpan<byte> actual,
        string expected)
    {
        var tokens = expected.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries);
        if (actual.Length != tokens.Length)
        {
            return false;
        }

        for (var index = 0; index < tokens.Length; index++)
        {
            if (tokens[index] == "?")
            {
                continue;
            }

            if (!byte.TryParse(
                    tokens[index],
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var expectedByte)
                || actual[index] != expectedByte)
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryReadRelative32Target(
        nint instructionAddress,
        ReadOnlySpan<byte> instruction,
        int displacementOffset,
        int instructionLength,
        out nint target)
    {
        target = 0;
        if (instructionAddress == 0
            || displacementOffset < 0
            || instructionLength <= 0
            || displacementOffset + sizeof(int) > instruction.Length)
        {
            return false;
        }

        var displacement = BinaryPrimitives.ReadInt32LittleEndian(
            instruction.Slice(
                displacementOffset,
                sizeof(int)));
        target = instructionAddress
            + instructionLength
            + displacement;
        return target != 0;
    }

    public static bool TryWriteRelative32(
        Span<byte> instruction,
        int displacementOffset,
        nint instructionAddress,
        int instructionLength,
        nint target)
    {
        if (instructionAddress == 0
            || target == 0
            || displacementOffset < 0
            || instructionLength <= 0
            || displacementOffset + sizeof(int) > instruction.Length)
        {
            return false;
        }

        var displacement = (long)target
            - ((long)instructionAddress + instructionLength);
        if (displacement is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        BinaryPrimitives.WriteInt32LittleEndian(
            instruction.Slice(
                displacementOffset,
                sizeof(int)),
            (int)displacement);
        return true;
    }

    public static bool TryWriteRelative8(
        Span<byte> instruction,
        int displacementOffset,
        nint instructionAddress,
        int instructionLength,
        nint target)
    {
        if (instructionAddress == 0
            || target == 0
            || displacementOffset < 0
            || instructionLength <= 0
            || displacementOffset >= instruction.Length)
        {
            return false;
        }

        var displacement = (long)target
            - ((long)instructionAddress + instructionLength);
        if (displacement is < sbyte.MinValue or > sbyte.MaxValue)
        {
            return false;
        }

        instruction[displacementOffset] = unchecked(
            (byte)(sbyte)displacement);
        return true;
    }
}
