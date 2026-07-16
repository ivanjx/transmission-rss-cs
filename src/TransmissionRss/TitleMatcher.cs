namespace TransmissionRss;

public static class TitleMatcher
{
    public static bool Matches(FeedRule rule, string title) =>
        rule.Includes.All(term => title.Contains(term, StringComparison.OrdinalIgnoreCase)) &&
        !rule.Excludes.Any(term => title.Contains(term, StringComparison.OrdinalIgnoreCase));
}
