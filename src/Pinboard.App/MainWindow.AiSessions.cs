using System.Windows;
using Pinboard.App.Services.AiSessions;

namespace Pinboard.App;

public partial class MainWindow
{
    private SessionArchiveService? _aiSessions;
    private GoogleDriveSessionCloud? _aiDrive;
    private AiSessionsView? _aiSessionsView;
    private bool _showAiSessions;
    private Task? _aiInitialization;
    private Task EnsureAiSessionsAsync()
    {
        if (_aiInitialization?.IsCompleted == true && _aiSessions is null) _aiInitialization = null;
        return _aiInitialization ??= InitializeAiSessionsAsync();
    }
    private async Task InitializeAiSessionsAsync()
    {
        try
        {
            var root = _settings.ResolveAiSessionsArchivePath();
            var catalog = await Task.Run(() => new SessionCatalog(root));
            if (string.IsNullOrWhiteSpace(_settings.AiSessionsArchivePath))
            { _settings.AiSessionsArchivePath = root; _settings.Save(); }
            _aiDrive = new GoogleDriveSessionCloud(catalog, ProfileDirectory);
            _aiSessions = new SessionArchiveService(catalog, SessionArchiveService.LocalSources(root, _settings.AiSessionsSourceProfilePath), _aiDrive) { Paused = _settings.AiSessionsPaused };
            _aiSessions.Start();
        }
        catch (Exception ex) { _aiInitialization = null; SetStatus(UiText("AI archive unavailable: ", "AI 资料库不可用：") + ex.Message); }
    }
    private async void AiSessionsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await FlushBeforeDocumentSwitchAsync(); RememberReaderState();
            _showAiSessions = true; await EnsureAiSessionsAsync();
            await ShowAiSessionsAsync();
            _sidebarSelectionChanging = true; SidebarList.SelectedItem = null; _sidebarSelectionChanging = false;
        }
        catch (Exception ex) { _showAiSessions = false; MessageBox.Show(this, ex.Message, "Pinboard", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async Task ShowAiSessionsAsync()
    {
        if (!_showAiSessions || !IsVisible) return;
        await EnsureAiSessionsAsync(); if (_aiSessions is null) return;
        if (CanvasView is not null)
        {
            await FlushBeforeDocumentSwitchAsync(); _webReady = false; CanvasView.Dispose(); CanvasView = null;
            _webInitialization = null; _canvasDocumentPath = null; _canvasFileIds.Clear();
        }
        _aiSessionsView ??= new AiSessionsView(_aiSessions, OpenAiSessionsSettings, paused =>
        { _settings.AiSessionsPaused = paused; _aiSessions.Paused = paused; _settings.Save(); });
        CanvasHost.Content = _aiSessionsView;
        AiSessionsSidebarButton.Background = (System.Windows.Media.Brush)FindResource("AccentSoftBrush");
        HeaderDocumentTitle.Text = UiText("AI Sessions", "AI 对话"); HeaderSaveStatus.Text = UiText("Selected conversations · automatic backups", "按勾选自动备份对话");
        Title = "AI Sessions - Pinboard"; TextViewToggleButton.Visibility = Visibility.Collapsed;
        SetStatus(UiText("AI archive · local snapshots and Google Drive backups", "AI 资料库 · 本地快照与 Google Drive 备份"));
        _aiSessionsView.ApplyLanguage(); await _aiSessionsView.ReloadAsync();
    }
    private void OpenAiSessionsSettings()
    {
        if (_aiSessions is not null && _aiDrive is not null) new AiSessionsSettingsWindow(_settings, _aiSessions, _aiDrive) { Owner = this }.ShowDialog();
    }
}
