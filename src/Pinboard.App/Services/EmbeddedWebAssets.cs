using System.Reflection;
using Pinboard.App.Models;

namespace Pinboard.App.Services;

public static class EmbeddedWebAssets
{
    private const string Prefix = "Pinboard.Web/";

    public static string EnsureExtracted()
    {
        var assembly = typeof(EmbeddedWebAssets).Assembly;
        var versionKey = assembly.ManifestModule.ModuleVersionId.ToString("N");
        var root = Path.Combine(AppSettings.SettingsDirectory, "WebAssets", versionKey);
        var ready = Path.Combine(root, ".ready");
        if (File.Exists(ready) && File.Exists(Path.Combine(root, "index.html")))
        {
            return root;
        }

        Directory.CreateDirectory(root);
        foreach (var resourceName in assembly.GetManifestResourceNames().Where(name => name.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            var relative = resourceName[Prefix.Length..]
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            var destination = Path.GetFullPath(Path.Combine(root, relative));
            var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(LocalizationService.T("EmbeddedAssetPathInvalid"));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var source = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidDataException(LocalizationService.T("EmbeddedAssetReadFailedFormat", resourceName));
            using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
        }
        if (!File.Exists(Path.Combine(root, "index.html")))
        {
            throw new InvalidDataException(LocalizationService.T("EmbeddedIndexMissing"));
        }
        File.WriteAllText(ready, DateTimeOffset.UtcNow.ToString("O"));
        return root;
    }
}
