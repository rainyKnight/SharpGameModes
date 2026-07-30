using SharpGameModes.Contracts;

namespace SharpGameModes.Domain.Tests;

public sealed class ModeContextStateTests
{
    [Fact]
    public void Activate_IsIdempotentForSameSelection()
    {
        using var state = new ModeContextState();
        var notifications = 0;
        using var subscription = state.Subscribe(_ => notifications++);
        var selection = Selection(ModeId.Classic);

        var first = state.Activate(selection, "test");
        var second = state.Activate(selection, "another-source");

        Assert.Same(first, second);
        Assert.Equal(1, notifications);
        Assert.Equal(1, state.Current!.Generation);
    }

    [Fact]
    public void Activate_TreatsSameMapInDifferentModesAsDifferentEntries()
    {
        using var state = new ModeContextState();

        state.Activate(Selection(ModeId.Classic), "classic-pool");
        var tdm = state.Activate(Selection(ModeId.TeamDeathmatch), "tdm-pool");

        Assert.Equal(2, tdm.Generation);
        Assert.Equal("tdm:de_mirage", tdm.Selection.EntryId);
        Assert.Equal(ModeId.TeamDeathmatch, state.Current!.Selection.Mode);
    }

    private static MapSelection Selection(ModeId mode)
        => new($"{mode.Value}:de_mirage", mode, "de_mirage", "荒漠迷城", false, null);
}
