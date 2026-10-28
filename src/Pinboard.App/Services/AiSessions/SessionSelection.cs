namespace Pinboard.App.Services.AiSessions;

public static class SessionSelection
{
    public static bool HasIssue(SessionEntry entry) => entry.Error is not null || entry.Status == "Retry pending"
        || entry.Status.Contains("missing ", StringComparison.Ordinal) || entry.Descriptor.Availability == "Missing source";
    // Context parents are displayed for navigation, but are not implicitly selected by a matching child.
    // Folded children that actually match remain part of an explicit bulk selection.
    public static IReadOnlyList<SessionEntry> Matching(IEnumerable<SessionEntry> entries, string? query, string? source)
    {
        query = query?.Trim() ?? "";
        return entries.Where(entry => (source is null || entry.Descriptor.Source == source)
            && (query.Length == 0 || entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
    }
}
