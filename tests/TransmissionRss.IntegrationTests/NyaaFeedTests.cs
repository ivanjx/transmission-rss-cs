using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TransmissionRss;
using Xunit;

namespace TransmissionRss.IntegrationTests;

public sealed class NyaaFeedTests
{
    private const string NyaaRss = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss xmlns:atom="http://www.w3.org/2005/Atom" xmlns:nyaa="https://nyaa.si/xmlns/nyaa" version="2.0">
          <channel>
            <title>Nyaa - Home - Torrent File RSS</title>
            <link>https://nyaa.si/</link>
            <atom:link href="https://nyaa.si/?page=rss" rel="self" type="application/rss+xml" />
            <item>
              <title>Example Show S01E03 1080p WEB x264</title>
              <link>https://nyaa.si/download/2007666.torrent</link>
              <guid isPermaLink="true">https://nyaa.si/view/2007666</guid>
              <pubDate>Sun, 17 Aug 2025 18:00:37 -0000</pubDate>
              <nyaa:infoHash>957448e40d163af61b57cf05fa25ec92bc55ea7c</nyaa:infoHash>
              <nyaa:categoryId>1_2</nyaa:categoryId>
              <nyaa:size>564.0 MiB</nyaa:size>
            </item>
            <item>
              <title>Example Show S01E03 720p WEB x264</title>
              <link>https://nyaa.si/download/2007665.torrent</link>
              <guid isPermaLink="true">https://nyaa.si/view/2007665</guid>
              <nyaa:infoHash>5616f794088be7437993ce01139e3f5afc4fd32d</nyaa:infoHash>
            </item>
          </channel>
        </rss>
        """;

    [Fact]
    public async Task ReadsTorrentLinksAndUsesGuidForDeduplication()
    {
        var reader = CreateReader();
        var result = await reader.ReadAsync(CreateRule("link", seenByGuid: true), 5, CancellationToken.None);
        var entries = Assert.IsType<FeedEntriesResult>(result).Entries;

        Assert.Equal(2, entries.Count);
        Assert.Equal("Example Show S01E03 1080p WEB x264", entries[0].Title);
        Assert.Equal("https://nyaa.si/download/2007666.torrent", entries[0].TorrentUrl);
        Assert.Equal("https://nyaa.si/view/2007666", entries[0].UniqueId);
    }

    [Fact]
    public async Task ReadsNyaaNamespacedInfoHashAsCustomLinkField()
    {
        var reader = CreateReader();
        var result = await reader.ReadAsync(CreateRule("infoHash", seenByGuid: false), 5, CancellationToken.None);
        var entries = Assert.IsType<FeedEntriesResult>(result).Entries;

        Assert.Equal(2, entries.Count);
        Assert.Equal("957448e40d163af61b57cf05fa25ec92bc55ea7c", entries[0].TorrentUrl);
        Assert.Equal(entries[0].TorrentUrl, entries[0].UniqueId);
    }

    [Fact]
    public async Task ReturnsCanceledResultWhenFeedReadIsCanceled()
    {
        var reader = CreateReader();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await reader.ReadAsync(CreateRule("link", seenByGuid: true), 5, cancellation.Token);

        Assert.IsType<CanceledServiceResult>(result);
    }

    [Fact]
    public async Task ReturnsErrorResultWhenFeedRequestFails()
    {
        var reader = new FeedReader(
            new StubHttpClientFactory(new HttpClient(new FailedFeedHandler())),
            NullLogger<FeedReader>.Instance);

        var result = await reader.ReadAsync(CreateRule("link", seenByGuid: true), 5, CancellationToken.None);

        Assert.IsType<ErrorServiceResult>(result);
    }

    private static FeedReader CreateReader() => new(
        new StubHttpClientFactory(new HttpClient(new NyaaFeedHandler())),
        NullLogger<FeedReader>.Instance);

    private static Feed CreateRule(string linkField, bool seenByGuid) => new(
        "nyaa", "https://nyaa.si/?page=rss", linkField, seenByGuid, []);

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class NyaaFeedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(NyaaRss, Encoding.UTF8, "application/rss+xml")
            });
    }

    private sealed class FailedFeedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
    }
}
