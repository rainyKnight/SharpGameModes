using System.Text.Json;

namespace SharpGameModes.BotMatch.Tests;

public sealed class NadeLineupCatalogTests
{
    private static readonly string[] Maps =
    [
        "cs_italy",
        "cs_office",
        "de_ancient",
        "de_anubis",
        "de_cache",
        "de_dust2",
        "de_inferno",
        "de_mirage",
        "de_nuke",
        "de_overpass",
        "de_train",
        "de_vertigo",
    ];

    [Fact]
    public void PackagedRoutes_CoverTwelveMapsAndFourSourceTypes()
    {
        var directory = DataDirectory();
        var files = Directory.GetFiles(directory, "*.json");

        Assert.Equal(48, files.Length);
        foreach (var map in Maps)
        {
            foreach (var type in new[] { "flash", "he", "molotov", "smoke" })
            {
                Assert.True(
                    File.Exists(Path.Combine(directory, $"{map}_{type}.json")),
                    $"Missing {map}_{type}.json");
            }

            Assert.NotEmpty(NadeLineupCatalog.Load(directory, map).Lineups);
        }
    }

    [Fact]
    public void PackagedRoutes_PreserveUpstreamCardinalityAndCompleteVectors()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var entryCount = 0;
        foreach (var file in Directory.GetFiles(DataDirectory(), "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                entryCount++;
                var id = entry.GetProperty("id").GetString();
                Assert.False(string.IsNullOrWhiteSpace(id));
                ids.Add(id!);
                Assert.False(
                    string.IsNullOrWhiteSpace(
                        entry.GetProperty("mapName").GetString()));
                Assert.False(
                    string.IsNullOrWhiteSpace(
                        entry.GetProperty("grenadeType").GetString()));
                AssertVector(entry.GetProperty("projectilePosition"));
                AssertVector(entry.GetProperty("projectileVelocity"));
                AssertVector(entry.GetProperty("landingPosition"));
            }
        }

        Assert.Equal(3_703, entryCount);
        Assert.Equal(3_506, ids.Count);
    }

    [Fact]
    public void Catalog_NormalizesDecoysAndQueriesOnlyNearbyCells()
    {
        var catalog = NadeLineupCatalog.Load(DataDirectory(), "de_mirage");
        var decoy = Assert.Single(
            catalog.Lineups.Where(lineup => lineup.GrenadeType == "decoy")
                .Take(1));
        Assert.Equal("decoy", decoy.GrenadeType);

        var nearby = catalog.Query(
                decoy.ProjectilePosition.X,
                decoy.ProjectilePosition.Y)
            .ToArray();
        Assert.Contains(nearby, lineup => lineup.Id == decoy.Id);
        Assert.DoesNotContain(
            catalog.Query(
                decoy.ProjectilePosition.X + 100_000f,
                decoy.ProjectilePosition.Y + 100_000f),
            lineup => lineup.Id == decoy.Id);
    }

    private static void AssertVector(JsonElement vector)
    {
        Assert.Equal(JsonValueKind.Number, vector.GetProperty("x").ValueKind);
        Assert.Equal(JsonValueKind.Number, vector.GetProperty("y").ValueKind);
        Assert.Equal(JsonValueKind.Number, vector.GetProperty("z").ValueKind);
    }

    private static string DataDirectory()
        => Path.Combine(
            AppContext.BaseDirectory,
            "Config",
            "sharp",
            "configs",
            "sharp-gamemodes",
            "botmatch-grenades");
}
