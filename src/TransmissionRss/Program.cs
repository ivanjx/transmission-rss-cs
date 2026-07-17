using TransmissionRss;

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
app.MapGet("/", Controllers.HomeAsync);
app.MapGet("/feeds/new", Controllers.NewFeed);
app.MapGet("/feeds/{id}", Controllers.EditFeedAsync);
app.MapGet("/feeds/{feedId}/rules/new", Controllers.NewRuleAsync);
app.MapGet("/rules/{id}", Controllers.EditRuleAsync);
app.MapGet("/downloads", Controllers.DownloadsAsync);
app.MapPost("/settings", Controllers.SaveSettingsAsync);
app.MapPost("/feeds", Controllers.CreateFeedAsync);
app.MapPost("/feeds/{id}", Controllers.UpdateFeedAsync);
app.MapPost("/feeds/{id}/delete", Controllers.DeleteFeedAsync);
app.MapPost("/feeds/{feedId}/rules", Controllers.CreateRuleAsync);
app.MapPost("/rules/{id}", Controllers.UpdateRuleAsync);
app.MapPost("/rules/{id}/delete", Controllers.DeleteRuleAsync);
app.MapPost("/downloads/delete", Controllers.DeleteDownloadAsync);
app.MapPost("/downloads/clear", Controllers.ClearDownloadsAsync);
app.MapGet("/poll", Controllers.PollNow);
app.MapPost("/poll", Controllers.PollNow);
app.MapGet("/health", Controllers.HealthAsync);

app.Run();
