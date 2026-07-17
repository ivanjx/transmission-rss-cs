using TransmissionRss;

if (args is ["--healthcheck"])
{
    Environment.ExitCode = await DockerHealthProbe.IsHealthyAsync() ? 0 : 1;
    return;
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddSingleton<AppRepository>();
builder.Services.AddSingleton<PollSignal>();
builder.Services.AddSingleton<FeedReader>();
builder.Services.AddSingleton<TransmissionClient>();
builder.Services.AddHttpClient();

if (!builder.Configuration.GetValue("Polling:Disabled", false))
{
    builder.Services.AddHostedService<PollingService>();
}

var app = builder.Build();
var repository = app.Services.GetRequiredService<AppRepository>();

if (await repository.InitializeAsync() is not SuccessRepositoryResult)
{
    return;
}

app.Services.GetRequiredService<PollSignal>().Request();

app.UseStaticFiles();
app.MapGet("/", Endpoints.HomeAsync);
app.MapGet("/feeds/new", Endpoints.NewFeed);
app.MapGet("/feeds/{id}", Endpoints.EditFeedAsync);
app.MapGet("/feeds/{feedId}/rules/new", Endpoints.NewRuleAsync);
app.MapGet("/rules/{id}", Endpoints.EditRuleAsync);
app.MapGet("/downloads", Endpoints.DownloadsAsync);
app.MapPost("/settings", Endpoints.SaveSettingsAsync);
app.MapPost("/feeds", Endpoints.CreateFeedAsync);
app.MapPost("/feeds/{id}", Endpoints.UpdateFeedAsync);
app.MapPost("/feeds/{id}/delete", Endpoints.DeleteFeedAsync);
app.MapPost("/feeds/{feedId}/rules", Endpoints.CreateRuleAsync);
app.MapPost("/rules/{id}", Endpoints.UpdateRuleAsync);
app.MapPost("/rules/{id}/delete", Endpoints.DeleteRuleAsync);
app.MapPost("/downloads/delete", Endpoints.DeleteDownloadAsync);
app.MapPost("/downloads/clear", Endpoints.ClearDownloadsAsync);
app.MapGet("/poll", Endpoints.PollNow);
app.MapPost("/poll", Endpoints.PollNow);
app.MapGet("/health", Endpoints.HealthAsync);
app.Run();

public static class Endpoints
{
    private const int HealthCheckTimeoutSeconds = 10;

    public static async Task<IResult> HealthAsync(
        HttpContext context,
        AppRepository repository,
        IHttpClientFactory httpClientFactory)
    {
        var feedsResult = await repository.GetFeedsAsync(cancellationToken: context.RequestAborted);

        if (feedsResult is not RepositoryResult<IReadOnlyList<Feed>> feeds)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (feeds.Value.Count == 0)
        {
            return Results.Ok();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(HealthCheckTimeoutSeconds));
        var checks = feeds.Value
            .Select(feed => IsFeedReachableAsync(feed.Url, httpClientFactory, timeout.Token))
            .ToList();

        while (checks.Count > 0)
        {
            var completed = await Task.WhenAny(checks);
            checks.Remove(completed);

            if (await completed)
            {
                await timeout.CancelAsync();
                return Results.Ok();
            }
        }

        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    public static async Task<IResult> HomeAsync(HttpContext context, AppRepository repository)
    {
        var settingsResult = await repository.GetSettingsAsync(context.RequestAborted);
        var feedsResult = await repository.GetFeedsAsync(cancellationToken: context.RequestAborted);

        if (settingsResult is CanceledRepositoryResult || feedsResult is CanceledRepositoryResult)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }

        if (settingsResult is not RepositoryResult<AppSettings> settings ||
            feedsResult is not RepositoryResult<IReadOnlyList<Feed>> feeds)
        {
            return Results.Problem("Transmission RSS could not load the dashboard.", statusCode: 500);
        }

        return TransmissionRss.Slices.Home.Create(new DashboardModel(
            settings.Value, feeds.Value, context.Request.Query["notice"].FirstOrDefault()));
    }

    public static IResult NewFeed() => TransmissionRss.Slices.FeedEditor.Create(new(
        new Feed(string.Empty, string.Empty, "link", false, []), true, null));

    public static async Task<IResult> EditFeedAsync(string id, HttpContext context, AppRepository repository)
    {
        var result = await repository.GetFeedAsync(id, context.RequestAborted);

        return result switch
        {
            RepositoryResult<Feed?> { Value: null } => Results.NotFound(),
            RepositoryResult<Feed?> feed => TransmissionRss.Slices.FeedEditor.Create(new(
                feed.Value, false, context.Request.Query["notice"].FirstOrDefault())),
            CanceledRepositoryResult => Results.StatusCode(StatusCodes.Status499ClientClosedRequest),
            _ => Results.Problem("Transmission RSS could not load the feed.", statusCode: 500)
        };
    }

    public static async Task<IResult> NewRuleAsync(string feedId, HttpContext context, AppRepository repository)
    {
        var result = await repository.GetFeedAsync(feedId, context.RequestAborted);

        return result switch
        {
            RepositoryResult<Feed?> { Value: null } => Results.NotFound(),
            RepositoryResult<Feed?> feed => TransmissionRss.Slices.RuleEditor.Create(new(
                feed.Value,
                new FeedRule(string.Empty, feedId, string.Empty, [], [], string.Empty, null, true),
                true,
                null)),
            CanceledRepositoryResult => Results.StatusCode(StatusCodes.Status499ClientClosedRequest),
            _ => Results.Problem("Transmission RSS could not load the feed.", statusCode: 500)
        };
    }

    public static async Task<IResult> EditRuleAsync(string id, HttpContext context, AppRepository repository)
    {
        var ruleResult = await repository.GetRuleAsync(id, context.RequestAborted);

        if (ruleResult is CanceledRepositoryResult)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }

        if (ruleResult is not RepositoryResult<FeedRule?> { Value: not null } rule)
        {
            return ruleResult is RepositoryResult<FeedRule?> ?
                Results.NotFound() :
                Results.Problem("Transmission RSS could not load the feed rule.", statusCode: 500);
        }

        var feedResult = await repository.GetFeedAsync(rule.Value.FeedId, context.RequestAborted);
        return feedResult switch
        {
            RepositoryResult<Feed?> { Value: not null } feed => TransmissionRss.Slices.RuleEditor.Create(new(
                feed.Value, rule.Value, false, null)),
            CanceledRepositoryResult => Results.StatusCode(StatusCodes.Status499ClientClosedRequest),
            _ => Results.Problem("Transmission RSS could not load the rule's feed.", statusCode: 500)
        };
    }

    public static async Task<IResult> DownloadsAsync(HttpContext context, AppRepository repository)
    {
        var result = await repository.GetDownloadedTorrentsAsync(context.RequestAborted);
        return result switch
        {
            RepositoryResult<IReadOnlyList<DownloadedTorrent>> torrents => TransmissionRss.Slices.Downloads.Create(new(
                torrents.Value, context.Request.Query["notice"].FirstOrDefault())),
            CanceledRepositoryResult => Results.StatusCode(StatusCodes.Status499ClientClosedRequest),
            _ => Results.Problem("Transmission RSS could not load downloaded torrent history.", statusCode: 500)
        };
    }

    public static async Task<IResult> SaveSettingsAsync(
        HttpContext context,
        AppRepository repository,
        PollSignal signal)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);

        if (!TryHttpUri(form["transmissionUrl"], out var uri))
        {
            return Results.BadRequest("Transmission URL must be an absolute HTTP(S) URL.");
        }

        var currentResult = await repository.GetSettingsAsync(context.RequestAborted);

        if (currentResult is CanceledRepositoryResult)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }

        if (currentResult is not RepositoryResult<AppSettings> current)
        {
            return Results.Problem("Transmission RSS could not load the current settings.", statusCode: 500);
        }

        var submittedPassword = form["password"].ToString();
        var settings = new AppSettings(uri!.ToString(), form["username"].ToString(),
            string.IsNullOrEmpty(submittedPassword) ? current.Value.Password : submittedPassword,
            ParseRange(form["pollInterval"], 10, 86400, 600), ParseRange(form["timeout"], 1, 300, 30),
            form.ContainsKey("addPaused"));
        var saveResult = await repository.SaveSettingsAsync(settings, context.RequestAborted);

        if (saveResult is not SuccessRepositoryResult)
        {
            return RepositoryFailure(saveResult, "Transmission RSS could not save the settings.");
        }

        signal.Request();
        return Results.Redirect("/?notice=Settings+saved");
    }

    public static Task<IResult> CreateFeedAsync(HttpContext context, AppRepository repository, PollSignal signal) =>
        SaveFeedAsync(Guid.NewGuid().ToString("N"), true, context, repository, signal);

    public static Task<IResult> UpdateFeedAsync(
        string id,
        HttpContext context,
        AppRepository repository,
        PollSignal signal) => SaveFeedAsync(id, false, context, repository, signal);

    public static async Task<IResult> DeleteFeedAsync(string id, HttpContext context, AppRepository repository)
    {
        var result = await repository.DeleteFeedAsync(id, context.RequestAborted);
        return RedirectForResult(result, "/?notice=Feed+deleted", "Transmission RSS could not delete the feed.");
    }

    public static Task<IResult> CreateRuleAsync(
        string feedId,
        HttpContext context,
        AppRepository repository,
        PollSignal signal) => SaveRuleAsync(Guid.NewGuid().ToString("N"), feedId, true, context, repository, signal);

    public static async Task<IResult> UpdateRuleAsync(
        string id,
        HttpContext context,
        AppRepository repository,
        PollSignal signal)
    {
        var existing = await repository.GetRuleAsync(id, context.RequestAborted);

        if (existing is not RepositoryResult<FeedRule?> { Value: not null } rule)
        {
            return existing is CanceledRepositoryResult ?
                Results.StatusCode(StatusCodes.Status499ClientClosedRequest) :
                Results.NotFound();
        }

        return await SaveRuleAsync(id, rule.Value.FeedId, false, context, repository, signal);
    }

    public static async Task<IResult> DeleteRuleAsync(string id, HttpContext context, AppRepository repository)
    {
        var ruleResult = await repository.GetRuleAsync(id, context.RequestAborted);

        if (ruleResult is not RepositoryResult<FeedRule?> { Value: not null } rule)
        {
            return ruleResult is CanceledRepositoryResult ?
                Results.StatusCode(StatusCodes.Status499ClientClosedRequest) :
                Results.NotFound();
        }

        var result = await repository.DeleteRuleAsync(id, context.RequestAborted);
        return RedirectForResult(result, $"/feeds/{rule.Value.FeedId}?notice=Rule+deleted",
            "Transmission RSS could not delete the feed rule.");
    }

    public static async Task<IResult> DeleteDownloadAsync(HttpContext context, AppRepository repository)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var result = await repository.DeleteDownloadedTorrentAsync(
            form["ruleId"].ToString(), form["uniqueId"].ToString(), context.RequestAborted);
        return RedirectForResult(result, "/downloads?notice=Downloaded+torrent+deleted",
            "Transmission RSS could not delete the downloaded torrent.");
    }

    public static async Task<IResult> ClearDownloadsAsync(HttpContext context, AppRepository repository)
    {
        var result = await repository.ClearDownloadedTorrentsAsync(context.RequestAborted);
        return RedirectForResult(result, "/downloads?notice=Downloaded+torrents+cleared",
            "Transmission RSS could not clear downloaded torrent history.");
    }

    public static IResult PollNow(PollSignal signal)
    {
        signal.Request();
        return Results.Redirect("/?notice=Poll+requested");
    }

    private static async Task<IResult> SaveFeedAsync(
        string id,
        bool isNew,
        HttpContext context,
        AppRepository repository,
        PollSignal signal)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var feed = new Feed(id, form["url"].ToString().Trim(),
            string.IsNullOrWhiteSpace(form["linkField"]) ? "link" : form["linkField"].ToString().Trim(),
            form.ContainsKey("seenByGuid"), []);
        var error = Validate(feed);

        if (error is not null)
        {
            return Results.RazorSlice<TransmissionRss.Slices.FeedEditor, FeedEditorModel>(
                new(feed, isNew, error), StatusCodes.Status400BadRequest);
        }

        var result = await repository.SaveFeedAsync(feed, context.RequestAborted);

        if (result is not SuccessRepositoryResult)
        {
            return RepositoryFailure(result, "Transmission RSS could not save the feed.");
        }

        signal.Request();
        return Results.Redirect($"/feeds/{id}?notice=Feed+saved");
    }

    private static async Task<IResult> SaveRuleAsync(
        string id,
        string feedId,
        bool isNew,
        HttpContext context,
        AppRepository repository,
        PollSignal signal)
    {
        var feedResult = await repository.GetFeedAsync(feedId, context.RequestAborted);

        if (feedResult is not RepositoryResult<Feed?> { Value: not null } feed)
        {
            return feedResult is CanceledRepositoryResult ?
                Results.StatusCode(StatusCodes.Status499ClientClosedRequest) :
                Results.NotFound();
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var rule = new FeedRule(id, feedId, form["name"].ToString().Trim(), ParseTerms(form["includes"]),
            ParseTerms(form["excludes"]), form["downloadDirectory"].ToString().Trim(),
            ParseNullableBool(form["addPaused"]), form.ContainsKey("enabled"));
        var error = Validate(rule);

        if (error is not null)
        {
            return Results.RazorSlice<TransmissionRss.Slices.RuleEditor, RuleEditorModel>(
                new(feed.Value, rule, isNew, error), StatusCodes.Status400BadRequest);
        }

        var result = await repository.SaveRuleAsync(rule, context.RequestAborted);

        if (result is not SuccessRepositoryResult)
        {
            return RepositoryFailure(result, "Transmission RSS could not save the feed rule.");
        }

        signal.Request();
        return Results.Redirect($"/feeds/{feedId}?notice=Rule+saved");
    }

    private static IResult RedirectForResult(RepositoryResult result, string successLocation, string errorDetail) =>
        result is SuccessRepositoryResult ? Results.Redirect(successLocation) : RepositoryFailure(result, errorDetail);

    private static IResult RepositoryFailure(RepositoryResult result, string detail) => result is CanceledRepositoryResult ?
        Results.StatusCode(StatusCodes.Status499ClientClosedRequest) :
        Results.Problem(detail, statusCode: 500);

    private static string? Validate(Feed feed) => TryHttpUri(feed.Url, out _) ?
        null :
        "Feed URL must be an absolute HTTP(S) URL.";

    private static string? Validate(FeedRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            return "Name is required.";
        }

        if (rule.Includes.Concat(rule.Excludes).Any(term => term.Length > 200))
        {
            return "Each match term must be 200 characters or fewer.";
        }

        return null;
    }

    private static IReadOnlyList<string> ParseTerms(string? value) => (value ?? string.Empty)
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static bool TryHttpUri(string? value, out Uri? uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme is "http" or "https";

    private static int ParseRange(string? value, int minimum, int maximum, int fallback) =>
        int.TryParse(value, out var result) ? Math.Clamp(result, minimum, maximum) : fallback;

    private static bool? ParseNullableBool(string? value) => value switch
    {
        "true" => true,
        "false" => false,
        _ => null
    };

    private static async Task<bool> IsFeedReachableAsync(
        string url,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(nameof(HealthAsync));
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }
}

public static class DockerHealthProbe
{
    public static async Task<bool> IsHealthyAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var response = await client.GetAsync("http://127.0.0.1:8080/health");
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }
}

public partial class Program;
