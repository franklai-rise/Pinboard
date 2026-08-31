using System.Diagnostics;
using Microsoft.Win32;
using Pinboard.App.Models;

namespace Pinboard.App.Services;

public sealed class PixPinService
{
    public const string ActionScript = "pixpin.runSystem(\"explorer.exe \\\"pinboard://capture-next?source=pixpin\\\"\")\npixpin.screenShot(ShotAction.Copy)";
    private static readonly string CachedExecutablePath = Path.Combine(AppSettings.SettingsDirectory, "pixpin-path.txt");
    private AppSettings? _settings;

    public void Configure(AppSettings settings) => _settings = settings;

    public string? FindExecutable()
    {
        foreach (var remembered in GetRememberedCandidates())
        {
            if (File.Exists(remembered))
            {
                return Path.GetFullPath(remembered);
            }
        }

        try
        {
            foreach (var process in Process.GetProcessesByName("PixPin"))
            {
                using (process)
                {
                    var path = process.MainModule?.FileName;
                    if (File.Exists(path))
                    {
                        TryRememberExecutable(path);
                        return path;
                    }
                }
            }
        }
        catch
        {
            // Process inspection can be denied by Windows. Continue with the
            // standard install locations and the manual chooser.
        }

        foreach (var candidate in GetStandardInstallCandidates())
        {
            if (File.Exists(candidate))
            {
                TryRememberExecutable(candidate);
                return candidate;
            }
        }

        return null;
    }

    public void RememberExecutable(string executablePath)
    {
        var path = Path.GetFullPath(executablePath);
        if (!File.Exists(path) || !string.Equals(Path.GetFileName(path), "PixPin.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException(LocalizationService.T("PixPinExecutableInvalid"), path);
        }

        Directory.CreateDirectory(AppSettings.SettingsDirectory);
        var temp = CachedExecutablePath + ".tmp";
        File.WriteAllText(temp, path);
        File.Move(temp, CachedExecutablePath, true);

        if (_settings is not null)
        {
            _settings.PixPinExecutablePath = path;
            _settings.Save();
        }
    }

    private void TryRememberExecutable(string executablePath)
    {
        try
        {
            RememberExecutable(executablePath);
        }
        catch
        {
            // Discovery must remain usable even when endpoint policy prevents
            // Pinboard from persisting the path hint.
        }
    }

    public string? FindConfiguration()
    {
        var executable = FindExecutable();
        if (executable is null)
        {
            return null;
        }
        var path = Path.Combine(Path.GetDirectoryName(executable)!, "Config", "PixPinConfig.json");
        return File.Exists(path) ? path : null;
    }

    public bool IsIntegrationPresent()
    {
        var configuration = FindConfiguration();
        if (configuration is null)
        {
            return false;
        }
        return File.ReadAllText(configuration).Contains("pinboard://capture-next", StringComparison.OrdinalIgnoreCase);
    }

    public string CreateConfigurationBackup()
    {
        EnsurePixPinStopped();
        var configuration = FindConfiguration()
            ?? throw new FileNotFoundException(LocalizationService.T("PixPinConfigMissing"));
        var backup = configuration + $".pinboard-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Copy(configuration, backup, overwrite: false);
        return backup;
    }

    public string RestoreLatestConfigurationBackup()
    {
        EnsurePixPinStopped();
        var configuration = FindConfiguration()
            ?? throw new FileNotFoundException(LocalizationService.T("PixPinConfigMissing"));
        var directory = Path.GetDirectoryName(configuration)!;
        var prefix = Path.GetFileName(configuration) + ".pinboard-backup-";
        var backup = Directory.EnumerateFiles(directory, prefix + "*")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
            ?? throw new FileNotFoundException(LocalizationService.T("PixPinBackupMissing"));
        var safety = configuration + $".before-restore-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Copy(configuration, safety, overwrite: false);
        File.Copy(backup, configuration, overwrite: true);
        return backup;
    }

    private static void EnsurePixPinStopped()
    {
        if (Process.GetProcessesByName("PixPin").Any())
        {
            throw new InvalidOperationException(LocalizationService.T("PixPinExitRequired"));
        }
    }

    public void StartCapture()
    {
        var executable = FindExecutable()
            ?? throw new FileNotFoundException(LocalizationService.T("PixPinExecutableMissing"));
        Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "-r \"pixpin.screenShot(ShotAction.Copy)\"",
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executable)
        });
    }

    private IEnumerable<string> GetRememberedCandidates()
    {
        if (!string.IsNullOrWhiteSpace(_settings?.PixPinExecutablePath))
        {
            yield return _settings.PixPinExecutablePath;
        }

        string? cached = null;
        try
        {
            if (File.Exists(CachedExecutablePath))
            {
                cached = File.ReadAllText(CachedExecutablePath).Trim();
            }
        }
        catch
        {
            // A stale or unreadable hint should not break screenshot capture.
        }

        if (!string.IsNullOrWhiteSpace(cached))
        {
            yield return cached;
        }
    }

    private static IEnumerable<string> GetStandardInstallCandidates()
    {
        foreach (var registryPath in new[]
                 {
                     @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\App Paths\PixPin.exe",
                     @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\App Paths\PixPin.exe"
                 })
        {
            string? value = null;
            try
            {
                value = Registry.GetValue(registryPath, string.Empty, null) as string;
            }
            catch
            {
                // Registry discovery is best effort.
            }
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value.Trim('"');
            }
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programs = Path.Combine(local, "Programs");
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var current = AppContext.BaseDirectory;
        foreach (var candidate in new[]
                 {
                     Path.Combine(local, "PixPin", "PixPin.exe"),
                     Path.Combine(programs, "PixPin", "PixPin.exe"),
                     Path.Combine(programFiles, "PixPin", "PixPin.exe"),
                     Path.Combine(programFilesX86, "PixPin", "PixPin.exe"),
                     Path.Combine(current, "PixPin.exe")
                 })
        {
            yield return candidate;
        }
    }
}
