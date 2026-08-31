using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using Pinboard.App.Services;

namespace Pinboard.App;

public partial class PixPinIntegrationWindow : Window
{
    private readonly PixPinService _pixPin;

    public PixPinIntegrationWindow(PixPinService pixPin)
    {
        InitializeComponent();
        _pixPin = pixPin;
        ScriptBox.Text = PixPinService.ActionScript;
        UpdateIntegrationStatus();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        ClipboardCaptureService.SetTextWithoutCapture(ScriptBox.Text);
        MessageBox.Show(this, LocalizationService.T("PixPinScriptCopied"), "Pinboard", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenPixPinButton_Click(object sender, RoutedEventArgs e)
    {
        var path = _pixPin.FindExecutable();
        if (path is null)
        {
            MessageBox.Show(this, LocalizationService.T("PixPinNotFound"), "Pinboard", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            Arguments = "-r \"pixpin.openConfigurationWindow()\"",
            UseShellExecute = true,
            WorkingDirectory = System.IO.Path.GetDirectoryName(path)
        });
    }

    private void CheckButton_Click(object sender, RoutedEventArgs e) => UpdateIntegrationStatus();

    private void ChoosePixPinButton_Click(object sender, RoutedEventArgs e)
    {
        var current = _pixPin.FindExecutable();
        var dialog = new OpenFileDialog
        {
            Title = LocalizationService.T("PixPinChooseExecutable"),
            Filter = LocalizationService.T("PixPinExecutableFilter"),
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = current is null ? null : Path.GetDirectoryName(current),
            FileName = "PixPin.exe"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _pixPin.RememberExecutable(dialog.FileName);
            IntegrationStatus.Text = string.Format(
                LocalizationService.T("PixPinExecutableSelectedFormat"),
                dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, LocalizationService.T("PixPinNotFound"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateIntegrationStatus()
    {
        try
        {
            IntegrationStatus.Text = _pixPin.IsIntegrationPresent()
                ? LocalizationService.T("PixPinCheckPresent")
                : LocalizationService.T("PixPinCheckMissing");
        }
        catch (Exception ex)
        {
            IntegrationStatus.Text = LocalizationService.T("PixPinCheckFailedFormat", ex.Message);
        }
    }

    private void BackupButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _pixPin.CreateConfigurationBackup();
            IntegrationStatus.Text = LocalizationService.T("PixPinBackupCreatedFormat", Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, LocalizationService.T("PixPinBackupFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, LocalizationService.T("PixPinRestoreConfirm"), LocalizationService.T("PixPinRestoreTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            var path = _pixPin.RestoreLatestConfigurationBackup();
            IntegrationStatus.Text = LocalizationService.T("PixPinRestoredFormat", Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, LocalizationService.T("PixPinRestoreFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
