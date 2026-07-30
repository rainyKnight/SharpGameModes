using SharpGameModes.Contracts;

namespace SharpGameModes.Domain.Tests;

public sealed class ModeIdTests
{
    [Theory]
    [InlineData("classic", "classic")]
    [InlineData("default", "classic")]
    [InlineData("competitive", "classic")]
    [InlineData("teamdeathmatch", "tdm")]
    [InlineData("infection", "zombie")]
    [InlineData("bot", "botmatch")]
    [InlineData("bots", "botmatch")]
    [InlineData("botclassic", "botmatch")]
    public void Parse_NormalizesSupportedAliases(string value, string expected)
    {
        Assert.Equal(expected, ModeId.Parse(value).Value);
    }

    [Theory]
    [InlineData("ghost")]
    [InlineData("")]
    public void Parse_RejectsRetiredOrDeferredModes(string value)
    {
        Assert.Throws<ArgumentException>(() => ModeId.Parse(value));
    }
}
