namespace TransmissionRss;

public sealed record AppSettings(
    string TransmissionUrl,
    string Username,
    string Password,
    int PollIntervalSeconds,
    int RequestTimeoutSeconds,
    bool AddPaused);

public sealed record Feed(
    string Id,
    string Url,
    string LinkField,
    bool SeenByGuid,
    IReadOnlyList<FeedRule> Rules,
    int? LastCheckedItemCount = null);

public sealed record FeedRule(
    string Id,
    string FeedId,
    string Name,
    IReadOnlyList<string> Includes,
    IReadOnlyList<string> Excludes,
    string DownloadDirectory,
    bool? AddPaused,
    bool Enabled);

public sealed record DownloadedTorrent(
    long Id,
    string RuleId,
    string RuleName,
    string UniqueId,
    string Title,
    string TorrentUrl,
    DateTimeOffset DownloadedAt,
    string DownloadDirectory,
    bool AddPaused);

public sealed record DownloadedTorrentsPage(
    IReadOnlyList<DownloadedTorrent> Torrents,
    int Page,
    int PageCount,
    long? NextId,
    long? PreviousId);

public sealed record DashboardModel(AppSettings Settings, IReadOnlyList<Feed> Feeds, string? Notice);

public sealed record DownloadsModel(
    IReadOnlyList<DownloadedTorrent> Torrents,
    int Page,
    int PageCount,
    long? NextId,
    long? PreviousId,
    string? Notice);

public sealed record FeedEditorModel(Feed Feed, bool IsNew, string? Error);

public sealed record RuleEditorModel(Feed Feed, FeedRule Rule, bool IsNew, string? Error);

public sealed record FeedEntry(string Title, string TorrentUrl, string UniqueId);
