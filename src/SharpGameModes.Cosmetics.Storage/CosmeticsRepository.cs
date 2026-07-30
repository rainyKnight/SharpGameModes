using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SharpGameModes.Cosmetics.Storage;

public sealed class CosmeticsRepository
{
    private static readonly object SqliteInitializationGate = new();
    private static bool _sqliteInitialized;

    public CosmeticsRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath { get; }

    public void EnsureCreated()
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = OpenConnection(SqliteOpenMode.ReadWriteCreate);
        ExecuteNonQuery(connection, "PRAGMA busy_timeout = 5000;");
        ExecuteNonQuery(connection, "PRAGMA journal_mode = DELETE;");
        ExecuteNonQuery(
            connection,
            """
            DROP TABLE IF EXISTS player_models;

            CREATE TABLE IF NOT EXISTS weapon_skins (
                steam_id TEXT NOT NULL,
                team INTEGER NOT NULL,
                weapon_definition_index INTEGER NOT NULL,
                paint_kit INTEGER NOT NULL,
                wear REAL NOT NULL,
                seed INTEGER NOT NULL,
                name_tag TEXT NOT NULL,
                stattrak INTEGER NOT NULL,
                stattrak_count INTEGER NOT NULL,
                sticker_0 TEXT NOT NULL,
                sticker_1 TEXT NOT NULL,
                sticker_2 TEXT NOT NULL,
                sticker_3 TEXT NOT NULL,
                sticker_4 TEXT NOT NULL,
                keychain TEXT NOT NULL,
                PRIMARY KEY (steam_id, team, weapon_definition_index)
            );

            CREATE TABLE IF NOT EXISTS knives (
                steam_id TEXT NOT NULL,
                team INTEGER NOT NULL,
                class_name TEXT NOT NULL,
                PRIMARY KEY (steam_id, team)
            );

            PRAGMA user_version = 2;
            """);
    }

    public CosmeticsSnapshot LoadAll()
    {
        EnsureCreated();
        using var connection = OpenConnection(SqliteOpenMode.ReadOnly);

        var skins = new Dictionary<WeaponSkinKey, WeaponSkinPreference>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT
                    steam_id, team, weapon_definition_index, paint_kit, wear, seed,
                    name_tag, stattrak, stattrak_count, sticker_0, sticker_1,
                    sticker_2, sticker_3, sticker_4, keychain
                FROM weapon_skins;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!TryReadSteamId(reader.GetString(0), out var steamId))
                {
                    continue;
                }

                var preference = new WeaponSkinPreference(
                    steamId,
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetDouble(4),
                    reader.GetInt32(5),
                    reader.GetString(6),
                    reader.GetInt32(7) != 0,
                    reader.GetInt32(8),
                    reader.GetString(9),
                    reader.GetString(10),
                    reader.GetString(11),
                    reader.GetString(12),
                    reader.GetString(13),
                    reader.GetString(14));
                skins[new WeaponSkinKey(steamId, preference.Team, preference.WeaponDefinitionIndex)] = preference;
            }
        }

        var knives = new Dictionary<KnifeKey, KnifePreference>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT steam_id, team, class_name FROM knives;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!TryReadSteamId(reader.GetString(0), out var steamId))
                {
                    continue;
                }

                var preference = new KnifePreference(steamId, reader.GetInt32(1), reader.GetString(2));
                knives[new KnifeKey(steamId, preference.Team)] = preference;
            }
        }

        return new CosmeticsSnapshot(skins, knives);
    }

    public void UpsertWeaponSkin(WeaponSkinPreference preference)
    {
        ValidateSkin(preference);
        EnsureCreated();
        using var connection = OpenConnection(SqliteOpenMode.ReadWriteCreate);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO weapon_skins (
                steam_id, team, weapon_definition_index, paint_kit, wear, seed,
                name_tag, stattrak, stattrak_count, sticker_0, sticker_1,
                sticker_2, sticker_3, sticker_4, keychain
            ) VALUES (
                $steamId, $team, $weaponDefinitionIndex, $paintKit, $wear, $seed,
                $nameTag, $stattrak, $stattrakCount, $sticker0, $sticker1,
                $sticker2, $sticker3, $sticker4, $keychain
            )
            ON CONFLICT (steam_id, team, weapon_definition_index) DO UPDATE SET
                paint_kit = excluded.paint_kit,
                wear = excluded.wear,
                seed = excluded.seed,
                name_tag = excluded.name_tag,
                stattrak = excluded.stattrak,
                stattrak_count = excluded.stattrak_count,
                sticker_0 = excluded.sticker_0,
                sticker_1 = excluded.sticker_1,
                sticker_2 = excluded.sticker_2,
                sticker_3 = excluded.sticker_3,
                sticker_4 = excluded.sticker_4,
                keychain = excluded.keychain;
            """;
        AddSkinParameters(command, preference);
        command.ExecuteNonQuery();
    }

    public void DeleteWeaponSkin(WeaponSkinKey key)
    {
        EnsureCreated();
        using var connection = OpenConnection(SqliteOpenMode.ReadWriteCreate);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM weapon_skins
            WHERE steam_id = $steamId
              AND team = $team
              AND weapon_definition_index = $weaponDefinitionIndex;
            """;
        command.Parameters.AddWithValue("$steamId", FormatSteamId(key.SteamId));
        command.Parameters.AddWithValue("$team", key.Team);
        command.Parameters.AddWithValue("$weaponDefinitionIndex", key.WeaponDefinitionIndex);
        command.ExecuteNonQuery();
    }

    public void UpsertKnife(KnifePreference preference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preference.ClassName);
        EnsureCreated();
        using var connection = OpenConnection(SqliteOpenMode.ReadWriteCreate);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO knives (steam_id, team, class_name)
            VALUES ($steamId, $team, $className)
            ON CONFLICT (steam_id, team) DO UPDATE SET
                class_name = excluded.class_name;
            """;
        command.Parameters.AddWithValue("$steamId", FormatSteamId(preference.SteamId));
        command.Parameters.AddWithValue("$team", preference.Team);
        command.Parameters.AddWithValue("$className", preference.ClassName);
        command.ExecuteNonQuery();
    }

    public void DeleteKnife(KnifeKey key)
    {
        EnsureCreated();
        using var connection = OpenConnection(SqliteOpenMode.ReadWriteCreate);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM knives
            WHERE steam_id = $steamId
              AND team = $team;
            """;
        command.Parameters.AddWithValue("$steamId", FormatSteamId(key.SteamId));
        command.Parameters.AddWithValue("$team", key.Team);
        command.ExecuteNonQuery();
    }

    public int ImportWeaponSkins(IEnumerable<WeaponSkinPreference> preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        EnsureCreated();
        using var connection = OpenConnection(SqliteOpenMode.ReadWriteCreate);
        using var transaction = connection.BeginTransaction();
        var imported = 0;
        foreach (var preference in preferences)
        {
            ValidateSkin(preference);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO weapon_skins (
                    steam_id, team, weapon_definition_index, paint_kit, wear, seed,
                    name_tag, stattrak, stattrak_count, sticker_0, sticker_1,
                    sticker_2, sticker_3, sticker_4, keychain
                ) VALUES (
                    $steamId, $team, $weaponDefinitionIndex, $paintKit, $wear, $seed,
                    $nameTag, $stattrak, $stattrakCount, $sticker0, $sticker1,
                    $sticker2, $sticker3, $sticker4, $keychain
                )
                ON CONFLICT (steam_id, team, weapon_definition_index) DO UPDATE SET
                    paint_kit = excluded.paint_kit,
                    wear = excluded.wear,
                    seed = excluded.seed,
                    name_tag = excluded.name_tag,
                    stattrak = excluded.stattrak,
                    stattrak_count = excluded.stattrak_count,
                    sticker_0 = excluded.sticker_0,
                    sticker_1 = excluded.sticker_1,
                    sticker_2 = excluded.sticker_2,
                    sticker_3 = excluded.sticker_3,
                    sticker_4 = excluded.sticker_4,
                    keychain = excluded.keychain;
                """;
            AddSkinParameters(command, preference);
            command.ExecuteNonQuery();
            imported++;
        }

        transaction.Commit();
        return imported;
    }

    public int ImportKnives(IEnumerable<KnifePreference> preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        EnsureCreated();
        using var connection = OpenConnection(SqliteOpenMode.ReadWriteCreate);
        using var transaction = connection.BeginTransaction();
        var imported = 0;
        foreach (var preference in preferences)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(preference.ClassName);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO knives (steam_id, team, class_name)
                VALUES ($steamId, $team, $className)
                ON CONFLICT (steam_id, team) DO UPDATE SET
                    class_name = excluded.class_name;
                """;
            command.Parameters.AddWithValue("$steamId", FormatSteamId(preference.SteamId));
            command.Parameters.AddWithValue("$team", preference.Team);
            command.Parameters.AddWithValue("$className", preference.ClassName);
            command.ExecuteNonQuery();
            imported++;
        }

        transaction.Commit();
        return imported;
    }

    private SqliteConnection OpenConnection(SqliteOpenMode mode)
        => OpenExternalConnection(DatabasePath, mode);

    private static SqliteConnection OpenExternalConnection(string path, SqliteOpenMode mode)
    {
        EnsureSqliteInitialized();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = mode,
            Cache = mode == SqliteOpenMode.ReadOnly ? SqliteCacheMode.Private : SqliteCacheMode.Shared,
            DefaultTimeout = 5,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static void EnsureSqliteInitialized()
    {
        if (_sqliteInitialized)
        {
            return;
        }

        lock (SqliteInitializationGate)
        {
            if (_sqliteInitialized)
            {
                return;
            }

            SQLitePCL.Batteries_V2.Init();
            _sqliteInitialized = true;
        }
    }

    private static void AddSkinParameters(SqliteCommand command, WeaponSkinPreference preference)
    {
        command.Parameters.AddWithValue("$steamId", FormatSteamId(preference.SteamId));
        command.Parameters.AddWithValue("$team", preference.Team);
        command.Parameters.AddWithValue("$weaponDefinitionIndex", preference.WeaponDefinitionIndex);
        command.Parameters.AddWithValue("$paintKit", preference.PaintKit);
        command.Parameters.AddWithValue("$wear", preference.Wear);
        command.Parameters.AddWithValue("$seed", preference.Seed);
        command.Parameters.AddWithValue("$nameTag", preference.NameTag);
        command.Parameters.AddWithValue("$stattrak", preference.StatTrak ? 1 : 0);
        command.Parameters.AddWithValue("$stattrakCount", preference.StatTrakCount);
        command.Parameters.AddWithValue("$sticker0", preference.Sticker0);
        command.Parameters.AddWithValue("$sticker1", preference.Sticker1);
        command.Parameters.AddWithValue("$sticker2", preference.Sticker2);
        command.Parameters.AddWithValue("$sticker3", preference.Sticker3);
        command.Parameters.AddWithValue("$sticker4", preference.Sticker4);
        command.Parameters.AddWithValue("$keychain", preference.Keychain);
    }

    private static void ValidateSkin(WeaponSkinPreference preference)
    {
        if (preference.WeaponDefinitionIndex <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(preference.WeaponDefinitionIndex));
        }

        if (preference.PaintKit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(preference.PaintKit));
        }

        if (preference.Wear is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(preference.Wear));
        }
    }

    private static bool TryReadSteamId(string value, out ulong steamId)
        => ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out steamId) && steamId != 0;

    private static string FormatSteamId(ulong steamId)
    {
        if (steamId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(steamId));
        }

        return steamId.ToString(CultureInfo.InvariantCulture);
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }
}
