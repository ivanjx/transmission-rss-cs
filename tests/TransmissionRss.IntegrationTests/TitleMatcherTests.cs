using Xunit;

namespace TransmissionRss.IntegrationTests;

public sealed class TitleMatcherTests
{
    [Fact]
    public void RequiresEveryIncludeAndIgnoresCase()
    {
        var rule = CreateRule(["submarine", "panzer", "mllsd"], []);

        Assert.True(TitleMatcher.Matches(rule, "[SUBMARINE] Girls und Panzer MLLSD 01"));
        Assert.False(TitleMatcher.Matches(rule, "[SUBMARINE] Girls und Panzer 01"));
    }

    [Fact]
    public void RejectsTitleWhenAnyExcludeOccurs()
    {
        var rule = CreateRule(["panzer"], ["dubbed", "720p"]);

        Assert.True(TitleMatcher.Matches(rule, "Girls und Panzer 1080P"));
        Assert.False(TitleMatcher.Matches(rule, "Girls und Panzer DUBBED 1080P"));
    }

    [Fact]
    public void EmptyIncludeListMatchesUnlessExcluded()
    {
        var rule = CreateRule([], ["cam"]);

        Assert.True(TitleMatcher.Matches(rule, "Any WEB release"));
        Assert.False(TitleMatcher.Matches(rule, "Any CAM release"));
    }

    private static FeedRule CreateRule(IReadOnlyList<string> includes, IReadOnlyList<string> excludes) =>
        new("rule", "feed", "Rule", includes, excludes, string.Empty, null, true, null, string.Empty);
}
