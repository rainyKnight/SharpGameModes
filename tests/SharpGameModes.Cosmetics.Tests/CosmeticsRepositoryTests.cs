using Microsoft.Data.Sqlite;
using SharpGameModes.Cosmetics.Storage;

namespace SharpGameModes.Cosmetics.Tests;

public sealed class CosmeticsRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"sharp-gamemodes-cosmetics-tests-{Guid.NewGuid():N}");

    public CosmeticsRepositoryTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void RoundTripsAndDeletesWeaponAndKnifePreferences()
    {
        var repository = new CosmeticsRepository(Path.Combine(_directory, "cosmetics.db"));
        var skin = CreateSkin(76561198000000001, 3, 7, 302);
        var knife = new KnifePreference(76561198000000001, 3, "weapon_knife_karambit");

        repository.UpsertWeaponSkin(skin);
        repository.UpsertKnife(knife);

        var snapshot = repository.LoadAll();
        Assert.Equal(skin, snapshot.WeaponSkins[new WeaponSkinKey(skin.SteamId, skin.Team, skin.WeaponDefinitionIndex)]);
        Assert.Equal(knife, snapshot.Knives[new KnifeKey(knife.SteamId, knife.Team)]);

        repository.DeleteWeaponSkin(new WeaponSkinKey(skin.SteamId, skin.Team, skin.WeaponDefinitionIndex));
        repository.DeleteKnife(new KnifeKey(knife.SteamId, knife.Team));
        snapshot = repository.LoadAll();
        Assert.Empty(snapshot.WeaponSkins);
        Assert.Empty(snapshot.Knives);
    }

    [Fact]
    public void RemovesLegacyPlayerModelTable()
    {
        var databasePath = Path.Combine(_directory, "cosmetics.db");
        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE player_models (
                    steam_id TEXT PRIMARY KEY,
                    t_model TEXT,
                    ct_model TEXT
                );
                INSERT INTO player_models VALUES ('76561198000000002', 'alpha', '@default');
                """;
            command.ExecuteNonQuery();
        }

        var repository = new CosmeticsRepository(databasePath);
        repository.EnsureCreated();
        using var verify = new SqliteConnection($"Data Source={databasePath}");
        verify.Open();
        using var query = verify.CreateCommand();
        query.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'player_models';";
        Assert.Equal(0L, (long)query.ExecuteScalar()!);
    }

    [Fact]
    public void BulkImportsWeaponPaintsAndKnives()
    {
        var repository = new CosmeticsRepository(Path.Combine(_directory, "cosmetics.db"));
        var skins = new[]
        {
            CreateSkin(76561198000000003, 2, 9, 344),
            CreateSkin(76561198000000003, 3, 16, 309),
        };
        var knives = new[]
        {
            new KnifePreference(76561198000000003, 2, "weapon_knife_butterfly"),
            new KnifePreference(76561198000000003, 3, "weapon_knife_m9_bayonet"),
        };

        Assert.Equal(2, repository.ImportWeaponSkins(skins));
        Assert.Equal(2, repository.ImportKnives(knives));
        var snapshot = repository.LoadAll();
        Assert.Equal(2, snapshot.WeaponSkins.Count);
        Assert.Equal(2, snapshot.Knives.Count);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private static WeaponSkinPreference CreateSkin(ulong steamId, int team, int weapon, int paint)
        => new(
            steamId,
            team,
            weapon,
            paint,
            0.01,
            0,
            string.Empty,
            false,
            0,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);
}
