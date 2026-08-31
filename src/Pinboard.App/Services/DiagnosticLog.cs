using System.Text;
using Pinboard.App.Models;

namespace Pinboard.App.Services;

public static class DiagnosticLog
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    public static string LogDirectory => Path.Combine(AppSettings.SettingsDirectory, "Logs");
    public static string LogPath => Path.Combine(LogDirectory, "pinboard.log");

    public static void Write(string area, Exception exception)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded();
                var entry = new StringBuilder()
                    .Append(DateTimeOffset.Now.ToString("O"))
                    .Append(" [")
                    .Append(area)
                    .AppendLine("]")
                    .AppendLine(exception.ToString())
                    .AppendLine()
                    .ToString();
                File.AppendAllText(LogPath, entry, Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never become another application failure.
        }
    }

    private static void RotateIfNeeded()
    {
        var file = new FileInfo(LogPath);
        if (!file.Exists || file.Length < MaxLogBytes)
        {
            return;
        }

        var previous = Path.Combine(LogDirectory, "pinboard.previous.log");
        File.Move(LogPath, previous, overwrite: true);
    }
}
