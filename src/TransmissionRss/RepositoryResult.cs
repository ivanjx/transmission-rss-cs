namespace TransmissionRss;

public abstract record RepositoryResult;

public record SuccessRepositoryResult : RepositoryResult;

public record ErrorRepositoryResult : RepositoryResult;

public sealed record CanceledRepositoryResult : RepositoryResult;

public sealed record RepositoryResult<T>(T Value) : SuccessRepositoryResult;
