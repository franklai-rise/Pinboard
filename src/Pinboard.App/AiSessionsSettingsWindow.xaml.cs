using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using Pinboard.App.Models;
using Pinboard.App.Services;
using Pinboard.App.Services.AiSessions;

namespace Pinboard.App;

public partial class AiSessionsSettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SessionArchiveService _service;
    private readonly GoogleDriveSessionCloud _drive;
    private readonly CancellationTokenSource _close = new();
    private bool _ready;
    private static string T(string en, string zh) => LocalizationService.CurrentLanguage == LocalizationService.Chinese ? zh : en;
    public AiSessionsSettingsWindow(AppSettings settings, SessionArchiveService service, GoogleDriveSessionCloud drive)
    {
        _settings = settings; _service = service; _drive = drive; InitializeComponent();
        Title = T("AI Sessions & Google Drive", "AI 对话与 Google Drive"); Heading.Text = Title;
        Subtitle.Text = T("Only checked conversations are backed up. Original records are uploaded without encryption.", "仅备份勾选的对话。上传原始记录，不额外加密。");
        ArchiveLabel.Text = T("Local archive", "本地资料库"); ArchiveBox.Text = service.Catalog.Root;
        PauseBox.Content = T("Pause discovery and backups", "暂停扫描与备份"); PauseBox.IsChecked = service.Paused;
        OpenFolderButton.Content = T("Open folder", "打开目录"); ImportButton.Content = T("Import app configuration", "导入应用配置");
        ConnectButton.Content = new System.Windows.Controls.TextBlock { Text = T("Connect", "连接"), Foreground = System.Windows.Media.Brushes.White }; DisconnectButton.Content = T("Disconnect", "断开"); RetryButton.Content = T("Retry selected", "重试所选");
        TestButton.Content = T("Test backup & restore", "测试上传与恢复");
        TestButton.ToolTip = T("Upload a small non-sensitive sample, download it, and verify its hashes.", "上传一条不含敏感内容的示例，再下载并校验。");
        CloseButton.Content = T("Done", "完成"); HelpHeading.Text = T("One-time setup", "首次连接步骤"); ConsoleButton.Content = T("Open Google Cloud setup ↗", "打开 Google Cloud 配置页面 ↗");
        HelpText.Text = T("1. Create a Google Cloud project and enable Google Drive API.\n2. Configure OAuth consent, then create a Desktop app client. Download its JSON and import it here.\n3. Connect in your browser and confirm the destination account.\n\nThe app requests drive.file access for its own backup files. Testing-mode refresh tokens can expire after seven days. Backups remain private and are never automatically deleted.",
            "1. 在 Google Cloud 创建项目并启用 Google Drive API。\n2. 配置 OAuth 同意页面，创建“桌面应用”客户端，下载 JSON 并在这里导入。\n3. 点击连接，在浏览器中登录并确认接收备份的账号。\n\n仅申请 drive.file 权限处理本功能创建的备份。测试状态的刷新令牌可能七天后过期。备份保持私有，不会自动删除。");
        _ready = true;
        Loaded += async (_, _) => await RefreshAsync(); Closed += (_, _) => _close.Cancel();
    }
    private async Task RefreshAsync()
    {
        ConnectButton.IsEnabled = _drive.IsConfigured; DisconnectButton.IsEnabled = _drive.IsConnected;
        TestButton.IsEnabled = _drive.IsConnected;
        AccountLabel.Text = _drive.IsConnected ? T("Checking account…", "正在读取账号…") : T("Not connected", "尚未连接");
        QuotaLabel.Text = _drive.IsConfigured ? T("Desktop app configuration imported", "已导入桌面应用配置") : T("Import your Desktop app configuration to connect.", "请先导入桌面应用配置。");
        if (!_drive.IsConnected) return;
        try
        {
            var account = await _drive.GetAccountAsync(_close.Token);
            AccountLabel.Text = account.DisplayName;
            QuotaLabel.Text = account.Available is { } available
                ? T("Available: ", "剩余空间：") + AiSessionsView.FormatSize(available) + " · " + T("Used: ", "已用：") + AiSessionsView.FormatSize(account.Usage)
                : T("Used: ", "已用：") + AiSessionsView.FormatSize(account.Usage) + T(" · Limit not provided by Google", " · Google 未返回容量上限");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ProgressLabel.Text = SessionArchiveService.FriendlyFailure(ex); }
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Google Cloud Desktop configuration|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { await _drive.ImportConfigurationAsync(dialog.FileName, _close.Token); await RefreshAsync(); }
        catch (Exception ex) { ProgressLabel.Text = ex.Message; }
    }
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false; ImportButton.IsEnabled = false;
        ProgressLabel.Text = T("Finish signing in in your browser…", "请在浏览器中完成登录…");
        try
        {
            var account = await _drive.ConnectAsync(_close.Token);
            var accept = MessageBox.Show(this, T("Upload selected conversation backups to ", "将勾选对话的备份上传到 ") + account.DisplayName + "?",
                T("Confirm Google Drive destination", "确认 Google Drive 接收账号"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (accept == MessageBoxResult.Yes) { _drive.ConfirmAccount(account.Id); await Task.Run(_service.QueueSelected); }
            else _drive.Disconnect();
            ProgressLabel.Text = accept == MessageBoxResult.Yes ? T("Connected. Only your selected conversations will upload.", "已连接，仅上传勾选的对话。") : T("Connection cancelled", "已取消连接");
        }
        catch (OperationCanceledException) { ProgressLabel.Text = T("Sign-in cancelled or timed out", "登录已取消或超时"); }
        catch (Exception ex) { ProgressLabel.Text = SessionArchiveService.FriendlyFailure(ex); }
        finally { ImportButton.IsEnabled = true; await RefreshAsync(); }
    }
    private async void Disconnect_Click(object sender, RoutedEventArgs e) { _drive.Disconnect(); await RefreshAsync(); }
    private async void Retry_Click(object sender, RoutedEventArgs e) { await Task.Run(_service.QueueSelected); ProgressLabel.Text = T("Selected conversations queued", "所选对话已加入队列"); }
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false; ProgressLabel.Text = T("Uploading and restoring a non-sensitive sample…", "正在上传并恢复一条无敏感信息的示例…");
        try
        {
            if (_service.Paused) throw new InvalidOperationException(T("Resume backups before running this test.", "请先恢复备份，再执行测试。"));
            await Task.Run(async () =>
            {
                var directory = Path.Combine(_service.Catalog.Root, "ConnectionTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
                var sample = Path.Combine(directory, "sample.txt");
                await File.WriteAllTextAsync(sample, "Pinboard Google Drive connection test. This sample contains no private conversation data.", _close.Token);
                var session = await _service.Imports.ImportAsync(sample, "Pinboard connection test", _close.Token);
                _service.Catalog.Merge([session]); _service.Catalog.Select(session.Key, true);
                try
                {
                    await _service.BackupAsync(session.Key, _close.Token);
                    var version = _service.Catalog.Versions(session.Key).FirstOrDefault(x => x.CloudFileId is not null)
                        ?? throw new InvalidDataException("The cloud has not confirmed this backup.");
                    await _service.RestoreAsync(version, directory, true, _close.Token);
                }
                finally { _service.Catalog.Select(session.Key, false); }
            }, _close.Token);
            ProgressLabel.Text = T("Passed: uploaded, downloaded, and all file hashes matched.", "通过：上传、下载成功，所有文件哈希一致。");
        }
        catch (OperationCanceledException) { ProgressLabel.Text = T("Test cancelled; local files are retained.", "测试已取消，本地文件已保留。"); }
        catch (Exception ex) { ProgressLabel.Text = SessionArchiveService.FriendlyFailure(ex); }
        finally { TestButton.IsEnabled = _drive.IsConnected; }
    }
    private void Pause_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return; _settings.AiSessionsPaused = PauseBox.IsChecked == true;
        _service.Paused = _settings.AiSessionsPaused; _settings.Save();
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _service.Catalog.Root + "\"") { UseShellExecute = true });
    private void Console_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://console.cloud.google.com/apis/dashboard") { UseShellExecute = true });
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
