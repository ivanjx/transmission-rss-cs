using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TransmissionRss;
using Xunit;

namespace TransmissionRss.IntegrationTests;

public sealed class TransmissionClientTests
{
    [Fact]
    public async Task RetriesWithTransmissionSessionIdAndSendsTorrentOptions()
    {
        var handler = new TransmissionHandler();
        var client = new TransmissionClient(
            new StubHttpClientFactory(new HttpClient(handler)),
            NullLogger<TransmissionClient>.Instance);
        var settings = new AppSettings("http://transmission:9091/transmission/rpc", "alice", "secret", 60, 5, false);
        var rule = new FeedRule("rule", "feed", "Shows", [], [],
            "/downloads/shows", true, true);

        var result = await client.AddTorrentAsync(
            settings,
            rule,
            "https://example.com/show.torrent",
            CancellationToken.None);

        Assert.IsType<SuccessServiceResult>(result);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Null(handler.Requests[0].SessionId);
        Assert.Equal("test-session", handler.Requests[1].SessionId);
        Assert.Equal("Basic", handler.Requests[1].Authorization?.Scheme);
        Assert.Equal("YWxpY2U6c2VjcmV0", handler.Requests[1].Authorization?.Parameter);

        using var payload = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("torrent-add", payload.RootElement.GetProperty("method").GetString());
        var arguments = payload.RootElement.GetProperty("arguments");
        Assert.Equal("https://example.com/show.torrent", arguments.GetProperty("filename").GetString());
        Assert.True(arguments.GetProperty("paused").GetBoolean());
        Assert.Equal("/downloads/shows", arguments.GetProperty("download-dir").GetString());
    }

    [Fact]
    public async Task ReturnsCanceledResultWhenRequestIsCanceled()
    {
        var client = new TransmissionClient(
            new StubHttpClientFactory(new HttpClient(new TransmissionHandler())),
            NullLogger<TransmissionClient>.Instance);
        var settings = new AppSettings("http://transmission:9091/transmission/rpc", string.Empty, string.Empty, 60, 5, false);
        var rule = new FeedRule("rule", "feed", "Shows", [], [],
            string.Empty, null, true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await client.AddTorrentAsync(
            settings,
            rule,
            "https://example.com/show.torrent",
            cancellation.Token);

        Assert.IsType<CanceledServiceResult>(result);
    }

    [Fact]
    public async Task ReturnsErrorResultWhenTransmissionRejectsTorrent()
    {
        var client = new TransmissionClient(
            new StubHttpClientFactory(new HttpClient(new RejectedTorrentHandler())),
            NullLogger<TransmissionClient>.Instance);
        var settings = new AppSettings("http://transmission:9091/transmission/rpc", string.Empty, string.Empty, 60, 5, false);
        var rule = new FeedRule("rule", "feed", "Shows", [], [],
            string.Empty, null, true);

        var result = await client.AddTorrentAsync(
            settings,
            rule,
            "https://example.com/show.torrent",
            CancellationToken.None);

        Assert.IsType<ErrorServiceResult>(result);
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TransmissionHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(
                request.Headers.TryGetValues("X-Transmission-Session-Id", out var values) ? values.Single() : null,
                request.Headers.Authorization,
                await request.Content!.ReadAsStringAsync(cancellationToken)));

            if (Requests.Count == 1)
            {
                var conflict = new HttpResponseMessage(HttpStatusCode.Conflict);
                conflict.Headers.Add("X-Transmission-Session-Id", "test-session");
                return conflict;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"result\":\"success\"}")
            };
        }
    }

    private sealed class RejectedTorrentHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"result\":\"duplicate torrent\"}")
            });
    }

    private sealed record CapturedRequest(string? SessionId, AuthenticationHeaderValue? Authorization, string Body);
}
