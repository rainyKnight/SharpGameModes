namespace SharpGameModes.BotMatch.Tests;

public sealed class BotCosmeticCatalogTests
{
    [Fact]
    public void PackagedCatalog_PreservesLatestUpstreamCardinality()
    {
        var catalog = CosmeticCatalog.Load(CatalogPath());
        var placements = CharmPlacementCatalog.Load(
            PlacementPath(),
            catalog);

        Assert.Equal(35, catalog.WeaponCount);
        Assert.Equal(1_456, catalog.WeaponPaintCount);
        Assert.Equal(556, catalog.KnifePaintCount);
        Assert.Equal(94, catalog.Gloves.Count);
        Assert.Equal(61, catalog.StickerCategories.Count);
        Assert.Equal(10_565, catalog.StickerKits.Count);
        Assert.Equal(81, catalog.KeychainDefinitions.Count);
        Assert.Equal(98, catalog.MusicKits.Count);
        Assert.Equal(16, placements.WeaponCount);
        Assert.Equal(158, placements.PlacementCount);
    }

    [Fact]
    public void Roller_KeepsWeaponPaintStickersAndCharmStablePerLoadout()
    {
        var catalog = CosmeticCatalog.Load(CatalogPath());
        var placements = CharmPlacementCatalog.Load(
            PlacementPath(),
            catalog);
        var roller = new CosmeticRoller(
            catalog,
            placements,
            new Random(17));
        var loadout = roller.RollLoadout(RandomizerAssets.TerroristTeam);

        var first = Assert.IsType<WeaponCosmeticSelection>(
            roller.GetOrCreateWeapon(loadout, 1));
        var second = Assert.IsType<WeaponCosmeticSelection>(
            roller.GetOrCreateWeapon(loadout, 1));

        Assert.Same(first, second);
        Assert.InRange(first.Stickers.Count, 0, 5);
        Assert.Equal(
            first.Stickers.Count,
            first.Stickers.Select(sticker => sticker.Slot).Distinct().Count());
        Assert.All(
            first.Stickers,
            sticker => Assert.InRange(sticker.Slot, 0, 4));
    }

    [Fact]
    public void Roller_UsesWeaponAwareCharmCoordinates()
    {
        var catalog = CosmeticCatalog.Load(CatalogPath());
        var placements = CharmPlacementCatalog.Load(
            PlacementPath(),
            catalog);
        Assert.True(placements.TryGetPlacements(1, out var deaglePlacements));
        var roller = new CosmeticRoller(
            catalog,
            placements,
            new Random(7054));

        KeychainSelection? keychain = null;
        for (var attempt = 0; attempt < 32 && keychain is null; attempt++)
        {
            roller.ResetMap();
            var loadout = roller.RollLoadout(
                RandomizerAssets.CounterTerroristTeam);
            keychain = roller.GetOrCreateWeapon(loadout, 1)?.Keychain;
        }

        var selected = Assert.IsType<KeychainSelection>(keychain);
        var coordinate = new CharmPlacement(
            Assert.IsType<float>(selected.X),
            Assert.IsType<float>(selected.Y),
            Assert.IsType<float>(selected.Z));
        Assert.Contains(coordinate, deaglePlacements);
        Assert.InRange(selected.Seed, 1, 100_000);
    }

    [Fact]
    public void StateStore_PreservesMusicOnlyForTeamReroll()
    {
        var store = new CosmeticStateStore();
        var first = store.GetOrCreate(
            slot: 4,
            userId: 100,
            team: RandomizerAssets.TerroristTeam,
            _ => Loadout(RandomizerAssets.TerroristTeam, 7));
        var same = store.GetOrCreate(
            slot: 4,
            userId: 100,
            team: RandomizerAssets.TerroristTeam,
            _ => throw new InvalidOperationException());
        Assert.Same(first, same);

        var rerolled = store.Reroll(
            slot: 4,
            userId: 100,
            team: RandomizerAssets.CounterTerroristTeam,
            preserveMusic: true,
            music => Loadout(
                RandomizerAssets.CounterTerroristTeam,
                music ?? 99));

        Assert.NotNull(rerolled);
        Assert.NotEqual(first.Generation, rerolled.Generation);
        Assert.Equal(7, rerolled.Loadout.MusicKit);
        Assert.Equal(
            RandomizerAssets.CounterTerroristTeam,
            rerolled.Loadout.Team);
    }

    private static BotCosmeticLoadout Loadout(byte team, int music)
        => new()
        {
            Team = team,
            AgentModel = "model",
            MusicKit = music,
            Knife = new KnifeSelection(500, 1, 0.01f),
            Glove = new GloveSelection(5030, 1, 0.01f),
        };

    private static string CatalogPath()
        => Path.Combine(DataDirectory(), "cosmetic_catalog.json");

    private static string PlacementPath()
        => Path.Combine(DataDirectory(), "charm_placements.json");

    private static string DataDirectory()
        => Path.Combine(
            AppContext.BaseDirectory,
            "Config",
            "sharp",
            "configs",
            "sharp-gamemodes",
            "botmatch-cosmetics");
}
