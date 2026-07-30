namespace SharpGameModes.BotMatch;

internal static class BotBuyChatPolicy
{
    public static string FormatWeaponGift(string donorName, string recipientName)
        => $"\u0004{donorName}\u0009：{recipientName}，我给你发了把枪";
}
