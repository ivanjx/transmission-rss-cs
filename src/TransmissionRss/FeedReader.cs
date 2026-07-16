using System.Xml;
using System.Xml.Linq;

namespace TransmissionRss;

public sealed class FeedReader(IHttpClientFactory httpClientFactory, ILogger<FeedReader> logger)
{
    private const int MaximumFeedBytes = 10 * 1024 * 1024;

    public async Task<ServiceResult> ReadAsync(
        Feed feed,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var client = httpClientFactory.CreateClient(nameof(FeedReader));
            using var response = await client.GetAsync(feed.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength > MaximumFeedBytes)
            {
                throw new InvalidDataException("Feed exceeds the 10 MB limit.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
            await using var limited = new LimitedReadStream(source, MaximumFeedBytes);
            using var xmlReader = XmlReader.Create(limited, new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumFeedBytes
            });
            var document = await XDocument.LoadAsync(xmlReader, LoadOptions.None, timeout.Token);
            var elements = document.Descendants().Where(element => element.Name.LocalName is "item" or "entry");
            var entries = new List<FeedEntry>();

            foreach (var element in elements)
            {
                var title = Value(element, "title") ?? string.Empty;
                var torrentUrl = FindLink(element, feed.LinkField);

                if (string.IsNullOrWhiteSpace(torrentUrl))
                {
                    continue;
                }

                var guid = Value(element, "guid") ?? Value(element, "id");
                var uniqueId = feed.SeenByGuid ? guid : torrentUrl;

                if (!string.IsNullOrWhiteSpace(uniqueId))
                {
                    entries.Add(new(title, torrentUrl, uniqueId));
                }
            }

            return new FeedEntriesResult(entries);
        }
        catch (OperationCanceledException)
        {
            return new CanceledServiceResult();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to read feed {FeedUrl}", feed.Url);
            return new ErrorServiceResult();
        }
    }

    private static string? FindLink(XElement item, string field)
    {
        var normalized = string.IsNullOrWhiteSpace(field) ? "link" : field;
        var value = Value(item, normalized) ?? Value(item, normalized.Replace('_', ':').Split(':').Last());

        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (normalized.Equals("link", StringComparison.OrdinalIgnoreCase))
        {
            var link = item.Elements().FirstOrDefault(element => element.Name.LocalName.Equals("link", StringComparison.OrdinalIgnoreCase));
            return link?.Attribute("href")?.Value;
        }

        return null;
    }

    private static string? Value(XElement item, string name) => item.Elements()
        .FirstOrDefault(element => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim();

    private sealed class LimitedReadStream(Stream inner, long maximumBytes) : Stream
    {
        private long _read;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => Track(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Track(await inner.ReadAsync(buffer, cancellationToken));

        private int Track(int count)
        {
            _read += count;
            return _read > maximumBytes ? throw new InvalidDataException("Feed exceeds the 10 MB limit.") : count;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
