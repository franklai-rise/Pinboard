using System.Globalization;
using System.Windows;

namespace Pinboard.App.Services;

public static class LocalizationService
{
    public const string English = "en";
    public const string Chinese = "zh-CN";
    private const string ResourceMarker = "Resources/Strings.";
    private static readonly IReadOnlyDictionary<string, string> EnglishFallback = new Dictionary<string, string>
    {
        ["ProjectDefault"] = "Default Project",
        ["ProjectOtherLocations"] = "Other Locations",
        ["ProjectArchive"] = "Archive",
        ["SceneInboxLabel"] = "Inbox | PixPin screenshots are arranged downward by time",
        ["SceneInboxLayoutLabel"] = "Inbox | PixPin screenshots are automatically spaced into two columns",
        ["SceneInvalid"] = "The board scene data is invalid.",
        ["RenameBoardEmpty"] = "Enter a display name.",
        ["RenameBoardTooLong"] = "The display name must be 120 characters or fewer.",
        ["ClipboardConnectFailed"] = "Could not connect to the Pinboard window message queue.",
        ["ClipboardListenFailed"] = "Could not monitor the Windows clipboard.",
        ["EmbeddedAssetPathInvalid"] = "An embedded canvas resource path is invalid.",
        ["EmbeddedAssetReadFailedFormat"] = "Could not read embedded canvas resource {0}.",
        ["EmbeddedIndexMissing"] = "The embedded canvas home page is missing from the app package.",
        ["ExecutablePathUnknown"] = "Could not determine the path to Pinboard.exe.",
        ["ImageDecodeFailed"] = "The clipboard image could not be decoded. PNG, JPG, WebP, and BMP are supported.",
        ["ImageDataUrlInvalid"] = "The image returned by the canvas is not a valid data URL.",
        ["ImportVaultRootMissing"] = "Could not find a Vault root containing .obsidian.",
        ["ImportSceneInvalid"] = "The decompressed Excalidraw scene is not valid JSON.",
        ["ImportStandardInvalid"] = "The standard Excalidraw file is not valid JSON.",
        ["ImportCompressedMissing"] = "No compressed-json Drawing block was found in the note.",
        ["ImportDecompressFailed"] = "Could not decompress the Excalidraw compressed-json data.",
        ["ImportDecompressedInvalid"] = "The decompressed result is not valid Excalidraw JSON.",
        ["PinboardReadOnlySceneMissing"] = "The read-only board has no scene data.",
        ["PinboardSceneMissing"] = "The board has no scene data.",
        ["PinboardReadOnlyFormat"] = "Board {0} is read-only. Use Save a Copy before editing.",
        ["PixPinConfigMissing"] = "PixPinConfig.json was not found.",
        ["PixPinBackupMissing"] = "No PixPin configuration backup created by Pinboard was found.",
        ["PixPinExitRequired"] = "Exit PixPin normally from its tray icon before backing up or restoring configuration. Pinboard will not force-close it.",
        ["PixPinExecutableMissing"] = "PixPin.exe was not found. Start PixPin or detect it again in Settings.",
        ["BoardMoveSourceMissing"] = "The board to move could not be found.",
        ["BoardOutsideLibrary"] = "Only boards inside the project library can be moved.",
        ["BoardAlreadyInProject"] = "The board is already in this project.",
        ["ProjectExistsFormat"] = "Project “{0}” already exists.",
        ["ProjectNameRequired"] = "Enter a project name.",
        ["ProjectNameReserved"] = "That name is reserved by Pinboard. Choose another project name.",
        ["ProjectNameInvalidChars"] = "Project names contain unsupported characters.",
        ["ProjectNameWindowsInvalid"] = "That project name cannot be saved as a Windows folder.",
        ["BoardNameRequired"] = "Enter a board name.",
        ["BoardNameInvalidChars"] = "The board name contains unsupported file-name characters.",
        ["BoardNameWindowsInvalid"] = "That board name cannot be saved as a Windows file."
    };

    public static string CurrentLanguage { get; private set; } = English;

    public static string Normalize(string? language) =>
        string.Equals(language, Chinese, StringComparison.OrdinalIgnoreCase) ? Chinese : English;

    public static void Apply(string? language)
    {
        CurrentLanguage = Normalize(language);
        var culture = CultureInfo.GetCultureInfo(CurrentLanguage == Chinese ? "zh-CN" : "en-US");
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        var dictionaries = application.Resources.MergedDictionaries;
        for (var index = dictionaries.Count - 1; index >= 0; index--)
        {
            if (dictionaries[index].Source?.OriginalString.Contains(ResourceMarker, StringComparison.OrdinalIgnoreCase) == true)
            {
                dictionaries.RemoveAt(index);
            }
        }

        dictionaries.Insert(0, new ResourceDictionary
        {
            Source = new Uri($"Resources/Strings.{CurrentLanguage}.xaml", UriKind.Relative)
        });
    }

    public static string T(string key, params object?[] arguments)
    {
        var value = Application.Current?.TryFindResource(key)?.ToString()
            ?? (EnglishFallback.TryGetValue(key, out var fallback) ? fallback : key);
        return arguments.Length == 0 ? value : string.Format(CultureInfo.CurrentCulture, value, arguments);
    }

    public static string ProjectDisplayName(string projectName)
    {
        if (projectName.Equals(ProjectLibraryService.DefaultProjectName, StringComparison.CurrentCultureIgnoreCase))
        {
            return T("ProjectDefault");
        }
        if (projectName.Equals(ProjectLibraryService.OtherLocationProjectName, StringComparison.CurrentCultureIgnoreCase))
        {
            return T("ProjectOtherLocations");
        }
        if (projectName.Equals(ProjectLibraryService.ArchiveProjectName, StringComparison.CurrentCultureIgnoreCase))
        {
            return T("ProjectArchive");
        }
        return projectName;
    }
}
