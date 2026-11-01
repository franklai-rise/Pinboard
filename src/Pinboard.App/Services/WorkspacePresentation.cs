using System.Globalization;

namespace Pinboard.App.Services;

public static class WorkspacePresentation
{
    public const double DefaultSidebarWidth = 272;
    public const double DefaultReaderRatio = 0.43;

    public static double SidebarWidth(double preferred, double availableWidth)
    {
        var maximum = Math.Max(220, Math.Min(420, availableWidth - 558));
        return Math.Clamp(double.IsFinite(preferred) ? preferred : DefaultSidebarWidth, 220, maximum);
    }

    public static double ReaderRatio(double ratio) => double.IsFinite(ratio)
        ? Math.Clamp(ratio, 0.15, 0.85) : DefaultReaderRatio;

    public static DateOnly? DailyDate(string path) => DateOnly.TryParseExact(
        Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var date) ? date : null;

    public static string DailyTitle(string path, string fallback, DateOnly today, bool chinese)
    {
        var date = DailyDate(path);
        return date == today ? (chinese ? "今日 · " : "Today · ") + date.Value.ToString("yyyy-MM-dd") : fallback;
    }

    public static string MonthTitle(DateOnly date, DateOnly today, bool chinese) =>
        (date.Year == today.Year && date.Month == today.Month ? (chinese ? "本月 · " : "This month · ") : "")
        + date.ToString("yyyy-MM");
}
