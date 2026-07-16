using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TransmissionRss.IntegrationTests;

public sealed class ServerTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"transmission-rss-{Guid.NewGuid():N}.db");
    private TestServerFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE activity (id INTEGER PRIMARY KEY, message TEXT NOT NULL)";
            await command.ExecuteNonQueryAsync();
        }

        _factory = new TestServerFactory(_databasePath);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();

        foreach (var path in new[] { _databasePath, _databasePath + "-shm", _databasePath + "-wal" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task DashboardAndHealthAreAvailable()
    {
        var health = await _client.GetAsync("/health");
        var dashboard = await _client.GetAsync("/");
        var html = await dashboard.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Contains("RSS feeds and rules", html);
        Assert.Contains("No feeds yet.", html);
        Assert.DoesNotContain("Activity", html);
        Assert.Contains("<link rel=\"icon\" href=\"/favicon.svg\" type=\"image/svg+xml\"", html);

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'activity'";
        Assert.Equal(0L, await command.ExecuteScalarAsync());

        var favicon = await _client.GetAsync("/favicon.svg");
        Assert.Equal(HttpStatusCode.OK, favicon.StatusCode);
        Assert.Equal("image/svg+xml", favicon.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task FeedCanOwnMultipleCaseInsensitiveMatchRules()
    {
        var feedId = await CreateFeedAsync("https://nyaa.si/?page=rss", "nyaa_infohash", true);

        var first = await PostFormAsync($"/feeds/{feedId}/rules", new()
        {
            ["name"] = "Girls und Panzer",
            ["includes"] = "submarine\r\npanzer\r\nmllsd\r\nPANZER",
            ["excludes"] = "dubbed\r\n720p",
            ["downloadDirectory"] = "/share/Movies/Girls und Panzer",
            ["enabled"] = "on"
        });
        var second = await PostFormAsync($"/feeds/{feedId}/rules", new()
        {
            ["name"] = "Link Click",
            ["includes"] = "toonshub\nlink\nclick",
            ["downloadDirectory"] = "/share/Movies/Link Click/Link Click S3",
            ["addPaused"] = "true",
            ["enabled"] = "on"
        });

        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);

        var repository = _factory.Services.GetRequiredService<AppRepository>();
        var feedResult = await repository.GetFeedAsync(feedId);
        var feed = Assert.IsType<RepositoryResult<Feed?>>(feedResult).Value!;
        Assert.Equal("nyaa_infohash", feed.LinkField);
        Assert.True(feed.SeenByGuid);
        Assert.Equal(2, feed.Rules.Count);
        var panzer = feed.Rules.Single(rule => rule.Name == "Girls und Panzer");
        Assert.Equal(["submarine", "panzer", "mllsd"], panzer.Includes);
        Assert.Equal(["dubbed", "720p"], panzer.Excludes);

        var html = await _client.GetStringAsync("/");
        Assert.Contains("https://nyaa.si/?page=rss", html);
        Assert.Contains("2 rules", html);
        Assert.Contains("Girls und Panzer", html);
        Assert.Contains("Link Click", html);
    }

    [Fact]
    public async Task FeedAndRuleEditorsSeparateTheirResponsibilities()
    {
        var newFeed = await _client.GetStringAsync("/feeds/new");
        Assert.Contains("action=\"/feeds\"", newFeed);
        Assert.Contains("name=\"url\"", newFeed);
        Assert.Contains("name=\"linkField\"", newFeed);
        Assert.DoesNotContain("name=\"includes\"", newFeed);

        var feedId = await CreateFeedAsync("https://nyaa.si/?page=rss", "link", false);
        var newRule = await _client.GetStringAsync($"/feeds/{feedId}/rules/new");
        Assert.Contains($"action=\"/feeds/{feedId}/rules\"", newRule);
        Assert.Contains("name=\"includes\"", newRule);
        Assert.Contains("name=\"excludes\"", newRule);
        Assert.Contains("Every term must occur; matching ignores case.", newRule);
        Assert.DoesNotContain("name=\"url\"", newRule);
        Assert.DoesNotContain("regex", newRule, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidFeedUrlAndBlankRuleNameReturnEditorErrors()
    {
        var badFeed = await PostFormAsync("/feeds", new() { ["url"] = "not-a-url" });
        var badFeedHtml = await badFeed.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, badFeed.StatusCode);
        Assert.Contains("Feed URL must be an absolute HTTP(S) URL.", badFeedHtml);

        var feedId = await CreateFeedAsync("https://example.com/rss", "link", false);
        var badRule = await PostFormAsync($"/feeds/{feedId}/rules", new()
        {
            ["name"] = " ",
            ["includes"] = "show"
        });
        var badRuleHtml = await badRule.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, badRule.StatusCode);
        Assert.Contains("Name is required.", badRuleHtml);
        Assert.Contains(">show</textarea>", badRuleHtml);
    }

    [Fact]
    public async Task SettingsAndDownloadedTorrentHistoryWorkWithNestedRules()
    {
        var settings = await PostFormAsync("/settings", new()
        {
            ["transmissionUrl"] = "http://transmission:9091/transmission/rpc",
            ["username"] = "test-user",
            ["password"] = "test-password",
            ["pollInterval"] = "120",
            ["timeout"] = "15",
            ["addPaused"] = "on"
        });
        Assert.Equal(HttpStatusCode.Redirect, settings.StatusCode);

        var repository = _factory.Services.GetRequiredService<AppRepository>();
        var feed = new Feed("feed", "https://nyaa.si/?page=rss", "link", false, []);
        var rule = new FeedRule("rule", feed.Id, "Nyaa weekly", ["1080p"], ["CAM"],
            "/downloads/anime", null, true, null, string.Empty);
        Assert.IsType<SuccessRepositoryResult>(await repository.SaveFeedAsync(feed));
        Assert.IsType<SuccessRepositoryResult>(await repository.SaveRuleAsync(rule));
        Assert.IsType<SuccessRepositoryResult>(await repository.MarkSeenAsync(
            rule, "torrent-guid", "Example torrent", "https://nyaa.si/download/1.torrent", false));

        var dashboard = await _client.GetStringAsync("/");
        Assert.Contains("http://transmission:9091/transmission/rpc", dashboard);
        Assert.DoesNotContain("test-password", dashboard);

        var downloads = await _client.GetStringAsync("/downloads");
        Assert.Contains("Example torrent", downloads);
        Assert.Contains("/downloads/anime", downloads);
        Assert.Contains("name=\"ruleId\" type=\"hidden\" value=\"rule\"", downloads);
    }

    [Fact]
    public async Task DeletingFeedCascadesRulesAndHistory()
    {
        var repository = _factory.Services.GetRequiredService<AppRepository>();
        var feed = new Feed("feed-delete", "https://example.com/rss", "link", false, []);
        var rule = new FeedRule("rule-delete", feed.Id, "Delete me", [], [], string.Empty,
            null, true, null, string.Empty);
        Assert.IsType<SuccessRepositoryResult>(await repository.SaveFeedAsync(feed));
        Assert.IsType<SuccessRepositoryResult>(await repository.SaveRuleAsync(rule));
        Assert.IsType<SuccessRepositoryResult>(await repository.MarkSeenAsync(
            rule, "item", "Title", "https://example.com/file.torrent", false));

        var response = await PostFormAsync($"/feeds/{feed.Id}/delete", []);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Null(Assert.IsType<RepositoryResult<Feed?>>(await repository.GetFeedAsync(feed.Id)).Value);
        Assert.Null(Assert.IsType<RepositoryResult<FeedRule?>>(await repository.GetRuleAsync(rule.Id)).Value);
        Assert.False(Assert.IsType<RepositoryResult<bool>>(await repository.HasSeenAsync(rule.Id, "item")).Value);
    }

    [Fact]
    public async Task RazorSlicesEncodeUserControlledValues()
    {
        var repository = _factory.Services.GetRequiredService<AppRepository>();
        var feed = new Feed("encoded-feed", "https://example.com/rss?x=<script>", "link", false, []);
        var rule = new FeedRule("encoded-rule", feed.Id, "<script>alert('rule')</script>", [], [],
            string.Empty, null, true, null, string.Empty);
        Assert.IsType<SuccessRepositoryResult>(await repository.SaveFeedAsync(feed));
        Assert.IsType<SuccessRepositoryResult>(await repository.SaveRuleAsync(rule));

        var dashboardHtml = await _client.GetStringAsync("/?notice=%3Cscript%3Ealert('notice')%3C%2Fscript%3E");
        var editHtml = await _client.GetStringAsync($"/rules/{rule.Id}");
        Assert.Contains("&lt;script&gt;", dashboardHtml);
        Assert.Contains("&lt;script&gt;", editHtml);
        Assert.DoesNotContain("<script>alert", dashboardHtml);
        Assert.DoesNotContain("<script>alert", editHtml);
    }

    [Fact]
    public async Task RepositoryReturnsCanceledResultWhenOperationIsCanceled()
    {
        var repository = _factory.Services.GetRequiredService<AppRepository>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await repository.GetFeedsAsync(cancellationToken: cancellation.Token);
        Assert.IsType<CanceledRepositoryResult>(result);
    }

    private async Task<string> CreateFeedAsync(string url, string linkField, bool seenByGuid)
    {
        var values = new Dictionary<string, string> { ["url"] = url, ["linkField"] = linkField };

        if (seenByGuid)
        {
            values["seenByGuid"] = "on";
        }

        var response = await PostFormAsync("/feeds", values);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.OriginalString.Split('/')[2].Split('?')[0];
    }

    private Task<HttpResponseMessage> PostFormAsync(string path, Dictionary<string, string> values) =>
        _client.PostAsync(path, new FormUrlEncodedContent(values));

    private sealed class TestServerFactory(string databasePath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = databasePath,
                ["Polling:Disabled"] = "true"
            }));
        }
    }
}
