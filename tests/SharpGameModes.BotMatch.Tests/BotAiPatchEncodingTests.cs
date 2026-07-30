using SharpGameModes.BotMatch;

namespace SharpGameModes.BotMatch.Tests;

public sealed class BotAiPatchEncodingTests
{
    [Fact]
    public void CatalogsContainEveryUpstreamPatch()
    {
        Assert.Equal(43, LinuxPatchDefinitions.All.Count);
        Assert.Equal(42, WindowsPatchDefinitions.All.Count);
        Assert.Contains(
            "FlashbangAvoidance_Disable",
            LinuxPatchDefinitions.All.Keys);
        Assert.Contains(
            "Upkeep_BotCOS_ZeroDrift",
            WindowsPatchDefinitions.All.Keys);
        Assert.Contains(
            "Upkeep_BotSIN_ZeroDrift",
            WindowsPatchDefinitions.All.Keys);
    }

    [Fact]
    public void EveryLinuxDefinitionHasValidReplacementAndExpectedLength()
        => ValidateCatalog(LinuxPatchDefinitions.All);

    [Fact]
    public void EveryWindowsDefinitionHasValidReplacementAndExpectedLength()
        => ValidateCatalog(WindowsPatchDefinitions.All);

    [Fact]
    public void ExpectedMatcherSupportsWildcardBytes()
    {
        Assert.True(
            BotAiPatchEncoding.MatchesExpected(
                [0xE8, 0x12, 0x34, 0x56, 0x78],
                "E8 ? ? ? ?"));
        Assert.False(
            BotAiPatchEncoding.MatchesExpected(
                [0xE9, 0x12, 0x34, 0x56, 0x78],
                "E8 ? ? ? ?"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("?")]
    [InlineData("90 GG")]
    public void ReplacementParserRejectsInvalidBytes(string value)
        => Assert.False(
            BotAiPatchEncoding.TryParsePatch(value, out _));

    [Fact]
    public void Relative32TargetCanBeReadAndRewritten()
    {
        var instruction = new byte[]
        {
            0xE9,
            0x10,
            0x00,
            0x00,
            0x00,
        };

        Assert.True(
            BotAiPatchEncoding.TryReadRelative32Target(
                0x1000,
                instruction,
                1,
                5,
                out var originalTarget));
        Assert.Equal((nint)0x1015, originalTarget);

        Assert.True(
            BotAiPatchEncoding.TryWriteRelative32(
                instruction,
                1,
                0x1000,
                5,
                0x0FF0));
        Assert.True(
            BotAiPatchEncoding.TryReadRelative32Target(
                0x1000,
                instruction,
                1,
                5,
                out var relocatedTarget));
        Assert.Equal((nint)0x0FF0, relocatedTarget);
    }

    [Fact]
    public void Relative8WriterRejectsOutOfRangeTarget()
    {
        var instruction = new byte[] { 0xEB, 0x00 };

        Assert.True(
            BotAiPatchEncoding.TryWriteRelative8(
                instruction,
                1,
                0x1000,
                2,
                0x1040));
        Assert.Equal(0x3E, instruction[1]);
        Assert.False(
            BotAiPatchEncoding.TryWriteRelative8(
                instruction,
                1,
                0x1000,
                2,
                0x2000));
    }

    private static void ValidateCatalog(
        IReadOnlyDictionary<
            string,
            (
                string signature,
                string patch,
                string expectedOriginal,
                int patchOffset)> catalog)
    {
        foreach (var (name, definition) in catalog)
        {
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.False(string.IsNullOrWhiteSpace(definition.signature));
            Assert.True(definition.patchOffset >= 0);
            Assert.True(
                BotAiPatchEncoding.TryParsePatch(
                    definition.patch,
                    out var replacement),
                name);
            var expectedLength = definition.expectedOriginal.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries).Length;
            Assert.Equal(expectedLength, replacement.Length);
        }
    }
}
