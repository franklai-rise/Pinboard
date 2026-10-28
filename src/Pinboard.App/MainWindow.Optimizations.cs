using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Pinboard.App.Models;
using Pinboard.App.Services;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Pinboard.App;

public partial class MainWindow
{
    private CaptureInbox _captureInbox = null!;
    private OcrJobQueueService _ocrQueue = null!;
    private readonly HashSet<Task> _captureWrites = [];
    private readonly HashSet<string> _canvasFileIds = new(StringComparer.Ordinal);
    private string? _canvasDocumentPath;
    private long _expectedCanvasRevision = -1;
    private (string Path, object Payload)? _pendingFocus;
    private TextReadingView? _textReader;
    private string? _readerDocumentPath;
    private readonly SemaphoreSlim _readerGate = new(1, 1);
    private readonly Dictionary<string, TextReadingViewState> _readerStates = new(StringComparer.OrdinalIgnoreCase);

    private bool UseTextReader => !_showAiSessions && _settings.PreferTextReadingView && _activeDocument is { } document
        && TextClipsLibrary.GetDocumentKind(_settings.LibraryPath, document.FilePath) != TextClipsDocumentKind.None;
    private bool IsCanvasVisible => !UseTextReader && ReferenceEquals(CanvasHost.Content, CanvasView);
    private string ProfileDirectory => Path.GetDirectoryName(Path.GetFullPath(_settings.StoragePath))!;
    private static string UiText(string english, string chinese) =>
        LocalizationService.CurrentLanguage == LocalizationService.Chinese ? chinese : english;

    private void InitializeOptimizations()
    {
        _settings.ExpandedSidebarGroups ??= new(StringComparer.Ordinal);
        foreach (var item in _settings.ExpandedSidebarGroups) _expandedGroups[item.Key] = item.Value;
        _captureInbox = new CaptureInbox(Path.Combine(ProfileDirectory, "Recovery", "CaptureInbox"));
        _ocrQueue = new OcrJobQueueService(_ocrService, path => new PinboardDocument(path),
            Path.Combine(ProfileDirectory, "OcrQueue"));
        _captureInbox.ProcessingFailed += (_, exception) => Dispatcher.BeginInvoke(() =>
        {
            SetStatus(UiText("Capture kept in Recovery; retrying.", "收录内容已保存在 Recovery，正在等待重试。"));
            ShowNotification(L("CaptureFailedTitle"), exception.Message);
        });
        _ocrQueue.JobFailed += (_, _) => Dispatcher.BeginInvoke(() => SetStatus(
            UiText("OCR queued for retry; the image is saved.", "图片已保存，文字识别将在稍后重试。")));
    }

    private void StartProcessingQueues()
    {
        _ocrQueue.Start();
        _captureInbox.Start(async (job, token) =>
            await (await Dispatcher.InvokeAsync(() => ProcessPendingCaptureAsync(job, token))).ConfigureAwait(false));
    }

    private async Task JournalCaptureAsync(byte[]? image, string? text, DateTimeOffset capturedAt, uint sequence)
    {
        // Resolve the date and receiving board before compression/OCR or any awaited work.
        var target = image is not null ? _settings.ResolveCaptureTarget(capturedAt)
            : _settings.ResolveTextCaptureTarget(capturedAt);
        if (image is not null) await _captureInbox.EnqueueImageAsync(target, image, capturedAt, sequence);
        else await _captureInbox.EnqueueTextAsync(target, text!, capturedAt, sequence);
    }

    private async void TrackCaptureWrite(Task task)
    {
        _captureWrites.Add(task);
        try { await task; }
        catch (Exception exception)
        {
            DiagnosticLog.Write("capture-journal", exception);
            SetStatus(L("StatusCaptureFailedFormat", exception.Message));
            ShowNotification(L("CaptureFailedTitle"), exception.Message);
        }
        finally { _captureWrites.Remove(task); }
    }

    private async Task ProcessPendingCaptureAsync(PendingCapture job, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_exiting) await Task.Delay(Timeout.Infinite, token);
        await _captureGate.WaitAsync(token);
        var externalMutation = false;
        var refreshed = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (_exiting) throw new OperationCanceledException(token);
            byte[]? original = null;
            AssetRecord? asset = null;
            string? text = null;
            if (job.Kind == "image")
            {
                original = await _captureInbox.ReadImageAsync(job, token);
                asset = await Task.Run(() => _imageProcessor.Process(original, _settings.WebpQuality), token);
            }
            else text = await _captureInbox.ReadTextAsync(job, token);

            externalMutation = await BeginExternalMutationIfActiveAsync(job.TargetPath);
            var document = await OpenDocumentAsync(job.TargetPath,
                activate: !_showAiSessions && (_activeDocument is null || _activeDocument.FilePath.Equals(job.TargetPath, StringComparison.OrdinalIgnoreCase)),
                refreshCanvas: false, captureGateHeld: true);
            if (asset is not null)
            {
                var result = await Task.Run(() => document.InsertCapturedImageAsync(asset, job.Source,
                    token, job.CapturedAt, job.Id), token);
                _lastInsertedElements[job.TargetPath] = result.ElementId;
                // Do not acknowledge the capture journal until the original OCR input is durable.
                await _ocrQueue.EnqueueAsync(document.FilePath, result.AssetHash, original!, result.X, result.Y,
                    _settings.OcrChinese, _settings.OcrEnglish, token);
            }
            else
            {
                var result = await Task.Run(() => document.InsertCapturedTextAsync(text!, job.Source,
                    token, job.CapturedAt, job.Id), token);
                _lastInsertedElements[job.TargetPath] = result.ElementId;
            }
            if (_activeDocument == document)
            {
                if (UseTextReader && IsVisible && WindowState != WindowState.Minimized)
                    await ShowTextReaderAsync();
                else if (IsCanvasVisible && _webReady)
                    await SendOpenDocumentAsync(document, incremental: true);
                refreshed = true;
            }
            RequestSidebarRefresh();
            UpdateCaptureTargetText();
            SetStatus(L(job.Kind == "image" ? "StatusCaptureAddedFormat" : "StatusTextClipAddedFormat", document.Title));
        }
        finally
        {
            if (externalMutation && !refreshed)
                await ReloadActiveDocumentAfterExternalMutationFailureAsync(job.TargetPath);
            _captureGate.Release();
        }
    }

    private void HandleDocumentRendered(BridgeMessage message)
    {
        if (message.DocumentId != _activeDocument?.FilePath || message.DocumentId != _canvasDocumentPath
            || !message.Payload.TryGetProperty("revision", out var revision) || revision.GetInt64() != _expectedCanvasRevision)
            return;
        _canvasFileIds.Clear();
        if (message.Payload.TryGetProperty("fileIds", out var files))
            foreach (var file in files.EnumerateArray())
                if (file.GetString() is { } id) _canvasFileIds.Add(id);
        _canvasLoading = false;
        if (_pendingFocus is { } focus && focus.Path == message.DocumentId)
        {
            _pendingFocus = null;
            PostToCanvas("FocusElement", focus.Payload, focus.Path);
        }
        SetStatus(L("StatusReady"));
    }

    private void FocusCanvasWhenReady(object payload, string path)
    {
        if (!_webReady || _canvasLoading || _canvasDocumentPath != path)
        {
            _pendingFocus = (path, payload);
            _ = EnsureVisibleCanvasAsync();
        }
        else PostToCanvas("FocusElement", payload, path);
    }

    private async Task ShowTextReaderAsync()
    {
        await _readerGate.WaitAsync();
        try
        {
            if (!UseTextReader || _activeDocument is not { } document || !IsVisible) return;
            if (CanvasView is not null)
            {
                await FlushBeforeDocumentSwitchAsync();
                _webReady = false;
                CanvasView.Dispose();
                CanvasView = null;
                _canvasDocumentPath = null;
                _canvasFileIds.Clear();
            }
            _textReader ??= CreateTextReader();
            var restoreState = _readerDocumentPath != document.FilePath;
            if (restoreState) _textReader.Clear();
            var snapshot = await Task.Run(() => document.LoadSnapshotAsync(includeFiles: false));
            if (_activeDocument != document || !UseTextReader) return;
            _readerDocumentPath = document.FilePath;
            _textReader.ConfigureFavorites(Path.Combine(ProfileDirectory, "text-favorites.json"), document.FilePath);
            _textReader.LoadScene(snapshot.SceneJson, LocalizationService.CurrentLanguage);
            if (restoreState && _readerStates.TryGetValue(document.FilePath, out var state))
                _textReader.RestoreViewState(state);
            CanvasHost.Content = _textReader;
            HeaderSaveStatus.Text = L("SaveStateSaved");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("text-reader", exception);
            SetStatus(L("StatusReadBoardsFailedFormat", exception.Message));
        }
        finally { _readerGate.Release(); }
    }

    private async Task FlushBeforeDocumentSwitchAsync()
    {
        if (!_webReady || CanvasView is null || _canvasDocumentPath is null) return;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (_canvasLoading && DateTime.UtcNow < deadline) await Task.Delay(50);
        if (_canvasLoading) throw new TimeoutException(L("CanvasRequestTimeout"));
        var result = await RequestCanvasAsync("Flush", new { }, _canvasDocumentPath, TimeSpan.FromSeconds(30));
        if (result.TryGetProperty("skipped", out var skipped) && skipped.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException("The canvas has not confirmed its saved state.");
    }

    private TextReadingView CreateTextReader()
    {
        var view = new TextReadingView();
        view.NavigateToElement += async (_, args) =>
        {
            var document = _activeDocument;
            await SwitchToCanvasAsync();
            if (document is not null) FocusCanvasWhenReady(new { elementId = args.ElementId, query = args.Query }, document.FilePath);
        };
        return view;
    }

    private async Task SwitchToCanvasAsync()
    {
        RememberReaderState();
        _textReader?.Clear();
        _readerDocumentPath = null;
        _settings.PreferTextReadingView = false;
        _settings.Save();
        UpdateTextViewToggle();
        if (CanvasView is not null) CanvasHost.Content = CanvasView;
        else CanvasHost.Content = null;
        await EnsureVisibleCanvasAsync();
    }

    private void RememberReaderState()
    {
        if (_readerDocumentPath is not null && _textReader is not null)
            _readerStates[_readerDocumentPath] = _textReader.CaptureViewState();
    }

    private async void TextViewToggleButton_Click(object sender, RoutedEventArgs args)
    {
        if (UseTextReader) await SwitchToCanvasAsync();
        else
        {
            _settings.PreferTextReadingView = true;
            _settings.Save();
            UpdateTextViewToggle();
            await ShowTextReaderAsync();
        }
    }

    private void UpdateTextViewToggle()
    {
        if (_showAiSessions) { TextViewToggleButton.Visibility = Visibility.Collapsed; return; }
        var isText = _activeDocument is { } document && TextClipsLibrary.GetDocumentKind(_settings.LibraryPath, document.FilePath) != TextClipsDocumentKind.None;
        TextViewToggleButton.Visibility = isText ? Visibility.Visible : Visibility.Collapsed;
        TextViewToggleButton.Content = UseTextReader ? UiText("Canvas", "画布") : UiText("Read", "阅读");
        TextViewToggleButton.ToolTip = UseTextReader ? UiText("Arrange and edit on the canvas", "在画布中排版和编辑") : UiText("Lightweight text reading", "轻量文字阅读模式");
    }

    private void RequestSidebarRefresh()
    {
        _sidebarDirty = true;
        if (_initialized && (!IsVisible || WindowState == WindowState.Minimized)) return;
        _sidebarRefreshTimer.Stop();
        _sidebarRefreshTimer.Start();
    }

    private void ApplySidebarItems(IReadOnlyList<SidebarDocument> items)
    {
        for (var index = 0; index < items.Count; index++)
        {
            var wanted = items[index];
            if (index < _sidebarDocuments.Count && _sidebarDocuments[index] == wanted) continue;
            var existing = -1;
            for (var scan = index; scan < _sidebarDocuments.Count; scan++)
                if (_sidebarDocuments[scan].Path == wanted.Path && _sidebarDocuments[scan].ProjectName == wanted.ProjectName)
                { existing = scan; break; }
            if (existing >= 0)
            {
                if (existing != index) _sidebarDocuments.Move(existing, index);
                if (_sidebarDocuments[index] != wanted) _sidebarDocuments[index] = wanted;
            }
            else _sidebarDocuments.Insert(index, wanted);
        }
        while (_sidebarDocuments.Count > items.Count) _sidebarDocuments.RemoveAt(_sidebarDocuments.Count - 1);
    }

    private static string StableGroupKey(CollectionViewGroup group)
    {
        if (group.Items[0] is not SidebarDocument item) return "unknown";
        var prefix = item.Kind switch
        {
            SidebarDocumentKind.ScreenshotDaily => "screenshots/daily/",
            SidebarDocumentKind.ScreenshotHistory => "screenshots/history/",
            SidebarDocumentKind.TextClipsDaily => "text/daily/",
            SidebarDocumentKind.TextClipsLegacy or SidebarDocumentKind.TextClipsMonthly => "text/history/",
            SidebarDocumentKind.TextClipsManual => "text/manual/",
            _ => "project/"
        };
        var month = item.Kind is SidebarDocumentKind.ScreenshotDaily or SidebarDocumentKind.TextClipsDaily
            && item.Title.Length >= 7 ? item.Title[..7] : item.ProjectName;
        return prefix + month;
    }

    private void UpdateReceiveStatus(string screenshotTitle)
    {
        var image = _settings.CapturePaused ? UiText("Paused", "已暂停") : screenshotTitle;
        var text = !_settings.TextCaptureEnabled ? UiText("Off", "已关闭") : _settings.TextCapturePaused
            ? UiText("Paused", "已暂停") : Path.GetFileNameWithoutExtension(_settings.ResolveTextCaptureTarget(DateTimeOffset.Now));
        CaptureTargetText.Text = UiText($"Screenshots: {image}  ·  Text: {text}", $"截图：{image}  ·  文字：{text}");
        CaptureTargetText.ToolTip = UiText("Click to change receiving targets or pause collection", "点击切换接收画板或暂停收集");
    }

    private void ReceiveStatusButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement target) return;
        var menu = new ContextMenu { PlacementTarget = target };
        void Add(string label, Action action)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add(UiText("Screenshots · open receiving board", "截图 · 打开接收画板"), async () => await OpenDocumentAsync(_settings.ResolveCaptureTarget(DateTimeOffset.Now), true));
        Add(UiText(_settings.CapturePaused ? "Resume screenshots" : "Pause screenshots", _settings.CapturePaused ? "恢复截图收集" : "暂停截图收集"), () =>
        { _settings.CapturePaused = !_settings.CapturePaused; _settings.Save(); UpdateCaptureTargetText(); });
        Add(UiText("Screenshots · automatic daily", "截图 · 恢复每日自动接收"), UseMonthlyCaptureTarget);
        if (_activeDocument is { } board && !UseTextReader && TextClipsLibrary.GetDocumentKind(_settings.LibraryPath, board.FilePath) == TextClipsDocumentKind.None)
            Add(UiText("Receive screenshots in current board", "将当前画板设为截图接收目标"), () => { _settings.FixedCaptureTarget = board.FilePath; _settings.Save(); RequestSidebarRefresh(); UpdateCaptureTargetText(); });
        menu.Items.Add(new Separator());
        Add(UiText("Text · open receiving board", "文字 · 打开接收画板"), async () => await OpenDocumentAsync(_settings.ResolveTextCaptureTarget(DateTimeOffset.Now), true));
        Add(UiText(!_settings.TextCaptureEnabled ? "Enable text collection" : _settings.TextCapturePaused ? "Resume text collection" : "Pause text collection",
            !_settings.TextCaptureEnabled ? "开启文字收集" : _settings.TextCapturePaused ? "恢复文字收集" : "暂停文字收集"), () =>
        {
            if (!_settings.TextCaptureEnabled) _settings.TextCaptureEnabled = true;
            else _settings.TextCapturePaused = !_settings.TextCapturePaused;
            _settings.Save(); _clipboardCapture.ConfigureTextCapture(_settings); UpdateCaptureTargetText();
        });
        Add(UiText("Text · automatic daily", "文字 · 恢复每日自动接收"), UseAutomaticTextCaptureTarget);
        if (_activeDocument is { } textBoard && TextClipsLibrary.GetDocumentKind(_settings.LibraryPath, textBoard.FilePath) != TextClipsDocumentKind.None)
            Add(UiText("Receive text in current board", "将当前画板设为文字接收目标"), () => { _settings.FixedTextCaptureTarget = textBoard.FilePath; _settings.Save(); RequestSidebarRefresh(); UpdateCaptureTargetText(); });
        target.ContextMenu = menu;
        menu.Closed += (_, _) => target.ClearValue(FrameworkElement.ContextMenuProperty);
        menu.IsOpen = true;
    }
}
