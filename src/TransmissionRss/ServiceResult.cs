namespace TransmissionRss;

public abstract record ServiceResult;

public record SuccessServiceResult : ServiceResult;

public record ErrorServiceResult : ServiceResult;

public sealed record CanceledServiceResult : ServiceResult;

public sealed record FeedEntriesResult(IReadOnlyList<FeedEntry> Entries) : SuccessServiceResult;
