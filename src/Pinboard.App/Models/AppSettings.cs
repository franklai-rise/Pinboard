using System.Text.Json;
using Pinboard.App.Services;

namespace Pinboard.App.Models;

public sealed class AppSettings
{
    public const int CurrentSettingsSchemaVersion = 3;
    public const string TextClipsBoardFileName = "Text Clips.pinboard";
    public const string TextClipsDirectoryName = "Text Clips";

    public int SettingsSchemaVersion { get; set; } = CurrentSettingsSchemaVersion;
    public string LibraryPath { get; set; } = DefaultLibraryPath;
    public string? FixedCaptureTarget { get; set; }
    public int WebpQuality { get; set; } = 80;
    public bool OcrChinese { get; set; } = true;
    public bool OcrEnglish { get; set; } = true;
    public bool CapturePaused { get; set; }
    // Clipboard text can contain passwords and other private data. New installs
    // therefore require an explicit opt-in. Existing serialized true/false values
    // are preserved by Load().
    public bool TextCaptureEnabled { get; set; }
    public bool TextCapturePaused { get; set; }
    public TextCaptureBoardMode TextCaptureBoardMode { get; set; } = TextCaptureBoardMode.Monthly;
    public bool TextPrivacyModeEnabled { get; set; } = true;
    public bool TextPrivacyReviewPending { get; set; }
    public List<string> TextCaptureExcludedApplications { get; set; } = CreateDefaultExcludedApplications();
    public string? PixPinExecutablePath { get; set; }
    public string? LastImportDirectory { get; set; }
    public bool StartAtLogin { get; set; } = true;
    public bool SidebarCollapsed { get; set; }
    // Behave like a normal desktop window unless the user explicitly pins it.
    public bool AlwaysOnTop { get; set; }
    public string Language { get; set; } = LocalizationService.English;
    public List<string> RecentFiles { get; set; } = [];

    public static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pinboard");

    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");
    public static string RecoveryDirectory => Path.Combine(SettingsDirectory, "Recovery");
    public static string DefaultLibraryPath
    {
        get
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents))
            {
                documents = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Documents");
            }
            return Path.Combine(documents, "Pinboard");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var serialized = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(serialized);
                if (settings is not null)
                {
                    using var document = JsonDocument.Parse(serialized);
                    var root = document.RootElement;
                    var previousVersion = root.TryGetProperty(nameof(SettingsSchemaVersion), out var versionElement)
                        && versionElement.TryGetInt32(out var parsedVersion)
                        ? parsedVersion
                        : 1;
                    var hadTextCapturePreference = root.TryGetProperty(nameof(TextCaptureEnabled), out _);
                    var hadTextCaptureBoardMode = root.TryGetProperty(nameof(TextCaptureBoardMode), out _);

                    // A preference explicitly saved by an existing installation is
                    // authoritative. Settings from before text collection existed
                    // are migrated to the privacy-preserving disabled state.
                    if (!hadTextCapturePreference)
                    {
                        settings.TextCaptureEnabled = false;
                    }
                    if (!hadTextCaptureBoardMode)
                    {
                        // Existing installations used one root-level Text Clips file.
                        // Preserve that layout unless the user explicitly chooses monthly boards.
                        settings.TextCaptureBoardMode = TextCaptureBoardMode.LegacySingleBoard;
                    }
                    if (previousVersion < CurrentSettingsSchemaVersion && settings.TextCaptureEnabled)
                    {
                        settings.TextPrivacyReviewPending = true;
                    }
                    settings.SettingsSchemaVersion = CurrentSettingsSchemaVersion;
                    settings.LibraryPath = NormalizeLibraryPath(settings.LibraryPath);
                    settings.Language = LocalizationService.Normalize(settings.Language);
                    settings.RecentFiles ??= [];
                    settings.TextCaptureExcludedApplications ??= CreateDefaultExcludedApplications();
                    settings.TextCaptureExcludedApplications = NormalizeApplicationNames(settings.TextCaptureExcludedApplications);
                    settings.PixPinExecutablePath = NormalizeExistingFile(settings.PixPinExecutablePath);
                    settings.LastImportDirectory = NormalizeExistingDirectory(settings.LastImportDirectory);
                    settings.RecentFiles = settings.RecentFiles
                        .Where(File.Exists)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(12)
                        .ToList();
                    if (previousVersion < CurrentSettingsSchemaVersion || !hadTextCapturePreference || !hadTextCaptureBoardMode)
                    {
                        try
                        {
                            settings.Save();
                        }
                        catch
                        {
                            // A settings migration should never prevent Pinboard from starting.
                        }
                    }
                    return settings;
                }
            }
        }
        catch
        {
            // A malformed settings file must never prevent the canvas from opening.
        }

        return new AppSettings();
    }

    public void Save()
    {
        SettingsSchemaVersion = CurrentSettingsSchemaVersion;
        TextCaptureExcludedApplications = NormalizeApplicationNames(TextCaptureExcludedApplications);
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, SettingsPath, true);
    }

    public static List<string> CreateDefaultExcludedApplications() =>
    [
        "1Password",
        "Bitwarden",
        "Dashlane",
        "Enpass",
        "KeePass",
        "KeePassXC",
        "LastPass",
        "ProtonPass"
    ];

    public static List<string> NormalizeApplicationNames(IEnumerable<string>? names) =>
        (names ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => Path.GetFileNameWithoutExtension(name.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? NormalizeExistingFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? Path.GetFullPath(path) : null;

    private static string? NormalizeExistingDirectory(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? Path.GetFullPath(path) : null;

    private static string NormalizeLibraryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return DefaultLibraryPath;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return DefaultLibraryPath;
        }
    }

    public string ResolveCaptureTarget(DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(FixedCaptureTarget))
        {
            return Path.GetFullPath(FixedCaptureTarget);
        }

        Directory.CreateDirectory(LibraryPath);
        return Path.Combine(LibraryPath, $"{now:yyyy-MM}.pinboard");
    }

    public string ResolveTextCaptureTarget(DateTimeOffset now)
    {
        Directory.CreateDirectory(LibraryPath);
        if (TextCaptureBoardMode == TextCaptureBoardMode.LegacySingleBoard)
        {
            return Path.Combine(LibraryPath, TextClipsBoardFileName);
        }

        var directory = Path.Combine(LibraryPath, TextClipsDirectoryName);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{now:yyyy-MM}.pinboard");
    }

    public string ResolveTextCaptureTarget() => ResolveTextCaptureTarget(DateTimeOffset.Now);

    public void Remember(string path)
    {
        path = Path.GetFullPath(path);
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 12)
        {
            RecentFiles.RemoveRange(12, RecentFiles.Count - 12);
        }
    }
}
