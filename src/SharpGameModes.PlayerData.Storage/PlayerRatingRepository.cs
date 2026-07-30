using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Data.Sqlite;
using SharpGameModes.Contracts;

namespace SharpGameModes.PlayerData.Storage;

public sealed class PlayerRatingRepository
{
    private static readonly object SqliteInitializationGate = new();
    private static bool _sqliteInitialized;

    public PlayerRatingRepository(string databasePath)
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
        ExecuteNonQuery(
            connection,
            """
            CREATE TABLE IF NOT EXISTS player_rating_matches (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                steam_id TEXT NOT NULL,
                player_name TEXT NOT NULL,
                map_name TEXT NOT NULL,
                recorded_at_utc TEXT NOT NULL,
                rounds_played INTEGER NOT NULL,
                rating REAL NOT NULL,
                impact REAL NOT NULL,
                kast REAL NOT NULL,
                adr REAL NOT NULL,
                kills_per_round REAL NOT NULL,
                deaths_per_round REAL NOT NULL,
                assists_per_round REAL NOT NULL,
                kills INTEGER NOT NULL,
                deaths INTEGER NOT NULL,
                assists INTEGER NOT NULL,
                damage INTEGER NOT NULL,
                headshots INTEGER NOT NULL,
                entry_kills INTEGER NOT NULL,
                entry_deaths INTEGER NOT NULL,
                multi_kill_rounds INTEGER NOT NULL,
                clutches_won INTEGER NOT NULL,
                kast_rounds INTEGER NOT NULL,
                survived_rounds INTEGER NOT NULL
            );
            """);
        ExecuteNonQuery(
            connection,
            """
            CREATE INDEX IF NOT EXISTS idx_player_rating_matches_steam_id_id
                ON player_rating_matches (steam_id, id);
            """);
        ExecuteNonQuery(
            connection,
            """
            CREATE TABLE IF NOT EXISTS player_rating_summary (
                steam_id TEXT PRIMARY KEY,
                last_known_name TEXT NOT NULL,
                rounds_recorded INTEGER NOT NULL,
                matches_recorded INTEGER NOT NULL,
                history_count INTEGER NOT NULL,
                rating REAL NOT NULL,
                impact REAL NOT NULL,
                kast REAL NOT NULL,
                adr REAL NOT NULL,
                kills_per_round REAL NOT NULL,
                deaths_per_round REAL NOT NULL,
                assists_per_round REAL NOT NULL,
                total_kills INTEGER NOT NULL,
                total_deaths INTEGER NOT NULL,
                total_assists INTEGER NOT NULL,
                total_damage INTEGER NOT NULL,
                total_entry_kills INTEGER NOT NULL,
                total_entry_deaths INTEGER NOT NULL,
                total_headshots INTEGER NOT NULL,
                multi_kill_rounds INTEGER NOT NULL,
                clutches_won INTEGER NOT NULL,
                last_map TEXT NOT NULL,
                last_updated_utc TEXT NOT NULL
            );
            """);
        ExecuteNonQuery(connection, "PRAGMA user_version = 1;");
    }

    public IReadOnlyDictionary<ulong, PlayerRatingSnapshot> LoadAll()
    {
        if (!File.Exists(DatabasePath))
        {
            throw new FileNotFoundException("Player rating database does not exist.", DatabasePath);
        }

        using var connection = OpenConnection(SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                steam_id,
                last_known_name,
                rounds_recorded,
                matches_recorded,
                history_count,
                rating,
                impact,
                kast,
                adr,
                kills_per_round,
                deaths_per_round,
                assists_per_round,
                total_kills,
                total_deaths,
                total_assists,
                total_damage,
                total_entry_kills,
                total_entry_deaths,
                total_headshots,
                multi_kill_rounds,
                clutches_won,
                last_map,
                last_updated_utc
            FROM player_rating_summary;
            """;

        var ratings = new Dictionary<ulong, PlayerRatingSnapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var steamIdText = reader.GetString(0);
            if (!ulong.TryParse(steamIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var steamId))
            {
                continue;
            }

            ratings[steamId] = new PlayerRatingSnapshot(
                steamId,
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetDouble(5),
                reader.GetDouble(6),
                reader.GetDouble(7),
                reader.GetDouble(8),
                reader.GetDouble(9),
                reader.GetDouble(10),
                reader.GetDouble(11),
                reader.GetInt32(12),
                reader.GetInt32(13),
                reader.GetInt32(14),
                reader.GetInt32(15),
                reader.GetInt32(16),
                reader.GetInt32(17),
                reader.GetInt32(18),
                reader.GetInt32(19),
                reader.GetInt32(20),
                reader.GetString(21),
                ParseTimestamp(reader.GetString(22)));
        }

        return new ReadOnlyDictionary<ulong, PlayerRatingSnapshot>(ratings);
    }

    public void WriteMatches(IReadOnlyCollection<PlayerMatchWrite> matches, int historyLimit = 100)
    {
        ArgumentNullException.ThrowIfNull(matches);
        if (historyLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(historyLimit));
        }

        if (matches.Count == 0)
        {
            return;
        }

        EnsureCreated();
        using var connection = OpenConnection(SqliteOpenMode.ReadWriteCreate);
        using var transaction = connection.BeginTransaction();
        foreach (var match in matches)
        {
            ValidateMatch(match);
            InsertMatch(connection, transaction, match);
            PruneHistory(connection, transaction, match.SteamId, historyLimit);
            RefreshSummary(connection, transaction, match.SteamId);
        }

        transaction.Commit();
    }

    private SqliteConnection OpenConnection(SqliteOpenMode mode)
    {
        EnsureSqliteInitialized();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
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

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static void InsertMatch(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlayerMatchWrite match)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO player_rating_matches (
                steam_id, player_name, map_name, recorded_at_utc, rounds_played,
                rating, impact, kast, adr, kills_per_round, deaths_per_round,
                assists_per_round, kills, deaths, assists, damage, headshots,
                entry_kills, entry_deaths, multi_kill_rounds, clutches_won,
                kast_rounds, survived_rounds
            ) VALUES (
                $steamId, $playerName, $mapName, $recordedAt, $roundsPlayed,
                $rating, $impact, $kast, $adr, $killsPerRound, $deathsPerRound,
                $assistsPerRound, $kills, $deaths, $assists, $damage, $headshots,
                $entryKills, $entryDeaths, $multiKillRounds, $clutchesWon,
                $kastRounds, $survivedRounds
            );
            """;
        command.Parameters.AddWithValue("$steamId", match.SteamId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$playerName", match.PlayerName);
        command.Parameters.AddWithValue("$mapName", match.MapName);
        command.Parameters.AddWithValue("$recordedAt", match.RecordedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$roundsPlayed", match.RoundsPlayed);
        command.Parameters.AddWithValue("$rating", match.Rating);
        command.Parameters.AddWithValue("$impact", match.Impact);
        command.Parameters.AddWithValue("$kast", match.Kast);
        command.Parameters.AddWithValue("$adr", match.Adr);
        command.Parameters.AddWithValue("$killsPerRound", match.KillsPerRound);
        command.Parameters.AddWithValue("$deathsPerRound", match.DeathsPerRound);
        command.Parameters.AddWithValue("$assistsPerRound", match.AssistsPerRound);
        command.Parameters.AddWithValue("$kills", match.Kills);
        command.Parameters.AddWithValue("$deaths", match.Deaths);
        command.Parameters.AddWithValue("$assists", match.Assists);
        command.Parameters.AddWithValue("$damage", match.Damage);
        command.Parameters.AddWithValue("$headshots", match.Headshots);
        command.Parameters.AddWithValue("$entryKills", match.EntryKills);
        command.Parameters.AddWithValue("$entryDeaths", match.EntryDeaths);
        command.Parameters.AddWithValue("$multiKillRounds", match.MultiKillRounds);
        command.Parameters.AddWithValue("$clutchesWon", match.ClutchesWon);
        command.Parameters.AddWithValue("$kastRounds", match.KastRounds);
        command.Parameters.AddWithValue("$survivedRounds", match.SurvivedRounds);
        command.ExecuteNonQuery();
    }

    private static void PruneHistory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ulong steamId,
        int historyLimit)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DELETE FROM player_rating_matches
            WHERE steam_id = $steamId
              AND id NOT IN (
                  SELECT id
                  FROM player_rating_matches
                  WHERE steam_id = $steamId
                  ORDER BY id DESC
                  LIMIT $historyLimit
              );
            """;
        command.Parameters.AddWithValue("$steamId", steamId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$historyLimit", historyLimit);
        command.ExecuteNonQuery();
    }

    private static void RefreshSummary(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ulong steamId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO player_rating_summary (
                steam_id, last_known_name, rounds_recorded, matches_recorded,
                history_count, rating, impact, kast, adr, kills_per_round,
                deaths_per_round, assists_per_round, total_kills, total_deaths,
                total_assists, total_damage, total_entry_kills, total_entry_deaths,
                total_headshots, multi_kill_rounds, clutches_won, last_map,
                last_updated_utc
            )
            SELECT
                aggregate.steam_id, latest.player_name, aggregate.rounds_recorded,
                aggregate.history_count, aggregate.history_count, aggregate.rating,
                aggregate.impact, aggregate.kast, aggregate.adr,
                aggregate.kills_per_round, aggregate.deaths_per_round,
                aggregate.assists_per_round, aggregate.total_kills,
                aggregate.total_deaths, aggregate.total_assists,
                aggregate.total_damage, aggregate.total_entry_kills,
                aggregate.total_entry_deaths, aggregate.total_headshots,
                aggregate.multi_kill_rounds, aggregate.clutches_won,
                latest.map_name, latest.recorded_at_utc
            FROM (
                SELECT
                    steam_id,
                    COUNT(*) AS history_count,
                    COALESCE(SUM(rounds_played), 0) AS rounds_recorded,
                    COALESCE(AVG(rating), 0) AS rating,
                    COALESCE(AVG(impact), 0) AS impact,
                    COALESCE(AVG(kast), 0) AS kast,
                    COALESCE(AVG(adr), 0) AS adr,
                    COALESCE(AVG(kills_per_round), 0) AS kills_per_round,
                    COALESCE(AVG(deaths_per_round), 0) AS deaths_per_round,
                    COALESCE(AVG(assists_per_round), 0) AS assists_per_round,
                    COALESCE(SUM(kills), 0) AS total_kills,
                    COALESCE(SUM(deaths), 0) AS total_deaths,
                    COALESCE(SUM(assists), 0) AS total_assists,
                    COALESCE(SUM(damage), 0) AS total_damage,
                    COALESCE(SUM(entry_kills), 0) AS total_entry_kills,
                    COALESCE(SUM(entry_deaths), 0) AS total_entry_deaths,
                    COALESCE(SUM(headshots), 0) AS total_headshots,
                    COALESCE(SUM(multi_kill_rounds), 0) AS multi_kill_rounds,
                    COALESCE(SUM(clutches_won), 0) AS clutches_won
                FROM player_rating_matches
                WHERE steam_id = $steamId
                GROUP BY steam_id
            ) AS aggregate
            JOIN (
                SELECT player_name, map_name, recorded_at_utc
                FROM player_rating_matches
                WHERE steam_id = $steamId
                ORDER BY id DESC
                LIMIT 1
            ) AS latest ON 1 = 1
            ON CONFLICT(steam_id) DO UPDATE SET
                last_known_name = excluded.last_known_name,
                rounds_recorded = excluded.rounds_recorded,
                matches_recorded = excluded.matches_recorded,
                history_count = excluded.history_count,
                rating = excluded.rating,
                impact = excluded.impact,
                kast = excluded.kast,
                adr = excluded.adr,
                kills_per_round = excluded.kills_per_round,
                deaths_per_round = excluded.deaths_per_round,
                assists_per_round = excluded.assists_per_round,
                total_kills = excluded.total_kills,
                total_deaths = excluded.total_deaths,
                total_assists = excluded.total_assists,
                total_damage = excluded.total_damage,
                total_entry_kills = excluded.total_entry_kills,
                total_entry_deaths = excluded.total_entry_deaths,
                total_headshots = excluded.total_headshots,
                multi_kill_rounds = excluded.multi_kill_rounds,
                clutches_won = excluded.clutches_won,
                last_map = excluded.last_map,
                last_updated_utc = excluded.last_updated_utc;
            """;
        command.Parameters.AddWithValue("$steamId", steamId.ToString(CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static void ValidateMatch(PlayerMatchWrite match)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentException.ThrowIfNullOrWhiteSpace(match.PlayerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(match.MapName);
        if (match.RoundsPlayed <= 0
            || match.Kills < 0
            || match.Deaths < 0
            || match.Assists < 0
            || match.Damage < 0
            || !double.IsFinite(match.Rating)
            || !double.IsFinite(match.Impact)
            || !double.IsFinite(match.Kast)
            || !double.IsFinite(match.Adr))
        {
            throw new ArgumentException("Player match data is invalid.", nameof(match));
        }
    }

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var timestamp)
            ? timestamp
            : DateTimeOffset.UnixEpoch;
}
