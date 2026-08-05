namespace SharpGameModes.BotMatch;

internal static class StockBotNamePolicy
{
    private static readonly string[] Names =
    [
        "Albert", "Allen", "Bert", "Bob", "Cecil", "Clarence", "Elliot",
        "Elmer", "Ernie", "Eugene", "Fergus", "Ferris", "Frank", "Frasier",
        "Fred", "George", "Graham", "Harvey", "Irwin", "Larry", "Lester",
        "Marvin", "Neil", "Niles", "Oliver", "Opie", "Percy", "Perry",
        "Rocco", "Roger", "Toby", "Waldo",
    ];

    public static string ForSlot(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        return Names[slot % Names.Length];
    }

    public static bool IsStockStyle(string? name)
        => name is not null && Names.Contains(name, StringComparer.Ordinal);
}
