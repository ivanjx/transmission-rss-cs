using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TransmissionRss;
using Xunit;

namespace TransmissionRss.IntegrationTests;

public sealed class AppRepositoryMigrationTests
{
    [Fact]
    public async Task MigratesFirstVersionDatabaseAndPreservesData()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"transmission-rss-migration-{Guid.NewGuid():N}.db");

        try
        {
            await CreateFirstVersionDatabaseAsync(databasePath);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = databasePath })
                .Build();
            var repository = new AppRepository(configuration, NullLogger<AppRepository>.Instance);

            Assert.IsType<SuccessRepositoryResult>(await repository.InitializeAsync());

            var feed = Assert.IsType<RepositoryResult<Feed?>>(await repository.GetFeedAsync("feed")).Value!;
            Assert.Equal("https://example.com/rss", feed.Url);
            Assert.Single(feed.Rules);
            Assert.Null(feed.LastCheckedItemCount);

            Assert.IsType<SuccessRepositoryResult>(await repository.SetFeedCheckedItemCountAsync("feed", 75));
            feed = Assert.IsType<RepositoryResult<Feed?>>(await repository.GetFeedAsync("feed")).Value!;
            Assert.Equal(75, feed.LastCheckedItemCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();

            foreach (var path in new[] { databasePath, databasePath + "-shm", databasePath + "-wal" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    private static async Task CreateFirstVersionDatabaseAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE feeds (
                id TEXT PRIMARY KEY,
                url TEXT NOT NULL,
                link_field TEXT NOT NULL,
                seen_by_guid INTEGER NOT NULL
            );
            CREATE TABLE rules (
                id TEXT PRIMARY KEY,
                feed_id TEXT NOT NULL,
                name TEXT NOT NULL,
                includes TEXT NOT NULL,
                excludes TEXT NOT NULL,
                download_directory TEXT NOT NULL,
                add_paused INTEGER NULL,
                enabled INTEGER NOT NULL,
                last_checked_at TEXT NULL,
                last_status TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (feed_id) REFERENCES feeds(id) ON DELETE CASCADE
            );
            CREATE TABLE seen_items (
                rule_id TEXT NOT NULL,
                unique_id TEXT NOT NULL,
                seen_at TEXT NOT NULL,
                title TEXT NULL,
                torrent_url TEXT NULL,
                rule_name TEXT NULL,
                download_directory TEXT NULL,
                add_paused INTEGER NULL,
                PRIMARY KEY (rule_id, unique_id),
                FOREIGN KEY (rule_id) REFERENCES rules(id) ON DELETE CASCADE
            );
            INSERT INTO feeds VALUES ('feed', 'https://example.com/rss', 'link', 0);
            INSERT INTO rules VALUES ('rule', 'feed', 'Example rule', 'show', '', '', NULL, 1,
                '2026-07-17T00:00:00.0000000+00:00', 'Checked 75 item(s), added 0.');
            """;
        await command.ExecuteNonQueryAsync();
    }
}
