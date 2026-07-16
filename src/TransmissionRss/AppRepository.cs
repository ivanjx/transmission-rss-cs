using Microsoft.Data.Sqlite;

namespace TransmissionRss;

public sealed class AppRepository(IConfiguration configuration, ILogger<AppRepository> logger)
{
    private const int DownloadHistoryLimit = 1000;
    private readonly string _connectionString = BuildConnectionString(configuration);

    public async Task<RepositoryResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder(_connectionString);
            var directory = Path.GetDirectoryName(builder.DataSource);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var connection = await OpenAsync(cancellationToken);
            await ExecuteAsync(connection, """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS settings (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    transmission_url TEXT NOT NULL,
                    username TEXT NOT NULL,
                    password TEXT NOT NULL,
                    poll_interval_seconds INTEGER NOT NULL,
                    request_timeout_seconds INTEGER NOT NULL,
                    add_paused INTEGER NOT NULL
                );
                INSERT OR IGNORE INTO settings VALUES (1, 'http://localhost:9091/transmission/rpc', '', '', 600, 30, 0);
                """, cancellationToken);

            await CreateCurrentSchemaAsync(connection, cancellationToken);
            logger.LogInformation("Database initialized");
            return new SuccessRepositoryResult();
        }
        catch (OperationCanceledException)
        {
            return new CanceledRepositoryResult();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to initialize database");
            return new ErrorRepositoryResult();
        }
    }

    public Task<RepositoryResult> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        ExecuteRepositoryAsync(async () =>
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT transmission_url, username, password, poll_interval_seconds, request_timeout_seconds, add_paused FROM settings WHERE id = 1";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                return new ErrorRepositoryResult();
            }

            return new RepositoryResult<AppSettings>(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetBoolean(5)));
        }, "Failed to read settings", cancellationToken);

    public Task<RepositoryResult> SaveSettingsAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default) =>
        ExecuteRepositoryAsync(async () =>
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE settings SET transmission_url = $url, username = $username, password = $password,
                    poll_interval_seconds = $interval, request_timeout_seconds = $timeout, add_paused = $paused
                WHERE id = 1
                """;
            command.Parameters.AddWithValue("$url", settings.TransmissionUrl);
            command.Parameters.AddWithValue("$username", settings.Username);
            command.Parameters.AddWithValue("$password", settings.Password);
            command.Parameters.AddWithValue("$interval", settings.PollIntervalSeconds);
            command.Parameters.AddWithValue("$timeout", settings.RequestTimeoutSeconds);
            command.Parameters.AddWithValue("$paused", settings.AddPaused);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new SuccessRepositoryResult();
        }, "Failed to save settings", cancellationToken);

    public Task<RepositoryResult> GetFeedsAsync(
        bool enabledRulesOnly = false,
        CancellationToken cancellationToken = default) =>
        ExecuteRepositoryAsync(async () =>
        {
            var feeds = await ReadFeedsAsync(null, enabledRulesOnly, cancellationToken);
            return new RepositoryResult<IReadOnlyList<Feed>>(feeds);
        }, "Failed to read feeds", cancellationToken);

    public Task<RepositoryResult> GetFeedAsync(string id, CancellationToken cancellationToken = default) =>
        ExecuteRepositoryAsync(async () =>
        {
            var feeds = await ReadFeedsAsync(id, false, cancellationToken);
            return new RepositoryResult<Feed?>(feeds.SingleOrDefault());
        }, $"Failed to read feed {id}", cancellationToken);

    public Task<RepositoryResult> SaveFeedAsync(Feed feed, CancellationToken cancellationToken = default) =>
        ExecuteRepositoryAsync(async () =>
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO feeds (id, url, link_field, seen_by_guid)
                VALUES ($id, $url, $field, $guid)
                ON CONFLICT(id) DO UPDATE SET url = excluded.url, link_field = excluded.link_field,
                    seen_by_guid = excluded.seen_by_guid
                """;
            command.Parameters.AddWithValue("$id", feed.Id);
            command.Parameters.AddWithValue("$url", feed.Url);
            command.Parameters.AddWithValue("$field", feed.LinkField);
            command.Parameters.AddWithValue("$guid", feed.SeenByGuid);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new SuccessRepositoryResult();
        }, $"Failed to save feed {feed.Id}", cancellationToken);

    public Task<RepositoryResult> DeleteFeedAsync(string id, CancellationToken cancellationToken = default) =>
        ExecuteRepositoryAsync(async () =>
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM feeds WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new SuccessRepositoryResult();
        }, $"Failed to delete feed {id}", cancellationToken);

    public Task<RepositoryResult> GetRuleAsync(string id, CancellationToken cancellationToken = default) =>
        ExecuteRepositoryAsync(async () =>
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, feed_id, name, includes, excludes, download_directory, add_paused, enabled,
                    last_checked_at, last_status
                FROM rules WHERE id = $id
                """;
            command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rule = await reader.ReadAsync(cancellationToken) ? ReadRule(reader, 0) : null;
            return new RepositoryResult<FeedRule?>(rule);
        }, $"Failed to read feed rule {id}", cancellationToken);

    public Task<RepositoryResult> SaveRuleAsync(FeedRule rule, CancellationToken cancellationToken = default) =>
        ExecuteRepositoryAsync(async () =>
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO rules (id, feed_id, name, includes, excludes, download_directory, add_paused,
                    enabled, last_checked_at, last_status)
                VALUES ($id, $feed, $name, $includes, $excludes, $directory, $paused, $enabled, $checked, $status)
                ON CONFLICT(id) DO UPDATE SET feed_id = excluded.feed_id, name = excluded.name,
                    includes = excluded.includes, excludes = excluded.excludes,
                    download_directory = excluded.download_directory, add_paused = excluded.add_paused,
                    enabled = excluded.enabled
                """;
            AddRuleParameters(command, rule);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new SuccessRepositoryResult();
        }, $"Failed to save feed rule {rule.Id}", cancellationToken);

    public Task<RepositoryResult> DeleteRuleAsync(string id, CancellationToken cancellationToken = default) =>
        ExecuteRepositoryAsync(async () =>
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM rules WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new SuccessRepositoryResult();
        }, $"Failed to delete feed rule {id}", cancellationToken);

    public Task<RepositoryResult> HasSeenAsync(
        string ruleId,
        string uniqueId,
        CancellationToken cancellationToken = default) =>
        ExecuteRepositoryAsync(async () =>
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM seen_items WHERE rule_id = $rule AND unique_id = $unique LIMIT 1";
            command.Parameters.AddWithValue("$rule", ruleId);
            command.Parameters.AddWithValue("$unique", uniqueId);
            return new RepositoryResult<bool>(await command.ExecuteScalarAsync(cancellationToken) is not null);
        }, $"Failed to read downloaded state for feed rule {ruleId}", cancellationToken);

    public Task<RepositoryResult> MarkSeenAsync(
        FeedRule rule,
        string uniqueId,
        string title,
        string torrentUrl,
        bool addPaused,
        CancellationToken cancellationToken = default) =>
        ExecuteRepositoryAsync(async () =>
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO seen_items
                    (rule_id, unique_id, seen_at, title, torrent_url, rule_name, download_directory, add_paused)
                VALUES ($rule, $unique, $at, $title, $url, $ruleName, $directory, $paused);
                DELETE FROM seen_items
                WHERE rowid NOT IN (SELECT rowid FROM seen_items ORDER BY seen_at DESC, rowid DESC LIMIT $limit);
                """;
            command.Parameters.AddWithValue("$rule", rule.Id);
            command.Parameters.AddWithValue("$unique", uniqueId);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$url", torrentUrl);
            command.Parameters.AddWithValue("$ruleName", rule.Name);
            command.Parameters.AddWithValue("$directory", rule.DownloadDirectory);
            command.Parameters.AddWithValue("$paused", addPaused);
            command.Parameters.AddWithValue("$limit", DownloadHistoryLimit);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new SuccessRepositoryResult();
        }, $"Failed to record downloaded torrent for feed rule {rule.Id}", cancellationToken);

    public Task<RepositoryResult> GetDownloadedTorrentsAsync(CancellationToken cancellationToken = default) =>
        ExecuteRepositoryAsync(async () =>
        {
            var torrents = new List<DownloadedTorrent>();
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT seen.rule_id, COALESCE(seen.rule_name, rules.name), seen.unique_id, seen.title, seen.torrent_url,
                    seen.seen_at, COALESCE(seen.download_directory, ''), COALESCE(seen.add_paused, 0)
                FROM seen_items AS seen
                INNER JOIN rules ON rules.id = seen.rule_id
                WHERE seen.title IS NOT NULL AND seen.torrent_url IS NOT NULL
                ORDER BY seen.seen_at DESC, seen.rowid DESC
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                torrents.Add(new(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5)), reader.GetString(6), reader.GetBoolean(7)));
            }

            return new RepositoryResult<IReadOnlyList<DownloadedTorrent>>(torrents);
        }, "Failed to read downloaded torrent history", cancellationToken);

    public Task<RepositoryResult> DeleteDownloadedTorrentAsync(
        string ruleId,
        string uniqueId,
        CancellationToken cancellationToken = default) =>
        ExecuteRepositoryAsync(async () =>
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM seen_items WHERE rule_id = $rule AND unique_id = $unique";
            command.Parameters.AddWithValue("$rule", ruleId);
            command.Parameters.AddWithValue("$unique", uniqueId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new SuccessRepositoryResult();
        }, $"Failed to delete downloaded torrent for feed rule {ruleId}", cancellationToken);

    public Task<RepositoryResult> ClearDownloadedTorrentsAsync(CancellationToken cancellationToken = default) =>
        ExecuteRepositoryAsync(async () =>
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM seen_items";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new SuccessRepositoryResult();
        }, "Failed to clear downloaded torrent history", cancellationToken);

    public Task<RepositoryResult> SetRuleStatusAsync(
        string ruleId,
        string status,
        CancellationToken cancellationToken = default) =>
        ExecuteRepositoryAsync(async () =>
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE rules SET last_checked_at = $at, last_status = $status WHERE id = $id";
            command.Parameters.AddWithValue("$id", ruleId);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$status", status);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new SuccessRepositoryResult();
        }, $"Failed to update status for feed rule {ruleId}", cancellationToken);

    private async Task<IReadOnlyList<Feed>> ReadFeedsAsync(
        string? feedId,
        bool enabledRulesOnly,
        CancellationToken cancellationToken)
    {
        var builders = new Dictionary<string, FeedBuilder>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT feeds.id, feeds.url, feeds.link_field, feeds.seen_by_guid,
                rules.id, rules.feed_id, rules.name, rules.includes, rules.excludes, rules.download_directory,
                rules.add_paused, rules.enabled, rules.last_checked_at, rules.last_status
            FROM feeds
            LEFT JOIN rules ON rules.feed_id = feeds.id AND ($enabledOnly = 0 OR rules.enabled = 1)
            WHERE ($feedId IS NULL OR feeds.id = $feedId)
            ORDER BY feeds.url COLLATE NOCASE, rules.name COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("$enabledOnly", enabledRulesOnly);
        command.Parameters.AddWithValue("$feedId", feedId is null ? DBNull.Value : feedId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);

            if (!builders.TryGetValue(id, out var builder))
            {
                builder = new(id, reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), []);
                builders.Add(id, builder);
            }

            if (!reader.IsDBNull(4))
            {
                builder.Rules.Add(ReadRule(reader, 4));
            }
        }

        return builders.Values
            .Where(builder => !enabledRulesOnly || builder.Rules.Count > 0)
            .Select(builder => new Feed(builder.Id, builder.Url, builder.LinkField, builder.SeenByGuid, builder.Rules))
            .ToList();
    }

    private async Task<RepositoryResult> ExecuteRepositoryAsync(
        Func<Task<RepositoryResult>> operation,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation();
        }
        catch (OperationCanceledException)
        {
            return new CanceledRepositoryResult();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "{RepositoryError}", errorMessage);
            return new ErrorRepositoryResult();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task CreateCurrentSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
        await ExecuteAsync(connection, """
            PRAGMA foreign_keys = ON;
            CREATE TABLE IF NOT EXISTS feeds (
                id TEXT PRIMARY KEY,
                url TEXT NOT NULL,
                link_field TEXT NOT NULL,
                seen_by_guid INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS rules (
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
            CREATE INDEX IF NOT EXISTS idx_rules_feed_id ON rules(feed_id);
            CREATE TABLE IF NOT EXISTS seen_items (
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
            CREATE INDEX IF NOT EXISTS idx_seen_items_seen_at ON seen_items(seen_at DESC);
            DROP TABLE IF EXISTS activity;
            """, cancellationToken);

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildConnectionString(IConfiguration configuration)
    {
        var path = configuration["Database:Path"] ?? "data/transmission-rss.db";
        return new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(path), ForeignKeys = true }.ToString();
    }

    private static FeedRule ReadRule(SqliteDataReader reader, int offset) => new(
        reader.GetString(offset),
        reader.GetString(offset + 1),
        reader.GetString(offset + 2),
        ParseTerms(reader.GetString(offset + 3)),
        ParseTerms(reader.GetString(offset + 4)),
        reader.GetString(offset + 5),
        reader.IsDBNull(offset + 6) ? null : reader.GetBoolean(offset + 6),
        reader.GetBoolean(offset + 7),
        reader.IsDBNull(offset + 8) ? null : DateTimeOffset.Parse(reader.GetString(offset + 8)),
        reader.GetString(offset + 9));

    private static IReadOnlyList<string> ParseTerms(string value) => value
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static void AddRuleParameters(SqliteCommand command, FeedRule rule)
    {
        command.Parameters.AddWithValue("$id", rule.Id);
        command.Parameters.AddWithValue("$feed", rule.FeedId);
        command.Parameters.AddWithValue("$name", rule.Name);
        command.Parameters.AddWithValue("$includes", string.Join('\n', rule.Includes));
        command.Parameters.AddWithValue("$excludes", string.Join('\n', rule.Excludes));
        command.Parameters.AddWithValue("$directory", rule.DownloadDirectory);
        command.Parameters.AddWithValue("$paused", rule.AddPaused is null ? DBNull.Value : rule.AddPaused.Value);
        command.Parameters.AddWithValue("$enabled", rule.Enabled);
        command.Parameters.AddWithValue("$checked", rule.LastCheckedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$status", rule.LastStatus);
    }

    private sealed record FeedBuilder(
        string Id,
        string Url,
        string LinkField,
        bool SeenByGuid,
        List<FeedRule> Rules);

}
