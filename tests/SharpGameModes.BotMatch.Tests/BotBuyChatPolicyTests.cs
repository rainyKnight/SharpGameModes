namespace SharpGameModes.BotMatch.Tests;

public sealed class BotBuyChatPolicyTests
{
    [Fact]
    public void FormatWeaponGift_UsesChineseBotSpeech()
    {
        var message = BotBuyChatPolicy.FormatWeaponGift("donk", "ZywOo");

        Assert.Equal("\u0004donk\u0009：ZywOo，我给你发了把枪", message);
        Assert.DoesNotContain("dropped", message, StringComparison.OrdinalIgnoreCase);
    }
}
