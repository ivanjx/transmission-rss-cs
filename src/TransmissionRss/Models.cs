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
    IReadOnlyList<FeedRule> Rules);

public sealed record FeedRule(
    string Id,
    string FeedId,
    string Name,
    IReadOnlyList<string> Includes,
    IReadOnlyList<string> Excludes,
    string DownloadDirectory,
    bool? AddPaused,
    bool Enabled,
    DateTimeOffset? LastCheckedAt,
    string LastStatus);

public sealed record DownloadedTorrent(
    string RuleId,
    string RuleName,
    string UniqueId,
    string Title,
    string TorrentUrl,
    DateTimeOffset DownloadedAt,
    string DownloadDirectory,
    bool AddPaused);

public sealed record DashboardModel(AppSettings Settings, IReadOnlyList<Feed> Feeds, string? Notice);

public sealed record DownloadsModel(IReadOnlyList<DownloadedTorrent> Torrents, string? Notice);

public sealed record FeedEditorModel(Feed Feed, bool IsNew, string? Error);

public sealed record RuleEditorModel(Feed Feed, FeedRule Rule, bool IsNew, string? Error);

public sealed record FeedEntry(string Title, string TorrentUrl, string UniqueId);
