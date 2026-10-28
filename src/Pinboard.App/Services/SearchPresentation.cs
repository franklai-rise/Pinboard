using Pinboard.App.Models;

namespace Pinboard.App.Services;

public sealed record SearchExcerptRun(string Text, bool IsMatch);
public sealed record SearchExcerpt(IReadOnlyList<SearchExcerptRun> Runs)
{
    public string Text => string.Concat(Runs.Select(run => run.Text));
}

public static class SearchPresentation
{
    public static SearchExcerpt BuildExcerpt(string text, string query, int length = 240)
    {
        length = Math.Clamp(length, 32, 1000);
        text = CollapseWhitespace(text);
        query = CollapseWhitespace(query).Trim();
        var matchIndex = query.Length == 0 ? -1 : text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        var start = matchIndex < 0 ? 0 : Math.Max(0, matchIndex - length / 3);
        if (start > 0 && char.IsLowSurrogate(text[start])) start--;
        var end = Math.Min(text.Length, start + length);
        if (end < text.Length && end > 0 && char.IsHighSurrogate(text[end - 1])) end++;
        var excerpt = text[start..end];
        var runs = new List<SearchExcerptRun>();
        if (start > 0) runs.Add(new("…", false));
        var cursor = 0;
        var searchCursor = start;
        while (query.Length > 0 && cursor < excerpt.Length)
        {
            var sourceMatch = text.IndexOf(query, searchCursor, StringComparison.OrdinalIgnoreCase);
            if (sourceMatch < 0 || sourceMatch >= end) break;
            var next = sourceMatch - start;
            if (next > cursor) runs.Add(new(excerpt[cursor..next], false));
            var visibleMatchLength = Math.Min(query.Length, excerpt.Length - next);
            runs.Add(new(excerpt.Substring(next, visibleMatchLength), true));
            cursor = next + visibleMatchLength;
            searchCursor = sourceMatch + query.Length;
        }
        if (cursor < excerpt.Length) runs.Add(new(excerpt[cursor..], false));
        if (end < text.Length) runs.Add(new("…", false));
        return new(runs.ToArray());
    }

    public static int Relevance(SearchHit hit, string query)
    {
        query = query.Trim();
        if (query.Length == 0) return 0;
        var score = hit.Text.Trim().Equals(query, StringComparison.OrdinalIgnoreCase) ? 100 : 0;
        if (hit.DocumentTitle.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 20;
        if (hit.Text.TrimStart().StartsWith(query, StringComparison.OrdinalIgnoreCase)) score += 12;
        var cursor = 0;
        for (var count = 0; count < 8; count++)
        {
            var next = hit.Text.IndexOf(query, cursor, StringComparison.OrdinalIgnoreCase);
            if (next < 0) break;
            score += 4;
            cursor = next + query.Length;
        }
        return score;
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWhitespace = false;
        foreach (var character in value)
        {
            var whitespace = char.IsWhiteSpace(character);
            if (!whitespace || !previousWhitespace) builder.Append(whitespace ? ' ' : character);
            previousWhitespace = whitespace;
        }
        return builder.ToString();
    }
}
