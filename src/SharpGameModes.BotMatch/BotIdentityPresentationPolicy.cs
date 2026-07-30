using System.Globalization;
using System.Text;

namespace SharpGameModes.BotMatch;

internal static class BotIdentityPresentationPolicy
{
    internal const int MaxAvatarBytes = 16 * 1024;

    private static ReadOnlySpan<byte> PngSignature =>
    [
        0x89,
        (byte)'P',
        (byte)'N',
        (byte)'G',
        0x0D,
        0x0A,
        0x1A,
        0x0A,
    ];

    internal static string NormalizePersonaName(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        var visibleElements = new List<string>();
        var elements = StringInfo.GetTextElementEnumerator(source);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (!IsInvisibleElement(element))
            {
                visibleElements.Add(element);
            }
        }

        var first = 0;
        while (first < visibleElements.Count
            && IsWhitespaceElement(visibleElements[first]))
        {
            first++;
        }

        var last = visibleElements.Count;
        while (last > first && IsWhitespaceElement(visibleElements[last - 1]))
        {
            last--;
        }

        var bounded = new List<string>();
        var utf8Bytes = 0;
        for (var index = first; index < last; index++)
        {
            var element = visibleElements[index];
            var elementBytes = Encoding.UTF8.GetByteCount(element);
            if (utf8Bytes + elementBytes > BotIdentityProfile.MaxNameUtf8Bytes)
            {
                break;
            }

            bounded.Add(element);
            utf8Bytes += elementBytes;
        }

        while (bounded.Count > 0
            && IsWhitespaceElement(bounded[^1]))
        {
            bounded.RemoveAt(bounded.Count - 1);
        }

        return string.Concat(bounded);
    }

    internal static bool TryNormalizeCrosshair(string? source, out string crosshair)
    {
        crosshair = source == "0" ? string.Empty : source?.Trim() ?? string.Empty;
        return Encoding.UTF8.GetByteCount(crosshair)
                   <= BotIdentityProfile.MaxCrosshairUtf8Bytes
            && (crosshair.Length == 0
                || crosshair.StartsWith("CSGO-", StringComparison.Ordinal));
    }

    internal static bool TryValidateAvatarBytes(
        ReadOnlySpan<byte> bytes,
        out string error)
    {
        if (bytes.Length == 0)
        {
            error = "avatar PNG is empty";
            return false;
        }
        if (bytes.Length > MaxAvatarBytes)
        {
            error = "avatar PNG must be 16 KiB or smaller";
            return false;
        }
        if (!bytes.StartsWith(PngSignature))
        {
            error = "avatar file is not a PNG";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsInvisibleElement(string element)
    {
        foreach (var rune in element.EnumerateRunes())
        {
            if (!IsInvisibleRune(rune))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsWhitespaceElement(string element)
    {
        var hasWhitespace = false;
        foreach (var rune in element.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                hasWhitespace = true;
                continue;
            }
            if (!IsInvisibleRune(rune))
            {
                return false;
            }
        }

        return hasWhitespace;
    }

    private static bool IsInvisibleRune(Rune rune)
        => Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.Surrogate
            or UnicodeCategory.OtherNotAssigned
            or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;
}
