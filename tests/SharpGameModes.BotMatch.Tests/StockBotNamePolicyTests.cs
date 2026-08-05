namespace SharpGameModes.BotMatch.Tests;

public sealed class StockBotNamePolicyTests
{
    [Fact]
    public void ForSlot_IsStableAndStockStyle()
    {
        var name = StockBotNamePolicy.ForSlot(7);

        Assert.Equal(name, StockBotNamePolicy.ForSlot(7));
        Assert.True(StockBotNamePolicy.IsStockStyle(name));
    }

    [Fact]
    public void ForSlot_UsesDistinctNamesForTypicalTeamSizes()
    {
        var names = Enumerable.Range(0, 10)
            .Select(StockBotNamePolicy.ForSlot)
            .ToArray();

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ForSlot_RejectsNegativeSlots()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => StockBotNamePolicy.ForSlot(-1));
}
