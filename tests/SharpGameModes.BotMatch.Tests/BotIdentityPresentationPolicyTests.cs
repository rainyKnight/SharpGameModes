using System.Text;

namespace SharpGameModes.BotMatch.Tests;

public sealed class BotIdentityPresentationPolicyTests
{
    [Fact]
    public void NormalizePersonaName_MatchesUpstreamVisibilityAndWhitespaceRules()
    {
        var value = BotIdentityPresentationPolicy.NormalizePersonaName(
            "\u200B \tA\u0301lice\u0007 \r\n");

        Assert.Equal("A\u0301lice", value);
    }

    [Fact]
    public void NormalizePersonaName_TruncatesAtCompleteUnicodeTextElement()
    {
        var value = BotIdentityPresentationPolicy.NormalizePersonaName(
            "12345678901234567890123456789😀X");

        Assert.Equal("12345678901234567890123456789", value);
        Assert.Equal(29, Encoding.UTF8.GetByteCount(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u200B\u0007")]
    [InlineData("\u0301")]
    public void NormalizePersonaName_RejectsInvisibleOnlyValues(string value)
    {
        Assert.Empty(BotIdentityPresentationPolicy.NormalizePersonaName(value));
    }

    [Theory]
    [InlineData("0", "")]
    [InlineData("", "")]
    [InlineData(" CSGO-abc ", "CSGO-abc")]
    public void TryNormalizeCrosshair_AcceptsUpstreamForms(
        string value,
        string expected)
    {
        Assert.True(
            BotIdentityPresentationPolicy.TryNormalizeCrosshair(
                value,
                out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void TryNormalizeCrosshair_RejectsInvalidPrefixAndOversizedCode()
    {
        Assert.False(
            BotIdentityPresentationPolicy.TryNormalizeCrosshair(
                "not-a-share-code",
                out _));
        Assert.False(
            BotIdentityPresentationPolicy.TryNormalizeCrosshair(
                "CSGO-" + new string('a', 59),
                out _));
    }

    [Fact]
    public void TryValidateAvatarBytes_AcceptsPngSignatureWithinLimit()
    {
        var bytes = new byte[]
        {
            0x89, (byte)'P', (byte)'N', (byte)'G',
            0x0D, 0x0A, 0x1A, 0x0A,
        };

        Assert.True(
            BotIdentityPresentationPolicy.TryValidateAvatarBytes(
                bytes,
                out var error));
        Assert.Empty(error);
    }

    [Fact]
    public void TryValidateAvatarBytes_RejectsEmptyInvalidAndOversizedData()
    {
        Assert.False(
            BotIdentityPresentationPolicy.TryValidateAvatarBytes(
                [],
                out _));
        Assert.False(
            BotIdentityPresentationPolicy.TryValidateAvatarBytes(
                new byte[8],
                out _));

        var oversized = new byte[
            BotIdentityPresentationPolicy.MaxAvatarBytes + 1];
        oversized[0] = 0x89;
        oversized[1] = (byte)'P';
        oversized[2] = (byte)'N';
        oversized[3] = (byte)'G';
        oversized[4] = 0x0D;
        oversized[5] = 0x0A;
        oversized[6] = 0x1A;
        oversized[7] = 0x0A;
        Assert.False(
            BotIdentityPresentationPolicy.TryValidateAvatarBytes(
                oversized,
                out _));
    }
}
