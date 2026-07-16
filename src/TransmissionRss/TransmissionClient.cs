using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TransmissionRss;

public sealed class TransmissionClient(
    IHttpClientFactory httpClientFactory,
    ILogger<TransmissionClient> logger)
{
    public async Task<ServiceResult> AddTorrentAsync(
        AppSettings settings,
        FeedRule rule,
        string torrentUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
            var client = httpClientFactory.CreateClient(nameof(TransmissionClient));

            if (!string.IsNullOrEmpty(settings.Username))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{settings.Password}")));
            }

            var arguments = new TorrentAddArguments(
                torrentUrl,
                rule.AddPaused ?? settings.AddPaused,
                string.IsNullOrWhiteSpace(rule.DownloadDirectory) ? null : rule.DownloadDirectory);
            var request = new TransmissionRequest("torrent-add", arguments);
            var response = await SendAsync(client, settings.TransmissionUrl, request, null, timeout.Token);

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var sessionId = GetSessionId(response);
                response.Dispose();
                response = await SendAsync(client, settings.TransmissionUrl, request, sessionId, timeout.Token);
            }

            using (response)
            {
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
                var result = json.RootElement.TryGetProperty("result", out var resultElement) ? resultElement.GetString() : null;

                if (!string.Equals(result, "success", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Transmission returned '{result ?? "an invalid response"}'.");
                }
            }

            return new SuccessServiceResult();
        }
        catch (OperationCanceledException)
        {
            return new CanceledServiceResult();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to add torrent {TorrentUrl} for rule {RuleName}", torrentUrl, rule.Name);
            return new ErrorServiceResult();
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string url, TransmissionRequest body, string? sessionId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, AppJsonContext.Default.TransmissionRequest)
        };

        if (!string.IsNullOrEmpty(sessionId))
        {
            request.Headers.TryAddWithoutValidation("X-Transmission-Session-Id", sessionId);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private static string GetSessionId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Transmission-Session-Id", out var values) ? values.First() :
        throw new InvalidOperationException("Transmission did not provide a session ID.");
}

public sealed record TransmissionRequest(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("arguments")] TorrentAddArguments Arguments);

public sealed record TorrentAddArguments(
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("paused")] bool Paused,
    [property: JsonPropertyName("download-dir"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DownloadDirectory);

[JsonSerializable(typeof(TransmissionRequest))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
