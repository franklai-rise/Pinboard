using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Pinboard.App.Services;
using Pinboard.App.Services.AiSessions;
using Forms = System.Windows.Forms;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Pinboard.App;

public partial class AiSessionsView : System.Windows.Controls.UserControl
{
    private readonly SessionArchiveService _service;
    private readonly Action _openCloud;
    private readonly Action<bool> _pause;
    private readonly DispatcherTimer _reloadTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly DispatcherTimer _filterTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private IReadOnlyList<SessionEntry> _entries = [];
    private bool _loading;
    private bool _bulkSelectionBusy;
    private bool _issuesOnly;
    private SessionIssueFilter _issueFilter;
    private bool _diagnosticsBusy;
    private bool _progressReadBusy;
    private SessionBackupProgress? _progress;
    private long _reloadGeneration;
    private Task _selectionWrites = Task.CompletedTask;
    private static string T(string en, string zh) => LocalizationService.CurrentLanguage == LocalizationService.Chinese ? zh : en;
    public AiSessionsView(SessionArchiveService service, Action openCloud, Action<bool> pause)
    {
        _service = service; _openCloud = openCloud; _pause = pause;
        InitializeComponent();
        _reloadTimer.Tick += async (_, _) => { _reloadTimer.Stop(); await ReloadAsync(); };
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); RebuildRows(); };
        _progressTimer.Tick += async (_, _) => await RefreshProgressAsync();
        Loaded += async (_, _) => { ApplyLanguage(); if (IsVisible) _progressTimer.Start(); await ReloadAsync(); };
        Unloaded += (_, _) => { _reloadTimer.Stop(); _filterTimer.Stop(); _progressTimer.Stop(); };
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible && IsLoaded) { _progressTimer.Start(); await ReloadAsync(); }
            else { _progressTimer.Stop(); _reloadTimer.Stop(); _filterTimer.Stop(); CurrentBackupProgress.IsIndeterminate = false; }
        };
        _service.Changed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!IsLoaded || !IsVisible) return;
            if (!_reloadTimer.IsEnabled) _reloadTimer.Start();
        });
        SizeChanged += (_, _) => DetailColumn.Width = new GridLength(ActualWidth < 720 ? 230 : 270);
    }
    public void FocusSearch() => FilterBox.Focus();
    public void ApplyLanguage()
    {
        Heading.Text = T("AI Sessions", "AI 对话"); Subtitle.Text = T("Browse titles. Back up the conversations you select.", "浏览对话标题，按勾选持续备份。");
        BackupButton.Content = new TextBlock { Text = T("Back up selected", "备份所选"), Foreground = System.Windows.Media.Brushes.White }; CloudButton.Content = T("Google Drive settings…", "Google Drive 设置…");
        RestoreButton.Content = T("Restore…", "恢复…"); FilterBox.ToolTip = T("Search conversation titles", "搜索对话标题");
        SelectAllButton.Content = T("Select all", "全选"); ClearSelectionButton.Content = T("Deselect all", "取消全选");
        RetryIssuesButton.Content = T("Retry shown issues", "重试当前问题");
        DetailRetryButton.Content = T("Retry this item", "重试此条"); MissingImagesButton.Content = T("Missing images…", "缺失清单…");
        var source = SourceBox.SelectedIndex; _loading = true;
        IssueFilterBox.ItemsSource = new[] { T("All issues", "全部问题"), T("Missing source", "原始记录缺失"), T("Missing images", "图片缺失"), T("Transfer / access", "传输／访问问题") };
        IssueFilterBox.SelectedIndex = (int)_issueFilter;
        SourceBox.ItemsSource = new[] { T("All sources", "全部来源"), "Codex", "Claude Code", "Antigravity", T("Imported", "手动导入") };
        SourceBox.SelectedIndex = Math.Max(0, source); _loading = false;
        EmptyLabel.Text = T("No matching conversations. Use Refresh or import an official export.", "暂无匹配的对话。可刷新目录或导入官方导出文件。");
        RebuildRows();
    }
    public async Task ReloadAsync()
    {
        var generation = ++_reloadGeneration;
        try
        {
            var entries = await Task.Run(_service.Catalog.List);
            if (generation != _reloadGeneration) return; _entries = entries; RebuildRows(); await RefreshProgressAsync();
        }
        catch (Exception ex) { ActivityLabel.Text = ex.Message; }
    }
    private void Filter_Changed(object sender, TextChangedEventArgs e)
    { if (_filterTimer is null) return; _filterTimer.Stop(); _filterTimer.Start(); }
    private void Source_Changed(object sender, SelectionChangedEventArgs e) { if (!_loading && _service is not null) RebuildRows(); }
    private void RebuildRows()
    {
        if (SessionList is null) return;
        var selectedKey = (SessionList.SelectedItem as SessionRow)?.Key;
        var scroller = FindScroller(SessionList); var offset = scroller?.VerticalOffset ?? 0;
        var previousRows = SessionList.Items.Cast<SessionRow>().ToDictionary(x => x.Key, StringComparer.Ordinal);
        var query = FilterBox.Text.Trim();
        var all = _entries.ToDictionary(x => x.Key, StringComparer.Ordinal);
        var visible = MatchingEntries().Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in visible.ToArray())
        {
            var current = all[key]; var visited = new HashSet<string>();
            while (current.Descriptor.ParentKey is { } parent && all.TryGetValue(parent, out current!) && visited.Add(parent)) visible.Add(parent);
        }
        var children = _entries.Where(x => x.Descriptor.ParentKey is { } p && all.ContainsKey(p))
            .GroupBy(x => x.Descriptor.ParentKey!).ToDictionary(x => x.Key, x => x.ToArray());
        var rows = new List<SessionRow>(); var added = new HashSet<string>();
        foreach (var entry in _entries.Where(x => x.Descriptor.ParentKey is null || !all.ContainsKey(x.Descriptor.ParentKey))) Add(entry, 0);
        foreach (var entry in _entries.Where(x => visible.Contains(x.Key) && !added.Contains(x.Key)))
        {
            // Cycles/missing parent metadata must not make a selected conversation disappear.
            if (entry.Descriptor.ParentKey is not { } p || !visible.Contains(p) || query.Length > 0 || _issuesOnly) Add(entry, 0);
        }
        SessionList.ItemsSource = rows;
        SessionList.SelectedItem = rows.FirstOrDefault(x => x.Key == selectedKey);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FindScroller(SessionList)?.ScrollToVerticalOffset(offset));
        EmptyLabel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyLabel.Text = _issuesOnly ? T("No issues match this filter.", "当前筛选没有问题条目。")
            : T("No matching conversations. Use Refresh or import an official export.", "暂无匹配的对话。可刷新目录或导入官方导出文件。");
        var count = _entries.Count(x => x.Selected);
        ActivityLabel.Text = T($"{_entries.Count} conversations · {count} selected · ", $"{_entries.Count} 条对话 · 已勾选 {count} 条 · ") + StatusText(_service.Activity);
        UpdateSelectionActions();
        RenderProgress();
        ShowDetails();
        void Add(SessionEntry entry, int depth)
        {
            if (!visible.Contains(entry.Key) || !added.Add(entry.Key)) return;
            var hasChildren = children.TryGetValue(entry.Key, out var nested);
            var expanded = _expanded.Contains(entry.Key) || query.Length > 0 || _issuesOnly;
            var glyph = hasChildren ? expanded ? "⌄" : "›" : "";
            rows.Add(previousRows.TryGetValue(entry.Key, out var old) && old.Entry == entry && old.ExpandGlyph == glyph && old.Indent.Left == Math.Min(depth, 3) * 14
                ? old : new SessionRow(entry, depth, glyph, SelectedChanged));
            if (hasChildren && expanded && depth < 16) foreach (var child in nested!) Add(child, depth + 1);
        }
    }
    private IReadOnlyList<SessionEntry> MatchingEntries() => SessionSelection.Matching(_entries.Where(entry => !_issuesOnly
        || (entry.Selected && SessionIssuePresentation.Matches(entry, _issueFilter))), FilterBox.Text,
        SourceBox.SelectedIndex switch { 1 => "Codex", 2 => "Claude Code", 3 => "Antigravity", 4 => "Imported", _ => null });
    private bool HasFilters => _issuesOnly || SourceBox.SelectedIndex > 0 || !string.IsNullOrWhiteSpace(FilterBox.Text);
    private async Task RefreshProgressAsync()
    {
        if (_progressReadBusy || !IsLoaded || !IsVisible) return;
        _progressReadBusy = true;
        try { _progress = await Task.Run(_service.GetProgress); if (IsLoaded && IsVisible) RenderProgress(); }
        catch (Exception ex) { BackupCurrentLabel.Text = T("Unable to read backup progress: ", "暂时无法读取备份进度：") + ex.Message; }
        finally { _progressReadBusy = false; }
    }
    private void RenderProgress()
    {
        if (_progress is not { } progress || BackupProgressPanel is null) return;
        BackupProgressPanel.Visibility = progress.Total > 0 || _issuesOnly ? Visibility.Visible : Visibility.Collapsed;
        var text = SessionProgressPresentation.Create(progress, LocalizationService.CurrentLanguage == LocalizationService.Chinese);
        BackupProgressTitle.Text = text.Title; BackupPercentLabel.Text = text.Percentage;
        BackupProgressSummary.Text = text.Summary; BackupCurrentLabel.Text = text.Current; BackupCurrentLabel.ToolTip = text.Current;
        BackupProgressPanel.ToolTip = text.Tooltip; OverallBackupProgress.Value = progress.Percentage;
        OverallBackupProgress.Foreground = progress.IsComplete ? System.Windows.Media.Brushes.SeaGreen
            : progress.Paused ? System.Windows.Media.Brushes.SlateGray : System.Windows.Media.Brushes.DodgerBlue;
        CurrentBackupProgress.Visibility = progress.Active > 0 ? Visibility.Visible : Visibility.Collapsed;
        CurrentBackupProgress.IsIndeterminate = progress.Active > 0 && progress.Current?.TotalBytes is not > 0 && !progress.Paused;
        CurrentBackupProgress.Value = (progress.Current?.ByteFraction ?? 0) * 100;
        ProgressIssuesButton.Visibility = progress.Problems > 0 || _issuesOnly ? Visibility.Visible : Visibility.Collapsed;
        ProgressIssuesButton.Content = _issuesOnly ? T("Show all", "查看全部") : T($"View issues ({progress.Problems:N0})", $"查看问题（{progress.Problems:N0}）");
        ProgressIssuesButton.ToolTip = T("Filter problem entries without changing any selection or files", "只筛选问题条目，不改变勾选或文件");
        IssueActions.Visibility = _issuesOnly ? Visibility.Visible : Visibility.Collapsed;
        RetryIssuesButton.IsEnabled = !_bulkSelectionBusy && !_service.Paused && MatchingEntries().Any(entry => entry.Selected && SessionSelection.HasIssue(entry));
    }
    private void ProgressIssues_Click(object sender, RoutedEventArgs e) { _issuesOnly = !_issuesOnly; RebuildRows(); }
    private void IssueFilter_Changed(object sender, SelectionChangedEventArgs e)
    { if (!_loading && _service is not null && IssueFilterBox.SelectedIndex >= 0) { _issueFilter = (SessionIssueFilter)IssueFilterBox.SelectedIndex; RebuildRows(); } }
    private async void RetryIssues_Click(object sender, RoutedEventArgs e)
    {
        try { var keys = MatchingEntries().Where(entry => entry.Selected && SessionSelection.HasIssue(entry)).Select(entry => entry.Key).ToArray();
            await Task.Run(() => _service.Catalog.QueueMany(keys)); await ReloadAsync(); }
        catch (Exception ex) { Error(ex); }
    }
    private void UpdateSelectionActions()
    {
        var matches = MatchingEntries(); var selected = matches.Count(x => x.Selected);
        var filtered = HasFilters;
        SelectionCountLabel.Text = T($"{selected:N0} / {matches.Count:N0} selected", $"已选 {selected:N0} / {matches.Count:N0}");
        SelectionCountLabel.ToolTip = filtered ? T("Selection within the current filters, including folded children", "当前筛选范围内的勾选数，含折叠子对话")
            : T("Selection across all current conversations, including folded children", "当前全部对话的勾选数，含折叠子对话");
        SelectAllButton.ToolTip = filtered ? T("Select all matches, including folded children", "全选所有匹配项，含折叠子对话")
            : T("Select all current conversations, including folded children", "全选当前全部对话，含折叠子对话");
        ClearSelectionButton.ToolTip = T("Deselect within the same scope. Existing backups are kept.", "取消当前范围内的勾选；保留已有备份。");
        SelectAllButton.IsEnabled = !_bulkSelectionBusy && selected < matches.Count;
        ClearSelectionButton.IsEnabled = !_bulkSelectionBusy && selected > 0;
        BackupButton.IsEnabled = SessionList.IsEnabled = FilterBox.IsEnabled = SourceBox.IsEnabled = !_bulkSelectionBusy;
    }
    private async void SelectAll_Click(object sender, RoutedEventArgs e) => await SetMatchingSelectionAsync(true);
    private async void ClearSelection_Click(object sender, RoutedEventArgs e) => await SetMatchingSelectionAsync(false);
    private async Task SetMatchingSelectionAsync(bool selected)
    {
        if (_bulkSelectionBusy) return;
        _bulkSelectionBusy = true; UpdateSelectionActions();
        try
        {
            try { await _selectionWrites; } catch { } // A prior row write has already reported its error.
            await ReloadAsync();
            var scope = MatchingEntries();
            if (!scope.Any(x => x.Selected != selected)) return;
            if (selected)
            {
                var dialog = new AiSessionSelectionWindow(scope, HasFilters)
                    { Owner = Window.GetWindow(this) };
                if (dialog.ShowDialog() != true) return;
            }
            await WriteSelectionAsync(scope.Select(x => x.Key).ToArray(), selected);
            await ReloadAsync();
        }
        catch (Exception ex) { Error(ex); await ReloadAsync(); }
        finally { _bulkSelectionBusy = false; UpdateSelectionActions(); }
    }
    private Task WriteSelectionAsync(IReadOnlyList<string> keys, bool selected)
    {
        var previous = _selectionWrites;
        return _selectionWrites = Task.Run(async () =>
        {
            try { await previous; } catch { }
            _service.Catalog.SetSelection(keys, selected);
        });
    }
    private static ScrollViewer? FindScroller(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScroller(child) is { } nested) return nested;
        }
        return null;
    }
    private async void SelectedChanged(SessionRow row, bool selected)
    {
        try { await WriteSelectionAsync([row.Key], selected); await ReloadAsync(); }
        catch (Exception ex) { Error(ex); await ReloadAsync(); }
    }
    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string key }) { if (!_expanded.Add(key)) _expanded.Remove(key); RebuildRows(); }
        e.Handled = true;
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => ShowDetails();
    private async void ShowDetails()
    {
        if (DetailTitle is null) return;
        var row = SessionList.SelectedItem as SessionRow;
        var selectedVersion = (VersionBox.SelectedItem as VersionChoice)?.Version.Id;
        DetailTitle.Text = row?.Title ?? T("Choose a conversation", "选择一条对话");
        PathBox.Text = row?.Entry.Descriptor.Path ?? "";
        PathBox.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        DetailError.Text = row?.Entry.Error ?? ""; RestoreButton.IsEnabled = false;
        DetailError.Visibility = DetailError.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailHelp.Text = ""; DetailRetryButton.Visibility = row is { Entry.Selected: true } && SessionSelection.HasIssue(row.Entry) ? Visibility.Visible : Visibility.Collapsed;
        DetailHelp.Visibility = Visibility.Collapsed;
        DetailActions.Visibility = row is not null && SessionSelection.HasIssue(row.Entry) ? Visibility.Visible : Visibility.Collapsed;
        MissingImagesButton.Visibility = row?.Entry.Status.Contains("missing attachments", StringComparison.Ordinal) == true ? Visibility.Visible : Visibility.Collapsed;
        RestoreButton.Visibility = Visibility.Collapsed; VersionBox.Visibility = Visibility.Collapsed;
        VersionBox.ItemsSource = null;
        if (row is null) { DetailInfo.Text = T("Check a conversation to keep backing up future changes. New conversations stay unchecked.", "勾选后将持续备份这条对话的更新。新增对话默认不勾选。"); return; }
        var d = row.Entry.Descriptor;
        DetailInfo.Text = d.Source + "\n" + T("Project: ", "项目：") + d.Project + "\n\n" + d.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            + " · " + FormatSize(d.Size) + (d.PreviewTitle ? "\n" + T("Title is an automatic preview", "标题为首条消息预览") : "")
            + (d.Archived ? "\n" + T("Archived in source app", "源应用已归档") : "") + "\n" + StatusText(d.Availability)
            + "\n\n" + T("Last local backup: ", "最近本地备份：") + (row.Entry.LastBackup?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—");
        try
        {
            var versions = await Task.Run(() => _service.Catalog.Versions(row.Key));
            if ((SessionList.SelectedItem as SessionRow)?.Key != row.Key) return;
            var job = await Task.Run(() => _service.Catalog.GetJob(row.Key));
            if ((SessionList.SelectedItem as SessionRow)?.Key != row.Key) return;
            DetailHelp.Text = SessionIssuePresentation.Help(row.Entry, job, LocalizationService.CurrentLanguage == LocalizationService.Chinese);
            DetailHelp.Visibility = DetailHelp.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            VersionBox.ItemsSource = versions.Select(x => new VersionChoice(x,
                x.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm:ss") + " · " + FormatSize(x.Size))).ToArray();
            VersionBox.SelectedIndex = versions.Count > 0 ? Math.Max(0, versions.ToList().FindIndex(version => version.Id == selectedVersion)) : -1; RestoreButton.IsEnabled = versions.Count > 0;
            RestoreButton.Visibility = VersionBox.Visibility = versions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { DetailError.Text = ex.Message; }
    }
    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        try { await _selectionWrites; await Task.Run(() => _service.ScanAsync()); await Task.Run(_service.QueueSelected); await ReloadAsync(); }
        catch (Exception ex) { Error(ex); }
    }
    private void Cloud_Click(object sender, RoutedEventArgs e) => _openCloud();
    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        Add(menu, T("Refresh titles", "刷新标题目录"), async () => { await Task.Run(() => _service.ScanAsync()); await ReloadAsync(); });
        Add(menu, _service.Paused ? T("Resume backups", "恢复备份") : T("Pause backups", "暂停备份"), () => { _pause(!_service.Paused); RebuildRows(); return Task.CompletedTask; });
        Add(menu, T("Retry failed or incomplete backups", "重试失败或不完整的备份"), async () => { await Task.Run(_service.QueueProblems); await ReloadAsync(); });
        Add(menu, T("Check duplicates and backup health…", "检查重复与备份状态…"), InspectAsync);
        Add(menu, T("Import official export…", "导入官方导出文件…"), ImportAsync);
        Add(menu, T("Google Drive settings…", "Google Drive 设置…"), () => { _openCloud(); return Task.CompletedTask; });
        Add(menu, T("Recover cloud backup index…", "找回云端备份目录…"), async () => { ActivityLabel.Text = T("Reading cloud manifests…", "正在读取云端版本清单…"); await Task.Run(() => _service.RecoverCloudIndexAsync()); await ReloadAsync(); });
        Add(menu, T("Open archive folder", "打开资料库目录"), () => { OpenFolder(_service.Catalog.Root); return Task.CompletedTask; });
        menu.PlacementTarget = (UIElement)sender; menu.IsOpen = true;
    }
    private void Row_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SessionRow row }) return;
        SessionList.SelectedItem = row; var menu = new ContextMenu();
        Add(menu, T("Rename display name…", "修改显示名称…"), async () =>
        {
            var dialog = new RenameBoardWindow(row.Title, row.Entry.Descriptor.Path ?? "") { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() == true) { await Task.Run(() => _service.Catalog.Rename(row.Key, dialog.ResultDisplayName)); await ReloadAsync(); }
        });
        Add(menu, T("Copy source path", "复制源文件路径"), () => { ClipboardCaptureService.SetTextWithoutCapture(row.Entry.Descriptor.Path ?? ""); return Task.CompletedTask; });
        Add(menu, T("Show in File Explorer", "在资源管理器中显示"), () =>
        {
            if (File.Exists(row.Entry.Descriptor.Path)) Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + row.Entry.Descriptor.Path + "\"") { UseShellExecute = true });
            return Task.CompletedTask;
        });
        Add(menu, T("Back up now", "立即备份"), async () =>
        {
            await Task.Run(() => { _service.Catalog.Select(row.Key, true); _service.Catalog.Queue(row.Key); }); await ReloadAsync();
        });
        if (row.Entry.Status.Contains("missing attachments", StringComparison.Ordinal)) Add(menu, T("View missing images…", "查看缺失图片清单…"), () => ShowMissingImagesAsync(row.Entry));
        menu.PlacementTarget = (UIElement)sender; menu.IsOpen = true; e.Handled = true;
    }
    private async void DetailRetry_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionRow row) return;
        try { await Task.Run(() => _service.Catalog.Queue(row.Key)); await ReloadAsync(); } catch (Exception ex) { Error(ex); }
    }
    private async void MissingImages_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionRow row) return;
        try { await ShowMissingImagesAsync(row.Entry); } catch (Exception ex) { Error(ex); }
    }
    private async Task ShowMissingImagesAsync(SessionEntry entry)
    {
        var latest = await Task.Run(() => _service.Catalog.Versions(entry.Key).FirstOrDefault()); if (latest is null) return;
        var paths = await Task.Run(async () => await _service.MissingAttachmentsAsync(await SessionArchiveService.ReadManifestAsync(latest.ManifestPath)));
        var body = T("These original image paths are missing. Restore files if available and refresh. This view does not change backups.\n\n", "以下原始图片路径已缺失。若能找回文件，请放回对应位置后刷新。此窗口不会修改备份。\n\n") + string.Join(Environment.NewLine + Environment.NewLine, paths);
        new AiSessionReportWindow(T("Missing images", "缺失图片清单"), body) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
    private async Task InspectAsync()
    {
        if (_diagnosticsBusy) return; _diagnosticsBusy = true;
        ActivityLabel.Text = T("Checking local versions and Drive metadata (read-only)…", "正在只读检查本地版本与 Drive 元信息…");
        try
        {
            SessionArchiveAudit audit; string cloudFailure = "";
            try { audit = await Task.Run(() => _service.InspectAsync()); }
            catch (System.Net.Http.HttpRequestException ex) { audit = await Task.Run(() => _service.InspectAsync(false)); cloudFailure = SessionArchiveService.FriendlyFailure(ex); }
            catch (DriveAuthorizationException ex) { audit = await Task.Run(() => _service.InspectAsync(false)); cloudFailure = SessionArchiveService.FriendlyFailure(ex); }
            var body = SessionArchiveDiagnostics.Present(audit, LocalizationService.CurrentLanguage == LocalizationService.Chinese);
            if (cloudFailure.Length > 0) body += Environment.NewLine + Environment.NewLine + cloudFailure;
            new AiSessionReportWindow(T("Backup health & duplicates", "备份状态与重复检查"), body) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        finally { _diagnosticsBusy = false; await ReloadAsync(); }
    }
    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (VersionBox.SelectedItem is not VersionChoice version) return;
        var menu = new ContextMenu(); Add(menu, T("Restore local backup…", "恢复本地备份…"), () => RestoreAsync(version.Version, false));
        if (version.Version.CloudFileId is not null) Add(menu, T("Download and verify cloud backup…", "下载并校验云端备份…"), () => RestoreAsync(version.Version, true));
        menu.PlacementTarget = RestoreButton; menu.IsOpen = true;
    }
    private async Task RestoreAsync(SessionVersion version, bool cloud)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = T("Choose a restore destination. A new subfolder will be created.", "选择恢复位置，将创建新的子文件夹。") };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        RestoreButton.IsEnabled = false; ActivityLabel.Text = T("Restoring and checking hashes…", "正在恢复并校验文件…");
        try { var path = await Task.Run(() => _service.RestoreAsync(version, dialog.SelectedPath, cloud)); OpenFolder(path); }
        finally { RestoreButton.IsEnabled = true; }
    }
    private async Task ImportAsync()
    {
        var dialog = new OpenFileDialog { Title = T("Import an official conversation export", "导入官方对话导出文件"), Filter = "Exports|*.json;*.jsonl;*.zip;*.html;*.md;*.txt|All files|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var name = new RenameBoardWindow(Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName) { Owner = Window.GetWindow(this) };
        if (name.ShowDialog() != true) return;
        var item = await Task.Run(() => _service.Imports.ImportAsync(dialog.FileName, name.ResultDisplayName!, CancellationToken.None));
        await Task.Run(() => _service.Catalog.Merge([item])); await ReloadAsync();
    }
    private void Add(ContextMenu menu, string text, Func<Task> action)
    {
        var item = new MenuItem { Header = text }; item.Click += async (_, _) => { try { await action(); } catch (Exception ex) { Error(ex); } }; menu.Items.Add(item);
    }
    private void Error(Exception ex) => MessageBox.Show(Window.GetWindow(this), SessionArchiveService.FriendlyFailure(ex), T("AI Sessions", "AI 对话"), MessageBoxButton.OK, MessageBoxImage.Warning);
    private static void OpenFolder(string path) => Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
    public static string FormatSize(long size) => size >= 1024 * 1024 * 1024 ? $"{size / (1024d * 1024 * 1024):0.00} GB" : size >= 1024 * 1024 ? $"{size / (1024d * 1024):0.0} MB" : $"{size / 1024d:0.0} KB";
    public static string StatusText(string value) => LocalizationService.CurrentLanguage != LocalizationService.Chinese ? value
        : value.StartsWith("Cloud backup (missing source", StringComparison.Ordinal) ? value.Contains("attachments") ? "云端部分保存 · 记录与附件缺失" : "云端部分保存 · 原始记录缺失"
        : value.StartsWith("Local backup (missing source", StringComparison.Ordinal) ? value.Contains("attachments") ? "本地部分保存 · 记录与附件缺失" : "本地部分保存 · 原始记录缺失"
        : value.StartsWith("Uploading selected conversation…", StringComparison.Ordinal) ? value.Replace("Uploading selected conversation…", "正在上传所选对话…")
        : value.StartsWith("Uploading ", StringComparison.Ordinal) ? value.Replace("Uploading ", "正在上传 ") : value switch
    {
        "Not selected" => "未勾选", "Selected" => "已勾选", "Backing up" => "正在本地备份", "Local backup" => "本地备份完成",
        "Uploading" => "正在上传", "Cloud backup" => "云端备份完成", "Retry pending" => "等待重试", "Available" => "源文件可用",
        "Missing source" => "源文件缺失", "Ready" => "就绪", "Scanning titles…" => "正在扫描标题…", "Paused" => "已暂停",
        "Needs source" => "等待找回源文件", "Sign-in required" => "需要重新连接", "Storage full" => "云端空间不足", "Access required" => "需要访问权限", "Needs review" => "需要检查",
        "Local backup (missing attachments)" => "本地已保存 · 附件缺失", "Cloud backup (missing attachments)" => "云端已保存 · 附件缺失",
        "Creating local snapshot…" => "正在生成本地快照…", "Uploading selected conversation…" => "正在上传所选对话…",
        "Selected conversations queued" => "所选对话已加入队列", "Backup needs attention" => "备份需要处理", _ => value
    };
    private sealed record VersionChoice(SessionVersion Version, string Label);
    public sealed class SessionRow : INotifyPropertyChanged
    {
        public SessionEntry Entry { get; }
        public string Key => Entry.Key;
        public string Title => Entry.Title;
        public string Description => Entry.Descriptor.Source + " · " + Entry.Descriptor.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm") + " · " +
            (string.IsNullOrEmpty(Entry.Descriptor.Project) ? "—" : Path.GetFileName(Entry.Descriptor.Project.TrimEnd('\\', '/')));
        public string Status => StatusText(Entry.Status);
        public System.Windows.Media.Brush StatusBrush => SessionSelection.HasIssue(Entry) ? System.Windows.Media.Brushes.DarkGoldenrod
            : Entry.Status == "Cloud backup" ? System.Windows.Media.Brushes.SeaGreen
            : Entry.Status is "Not selected" or "Local backup" ? System.Windows.Media.Brushes.Gray
            : Entry.Status == "Retry pending" ? System.Windows.Media.Brushes.DarkGoldenrod : System.Windows.Media.Brushes.DodgerBlue;
        public string SelectionHelp => T("Back up this conversation and its future updates", "备份这条对话及后续更新");
        public Thickness Indent { get; }
        public string ExpandGlyph { get; }
        private bool _selected;
        private readonly Action<SessionRow, bool> _changed;
        public bool Selected { get => _selected; set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); _changed(this, value); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        public SessionRow(SessionEntry entry, int depth, string glyph, Action<SessionRow, bool> changed)
        { Entry = entry; _selected = entry.Selected; _changed = changed; Indent = new(Math.Min(depth, 3) * 14, 0, 0, 0); ExpandGlyph = glyph; }
    }
}
