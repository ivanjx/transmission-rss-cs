namespace TransmissionRss;

public sealed class PollSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Request()
    {
        if (_signal.CurrentCount == 0)
        {
            _signal.Release();
        }
    }

    public async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        await _signal.WaitAsync(delay, cancellationToken);
}

public sealed class PollingService(
    AppRepository repository,
    FeedReader feedReader,
    TransmissionClient transmission,
    PollSignal signal,
    ILogger<PollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settingsResult = await repository.GetSettingsAsync(stoppingToken);

                if (settingsResult is CanceledRepositoryResult)
                {
                    break;
                }

                if (settingsResult is not RepositoryResult<AppSettings> settings)
                {
                    logger.LogError("Polling settings could not be loaded");
                    await signal.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                await signal.WaitAsync(TimeSpan.FromSeconds(settings.Value.PollIntervalSeconds), stoppingToken);
                await PollAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Polling cycle failed");
            }
        }
    }

    public async Task<ServiceResult> PollAsync(CancellationToken cancellationToken = default)
    {
        var settingsResult = await repository.GetSettingsAsync(cancellationToken);
        var feedsResult = await repository.GetFeedsAsync(enabledRulesOnly: true, cancellationToken);

        if (settingsResult is CanceledRepositoryResult || feedsResult is CanceledRepositoryResult)
        {
            return new CanceledServiceResult();
        }

        if (settingsResult is not RepositoryResult<AppSettings> settings ||
            feedsResult is not RepositoryResult<IReadOnlyList<Feed>> feeds)
        {
            logger.LogError("Polling data could not be loaded");
            return new ErrorServiceResult();
        }

        var succeeded = true;

        foreach (var feed in feeds.Value)
        {
            var result = await PollFeedAsync(settings.Value, feed, cancellationToken);

            if (result is CanceledServiceResult)
            {
                return result;
            }

            if (result is not SuccessServiceResult)
            {
                succeeded = false;
                logger.LogError("Feed {FeedUrl} failed", feed.Url);
            }
        }

        return succeeded ?
            new SuccessServiceResult() :
            new ErrorServiceResult();
    }

    private async Task<ServiceResult> PollFeedAsync(
        AppSettings settings,
        Feed feed,
        CancellationToken cancellationToken)
    {
        try
        {
            var feedResult = await feedReader.ReadAsync(feed, settings.RequestTimeoutSeconds, cancellationToken);

            if (feedResult is CanceledServiceResult)
            {
                return feedResult;
            }

            if (feedResult is not FeedEntriesResult entries)
            {
                logger.LogError("Feed {FeedUrl} could not be read", feed.Url);
                return new ErrorServiceResult();
            }

            foreach (var rule in feed.Rules)
            {
                var result = await ApplyRuleAsync(settings, rule, entries.Entries, cancellationToken);

                if (result is not SuccessServiceResult)
                {
                    logger.LogError("Rule {RuleName} could not be applied to feed {FeedUrl}", rule.Name, feed.Url);
                    return result;
                }
            }

            var checkedResult = await repository.SetFeedCheckedItemCountAsync(
                feed.Id, entries.Entries.Count, cancellationToken);
            return checkedResult switch
            {
                SuccessRepositoryResult => new SuccessServiceResult(),
                CanceledRepositoryResult => new CanceledServiceResult(),
                _ => new ErrorServiceResult()
            };
        }
        catch (OperationCanceledException)
        {
            return new CanceledServiceResult();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Feed {FeedUrl} could not be processed", feed.Url);
            return new ErrorServiceResult();
        }
    }

    private async Task<ServiceResult> ApplyRuleAsync(
        AppSettings settings,
        FeedRule rule,
        IReadOnlyList<FeedEntry> entries,
        CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
        {
            if (!TitleMatcher.Matches(rule, entry.Title))
            {
                continue;
            }

            var seenResult = await repository.HasSeenAsync(rule.Id, entry.UniqueId, cancellationToken);

            if (seenResult is CanceledRepositoryResult)
            {
                return new CanceledServiceResult();
            }

            if (seenResult is not RepositoryResult<bool> seen)
            {
                return new ErrorServiceResult();
            }

            if (seen.Value)
            {
                continue;
            }

            var transmissionResult = await transmission.AddTorrentAsync(
                settings, rule, entry.TorrentUrl, cancellationToken);

            if (transmissionResult is not SuccessServiceResult)
            {
                return transmissionResult;
            }

            var addPaused = rule.AddPaused ?? settings.AddPaused;
            var markResult = await repository.MarkSeenAsync(
                rule, entry.UniqueId, entry.Title, entry.TorrentUrl, addPaused, cancellationToken);

            if (markResult is CanceledRepositoryResult)
            {
                return new CanceledServiceResult();
            }

            if (markResult is not SuccessRepositoryResult)
            {
                return new ErrorServiceResult();
            }

            logger.LogInformation("Added {TorrentTitle} from rule {RuleName}", entry.Title, rule.Name);
        }

        return new SuccessServiceResult();
    }

}
