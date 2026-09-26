using System.Windows;
using Pinboard.App.Models;
using Pinboard.App.Services;
using Forms = System.Windows.Forms;

namespace Pinboard.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly string? _activeDocumentPath;
    private readonly PixPinService _pixPin;

    public SettingsWindow(AppSettings settings, string? activeDocumentPath, PixPinService pixPin)
    {
        InitializeComponent();
        _settings = settings;
        _activeDocumentPath = activeDocumentPath;
        _pixPin = pixPin;
        _pixPin.Configure(settings);
        LibraryBox.Text = settings.LibraryPath;
        QualitySlider.Value = settings.WebpQuality;
        ChineseOcrBox.IsChecked = settings.OcrChinese;
        EnglishOcrBox.IsChecked = settings.OcrEnglish;
        PauseCaptureBox.IsChecked = settings.CapturePaused;
        FixedTargetBox.IsChecked = !string.IsNullOrWhiteSpace(settings.FixedCaptureTarget);
        FixedTargetBox.IsEnabled = !string.IsNullOrWhiteSpace(activeDocumentPath);
        TextCaptureBox.IsChecked = settings.TextCaptureEnabled;
        PauseTextCaptureBox.IsChecked = settings.TextCapturePaused;
        TextCaptureBoardModeBox.SelectedIndex = settings.TextCaptureBoardMode switch
        {
            TextCaptureBoardMode.Daily => 0,
            TextCaptureBoardMode.Monthly => 1,
            _ => 2
        };
        TextPrivacyModeBox.IsChecked = settings.TextPrivacyModeEnabled;
        ExcludedAppsBox.Text = string.Join("; ", settings.TextCaptureExcludedApplications);
        PrivacyMigrationNotice.Visibility = settings.TextPrivacyReviewPending
            ? Visibility.Visible
            : Visibility.Collapsed;
        StartupBox.IsChecked = settings.StartAtLogin;
        UpdateQualityText();
    }

    private void BrowseLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = LocalizationService.T("SettingsFolderDescription"),
            SelectedPath = Directory.Exists(LibraryBox.Text) ? LibraryBox.Text : AppSettings.DefaultLibraryPath
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            LibraryBox.Text = dialog.SelectedPath;
        }
    }

    private void OpenLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        var path = Directory.Exists(LibraryBox.Text) ? LibraryBox.Text : _settings.LibraryPath;
        Directory.CreateDirectory(path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{path}\"",
            UseShellExecute = true
        });
    }

    private void QualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateQualityText();

    private void UpdateQualityText()
    {
        if (QualityText is not null)
        {
            QualityText.Text = ((int)QualitySlider.Value).ToString();
        }
    }

    private void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            new FileAssociationService().Register();
            MessageBox.Show(this, LocalizationService.T("SettingsRepairSuccess"), "Pinboard", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, LocalizationService.T("SettingsRepairFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PixPinButton_Click(object sender, RoutedEventArgs e) =>
        new PixPinIntegrationWindow(_pixPin) { Owner = this }.ShowDialog();

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var library = Path.GetFullPath(LibraryBox.Text.Trim());
            Directory.CreateDirectory(library);
            _settings.LibraryPath = library;
            _settings.WebpQuality = (int)QualitySlider.Value;
            _settings.OcrChinese = ChineseOcrBox.IsChecked == true;
            _settings.OcrEnglish = EnglishOcrBox.IsChecked == true;
            _settings.CapturePaused = PauseCaptureBox.IsChecked == true;
            _settings.FixedCaptureTarget = FixedTargetBox.IsChecked == true ? _activeDocumentPath : null;
            _settings.TextCaptureEnabled = TextCaptureBox.IsChecked == true;
            _settings.TextCapturePaused = PauseTextCaptureBox.IsChecked == true;
            _settings.TextCaptureBoardMode = TextCaptureBoardModeBox.SelectedIndex switch
            {
                1 => TextCaptureBoardMode.Monthly,
                2 => TextCaptureBoardMode.LegacySingleBoard,
                _ => TextCaptureBoardMode.Daily
            };
            _settings.TextPrivacyModeEnabled = TextPrivacyModeBox.IsChecked == true;
            _settings.TextCaptureExcludedApplications = AppSettings.NormalizeApplicationNames(
                ExcludedAppsBox.Text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            _settings.TextPrivacyReviewPending = false;
            _settings.StartAtLogin = StartupBox.IsChecked == true;
            new StartupService().SetEnabled(_settings.StartAtLogin);
            _settings.Save();
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, LocalizationService.T("SettingsSaveFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
