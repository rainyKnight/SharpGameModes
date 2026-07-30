using Microsoft.Data.Sqlite;
using SharpGameModes.PlayerData.Storage;

namespace SharpGameModes.PlayerData.Tests;

public sealed class PlayerRatingRepositoryTests
{
    [Fact]
    public void EnsureCreated_CreatesLegacyCompatibleSchema()
    {
        using var database = new TemporaryDatabase();
        var repository = new PlayerRatingRepository(database.Path);

        repository.EnsureCreated();

        using var connection = database.Open();
        Assert.Equal(1L, ExecuteScalar(connection, "PRAGMA user_version;"));
        Assert.Equal(23L, ExecuteScalar(
            connection,
            "SELECT COUNT(*) FROM pragma_table_info('player_rating_summary');"));
        Assert.Equal(24L, ExecuteScalar(
            connection,
            "SELECT COUNT(*) FROM pragma_table_info('player_rating_matches');"));
    }

    [Fact]
    public void LoadAll_ReadsLegacySummaryWithoutConversion()
    {
        using var database = new TemporaryDatabase();
        var repository = new PlayerRatingRepository(database.Path);
        repository.EnsureCreated();
        using (var connection = database.Open())
        {
            InsertSummary(connection, "76561198000000001", "Player One", 1.2345);
        }

        var ratings = repository.LoadAll();

        var rating = Assert.Single(ratings).Value;
        Assert.Equal(76561198000000001UL, rating.SteamId);
        Assert.Equal("Player One", rating.LastKnownName);
        Assert.Equal(80, rating.RoundsRecorded);
        Assert.Equal(5, rating.HistoryCount);
        Assert.Equal(1.2345, rating.Rating, precision: 10);
        Assert.Equal("de_mirage", rating.LastMap);
        Assert.Equal(DateTimeOffset.Parse("2026-07-23T00:00:00Z"), rating.LastUpdatedAt);
    }

    [Fact]
    public void LoadAll_SkipsMalformedSteamIdRows()
    {
        using var database = new TemporaryDatabase();
        var repository = new PlayerRatingRepository(database.Path);
        repository.EnsureCreated();
        using (var connection = database.Open())
        {
            InsertSummary(connection, "not-a-steam-id", "Broken", 2.0);
        }

        Assert.Empty(repository.LoadAll());
    }

    [Fact]
    public void WriteMatches_PrunesHistoryAndRefreshesLegacySummary()
    {
        using var database = new TemporaryDatabase();
        var repository = new PlayerRatingRepository(database.Path);
        var matches = Enumerable.Range(0, 105)
            .Select(index => Match(
                76561198000000001UL,
                $"Player {index}",
                rating: index,
                recordedAt: DateTimeOffset.UnixEpoch.AddDays(index)))
            .ToArray();

        repository.WriteMatches(matches, historyLimit: 100);

        var summary = Assert.Single(repository.LoadAll()).Value;
        Assert.Equal(100, summary.HistoryCount);
        Assert.Equal(100, summary.MatchesRecorded);
        Assert.Equal(1000, summary.RoundsRecorded);
        Assert.Equal(54.5, summary.Rating, precision: 10);
        Assert.Equal("Player 104", summary.LastKnownName);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(104), summary.LastUpdatedAt);
        using var connection = database.Open();
        Assert.Equal(100L, ExecuteScalar(connection, "SELECT COUNT(*) FROM player_rating_matches;"));
    }

    [Fact]
    public void WriteMatches_UpdatesMultiplePlayersInOneTransaction()
    {
        using var database = new TemporaryDatabase();
        var repository = new PlayerRatingRepository(database.Path);

        repository.WriteMatches(
        [
            Match(76561198000000001UL, "One", 1.1, DateTimeOffset.UtcNow),
            Match(76561198000000002UL, "Two", 1.2, DateTimeOffset.UtcNow),
        ]);

        Assert.Equal(2, repository.LoadAll().Count);
    }

    private static void InsertSummary(SqliteConnection connection, string steamId, string name, double rating)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO player_rating_summary VALUES (
                $steamId, $name, 80, 5, 5, $rating, 1.1, 0.72, 84.5,
                0.8, 0.6, 0.3, 64, 48, 24, 6760, 12, 9, 7, 18, 2,
                'de_mirage', '2026-07-23T00:00:00Z'
            );
            """;
        command.Parameters.AddWithValue("$steamId", steamId);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$rating", rating);
        command.ExecuteNonQuery();
    }

    private static PlayerMatchWrite Match(
        ulong steamId,
        string name,
        double rating,
        DateTimeOffset recordedAt)
        => new(
            steamId,
            name,
            "de_mirage",
            recordedAt,
            RoundsPlayed: 10,
            Rating: rating,
            Impact: 1.0,
            Kast: 0.7,
            Adr: 80,
            KillsPerRound: 0.8,
            DeathsPerRound: 0.6,
            AssistsPerRound: 0.2,
            Kills: 8,
            Deaths: 6,
            Assists: 2,
            Damage: 800,
            Headshots: 4,
            EntryKills: 1,
            EntryDeaths: 1,
            MultiKillRounds: 2,
            ClutchesWon: 0,
            KastRounds: 7,
            SurvivedRounds: 4);

    private static long ExecuteScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"sharp-gamemodes-player-data-{Guid.NewGuid():N}.db");
        }

        public string Path { get; }

        public SqliteConnection Open()
        {
            var connection = new SqliteConnection($"Data Source={Path}");
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            File.Delete(Path);
            File.Delete($"{Path}-wal");
            File.Delete($"{Path}-shm");
        }
    }
}
