using SharpGameModes.Contracts;

namespace SharpGameModes.Domain.Tests;

public sealed class MapVotingTests
{
    [Fact]
    public void Search_ReturnsBothModesForSamePhysicalMap()
    {
        var entries = new[]
        {
            Entry("classic:de_mirage", ModeId.Classic, "经典竞技", "de_mirage", "荒漠迷城"),
            Entry("tdm:de_mirage", ModeId.TeamDeathmatch, "团队死斗", "de_mirage", "荒漠迷城"),
        };

        var matches = MapSearch.Find(entries, "mirage");

        Assert.Equal(2, matches.Count);
        Assert.Contains(matches, entry => MapEntryDisplay.Format(entry) == "荒漠迷城 [经典竞技]");
        Assert.Contains(matches, entry => MapEntryDisplay.Format(entry) == "荒漠迷城 [团队死斗]");
    }

    [Fact]
    public void NominationPage_WrapsAcrossFiveItemPages()
    {
        var entries = Enumerable.Range(1, 12)
            .Select(index => Entry($"classic:map{index}", ModeId.Classic, "经典竞技", $"map{index}", $"地图{index}"))
            .ToArray();

        var second = NominationPage.Create(entries, 1, 5);
        var wrapped = NominationPage.Create(entries, 3, 5);

        Assert.Equal((2, 3, 5), (second.PageNumber, second.PageCount, second.Entries.Count));
        Assert.Equal("classic:map6", second.Entries[0].EntryId);
        Assert.Equal(1, wrapped.PageNumber);
    }

    [Theory]
    [InlineData(0, 12, 1, 0)]
    [InlineData(7, 12, 1, 5)]
    [InlineData(7, 12, 5, 9)]
    [InlineData(11, 12, 2, 11)]
    public void PagedSelection_ResolvesNumbersWithinCurrentFiveItemPage(
        int currentIndex,
        int itemCount,
        int visibleNumber,
        int expectedIndex)
    {
        var resolved = PagedSelection.TryResolveVisibleNumber(
            currentIndex,
            itemCount,
            5,
            visibleNumber,
            out var selectedIndex);

        Assert.True(resolved);
        Assert.Equal(expectedIndex, selectedIndex);
    }

    [Theory]
    [InlineData(11, 12, 3)]
    [InlineData(0, 5, 0)]
    [InlineData(0, 5, 6)]
    public void PagedSelection_RejectsNumbersOutsideVisibleItems(
        int currentIndex,
        int itemCount,
        int visibleNumber)
    {
        Assert.False(PagedSelection.TryResolveVisibleNumber(
            currentIndex,
            itemCount,
            5,
            visibleNumber,
            out _));
    }

    [Fact]
    public void CandidateSelection_PrioritizesUniqueNominationsAndRelaxesRecentMaps()
    {
        var entries = Enumerable.Range(1, 6)
            .Select(index => Entry($"classic:map{index}", ModeId.Classic, "经典竞技", $"map{index}", $"地图{index}"))
            .ToArray();

        var selected = MapCandidateSelector.Select(
            entries,
            "classic:map1",
            ["classic:map2", "classic:map3", "classic:map4"],
            ["classic:map5", "classic:map5"],
            5,
            new Random(7));

        Assert.Equal("classic:map5", selected[0].EntryId);
        Assert.Equal(5, selected.Count);
        Assert.Equal(5, selected.Select(entry => entry.EntryId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(selected, entry => entry.EntryId == "classic:map1");
    }

    [Fact]
    public void Vote_RequiresRevokeBeforeChangingAndChoosesHighestCount()
    {
        var first = Entry("classic:first", ModeId.Classic, "经典竞技", "first", "第一张");
        var second = Entry("classic:second", ModeId.Classic, "经典竞技", "second", "第二张");
        var vote = new MapVoteSession([first, second]);

        Assert.Equal(MapVoteCastResult.Accepted, vote.Cast(1, first.EntryId));
        Assert.Equal(first.EntryId, vote.GetVote(1));
        Assert.Equal(MapVoteCastResult.MustRevokeFirst, vote.Cast(1, second.EntryId));
        Assert.True(vote.Revoke(1));
        Assert.Null(vote.GetVote(1));
        Assert.Equal(MapVoteCastResult.Accepted, vote.Cast(1, second.EntryId));
        Assert.Equal(MapVoteCastResult.Accepted, vote.Cast(2, second.EntryId));

        Assert.Equal(second, vote.SelectWinner(new Random(1)));
    }

    [Fact]
    public void Rtv_UsesCeilingRatioAndRejectsDuplicateVotes()
    {
        var tracker = new RtvTracker();
        ulong[] eligible = [1, 2, 3, 4];

        var first = tracker.Register(1, eligible, 0.6);
        var duplicate = tracker.Register(1, eligible, 0.6);
        tracker.Register(2, eligible, 0.6);
        var passed = tracker.Register(3, eligible, 0.6);

        Assert.Equal((true, 1, 3, false), (first.Accepted, first.CurrentVotes, first.RequiredVotes, first.Passed));
        Assert.False(duplicate.Accepted);
        Assert.True(passed.Passed);
    }

    [Fact]
    public void CatalogCombine_PreservesModeQualifiedIdentity()
    {
        var classic = MapCatalog.FromDocument(Document("classic", "经典竞技"));
        var tdm = MapCatalog.FromDocument(Document("tdm", "团队死斗"));

        var combined = MapCatalog.Combine([classic, tdm]);

        Assert.Equal(2, combined.Entries.Count);
        Assert.Equal(ModeId.TeamDeathmatch, combined.ResolveEntryId("TDM:DE_MIRAGE")!.Mode);
        Assert.Equal(ModeId.Classic, combined.ResolvePhysicalMap("de_mirage", ModeId.Classic)!.Mode);
    }

    private static MapPoolDocument Document(string mode, string displayName)
        => new()
        {
            Mode = mode,
            DisplayName = displayName,
            Maps = new Dictionary<string, MapDefinition>
            {
                ["de_mirage"] = new() { DisplayName = "荒漠迷城", MaxPlayers = 64 },
            },
        };

    private static MapPoolEntry Entry(
        string id,
        ModeId mode,
        string modeDisplayName,
        string mapName,
        string displayName)
        => new(id, mode, modeDisplayName, mapName, displayName, true, false, null, 0, 64, 1);
}
