using SharpGameModes.Contracts;

namespace SharpGameModes.Domain.Tests;

public sealed class MapCatalogTests
{
    [Fact]
    public void ClassicPool_ProvidesStockMapExamplesWithStableIds()
    {
        var catalog = MapCatalog.Load(ConfigPath("sharp", "configs", "sharp-gamemodes", "map-pools", "classic.jsonc"));

        Assert.Equal(3, catalog.Entries.Count);
        Assert.All(catalog.Entries, entry =>
        {
            Assert.Equal(ModeId.Classic, entry.Mode);
            Assert.Equal(64, entry.MaxPlayers);
            Assert.StartsWith("classic:", entry.EntryId, StringComparison.Ordinal);
        });
        Assert.Equal("Mirage", catalog.ResolvePhysicalMap("DE_MIRAGE")!.DisplayName);
        Assert.All(catalog.Entries, entry => Assert.False(entry.Workshop));
        var rule = catalog.ResolvePhysicalMap("de_mirage")!.AutoTeam;
        Assert.NotNull(rule);
        Assert.True(rule.Enabled);
        Assert.Equal("first_round_then_balance", rule.RoundRandomizeMode);
        Assert.True(rule.RecordPlayerData);
        Assert.True(rule.UsePlayerDataForBalance);
    }

    [Fact]
    public void EligibleCandidates_ExcludeZeroWeightAndDisabledEntries()
    {
        var catalog = MapCatalog.FromDocument(new MapPoolDocument
        {
            Mode = "classic",
            DisplayName = "Classic",
            Maps = new Dictionary<string, MapDefinition>
            {
                ["enabled"] = new(),
                ["zero_weight"] = new() { Weight = 0 },
                ["disabled"] = new() { Enabled = false },
            },
        });

        var candidate = Assert.Single(catalog.GetEligibleCandidates(64));
        Assert.Equal("enabled", candidate.MapName);
    }

    [Fact]
    public void TeamDeathmatchPool_ProvidesStockMapExample()
    {
        var catalog = MapCatalog.Load(ConfigPath("sharp", "configs", "sharp-gamemodes", "map-pools", "tdm.jsonc"));

        var entry = Assert.Single(catalog.Entries);
        Assert.Equal("tdm:de_dust2", entry.EntryId);
        Assert.Equal(ModeId.TeamDeathmatch, entry.Mode);
        Assert.Equal("Team Deathmatch", entry.ModeDisplayName);
        Assert.Equal("Dust II", entry.DisplayName);
        Assert.False(entry.Workshop);
        Assert.Null(entry.WorkshopId);
        Assert.Equal(64, entry.MaxPlayers);
        Assert.True(entry.AutoTeam!.Enabled);
        Assert.False(entry.AutoTeam.RecordPlayerData);
        Assert.False(entry.AutoTeam.PrintTopPlayersToChat);
    }

    [Fact]
    public void ZombiePool_ProvidesStockMapExample()
    {
        var catalog = MapCatalog.Load(ConfigPath("sharp", "configs", "sharp-gamemodes", "map-pools", "zombie.jsonc"));

        var entry = Assert.Single(catalog.Entries);
        Assert.Equal("zombie:de_dust2", entry.EntryId);
        Assert.Equal(ModeId.Zombie, entry.Mode);
        Assert.Equal("Zombie Infection", entry.ModeDisplayName);
        Assert.Equal("Dust II (example)", entry.DisplayName);
        Assert.False(entry.Workshop);
        Assert.Null(entry.WorkshopId);
        Assert.Equal(64, entry.MaxPlayers);
        Assert.False(entry.AutoTeam!.Enabled);
        Assert.False(entry.AutoTeam.LockTeamSelect);
    }

    [Fact]
    public void BotMatchPool_UsesUpstreamNadeMapsExceptNukeWithoutHumanAutoTeamRules()
    {
        var catalog = MapCatalog.Load(ConfigPath("sharp", "configs", "sharp-gamemodes", "map-pools", "botmatch.jsonc"));
        string[] expectedMaps =
        [
            "cs_italy",
            "cs_office",
            "de_ancient",
            "de_anubis",
            "de_cache",
            "de_dust2",
            "de_inferno",
            "de_mirage",
            "de_overpass",
            "de_train",
            "de_vertigo",
        ];

        Assert.Equal(expectedMaps, catalog.Entries.Select(entry => entry.MapName));
        Assert.DoesNotContain(catalog.Entries, entry => entry.MapName == "de_nuke");
        Assert.All(catalog.Entries, entry =>
        {
            Assert.Equal(ModeId.BotMatch, entry.Mode);
            Assert.StartsWith("botmatch:", entry.EntryId, StringComparison.Ordinal);
            Assert.False(entry.Workshop);
            Assert.Equal(64, entry.MaxPlayers);
            Assert.False(entry.AutoTeam!.Enabled);
            Assert.False(entry.AutoTeam.LockTeamSelect);
            Assert.False(entry.AutoTeam.RecordPlayerData);
        });
        var mirage = catalog.ResolvePhysicalMap("de_mirage")!;
        Assert.Equal("荒漠迷城", mirage.DisplayName);
        Assert.Equal("荒漠迷城 [强化人机]", MapEntryDisplay.Format(mirage));
    }

    [Fact]
    public void FromDocument_RejectsInvalidWorkshopId()
    {
        var document = new MapPoolDocument
        {
            Mode = "classic",
            DisplayName = "经典竞技",
            Maps = new Dictionary<string, MapDefinition>
            {
                ["broken"] = new() { Workshop = true, WorkshopId = "not-an-id" },
            },
        };

        Assert.Throws<InvalidDataException>(() => MapCatalog.FromDocument(document));
    }

    [Fact]
    public void FromDocument_MapRuleOverridesOnlySpecifiedModeFields()
    {
        var document = new MapPoolDocument
        {
            Mode = "classic",
            DisplayName = "Classic",
            AutoTeam = new AutoTeamRuleOverrides
            {
                Enabled = true,
                CounterTerroristRatio = 1,
                TerroristRatio = 1,
                RoundRandomizeMode = "first_round_then_balance",
                RecordPlayerData = true,
            },
            Maps = new Dictionary<string, MapDefinition>
            {
                ["de_example"] = new()
                {
                    AutoTeam = new AutoTeamRuleOverrides
                    {
                        CounterTerroristRatio = 2,
                        RecordPlayerData = false,
                    },
                },
            },
        };

        var entry = Assert.Single(MapCatalog.FromDocument(document).Entries);

        Assert.True(entry.AutoTeam!.Enabled);
        Assert.Equal(2, entry.AutoTeam.CounterTerroristRatio);
        Assert.Equal(1, entry.AutoTeam.TerroristRatio);
        Assert.Equal("first_round_then_balance", entry.AutoTeam.RoundRandomizeMode);
        Assert.False(entry.AutoTeam.RecordPlayerData);
    }

    private static string ConfigPath(params string[] parts)
        => Path.Combine([AppContext.BaseDirectory, "Config", .. parts]);
}
