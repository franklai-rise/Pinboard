using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Pinboard.App.Interop;
using Pinboard.App.Models;
using Pinboard.App.Services;
using Forms = System.Windows.Forms;
using FileSystem = Microsoft.VisualBasic.FileIO.FileSystem;
using RecycleOption = Microsoft.VisualBasic.FileIO.RecycleOption;
using UIOption = Microsoft.VisualBasic.FileIO.UIOption;

namespace Pinboard.App;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly bool _startHidden;
    private readonly ImageProcessor _imageProcessor = new();
    private readonly OcrService _ocrService = new();
    private readonly SearchService _searchService = new();
    private readonly PixPinService _pixPin = new();
    private readonly ClipboardCaptureService _clipboardCapture = new();
    private readonly PixPinDefaultShortcutListener _defaultPixPinShortcut = new();
    private readonly Dictionary<string, PinboardDocument> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<SidebarDocument> _sidebarDocuments = [];
    private readonly Dictionary<string, string> _lastInsertedElements = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pendingCanvasRequests = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _ocrGate = new(2, 2);
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly DispatcherTimer _canvasIdleTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _sidebarRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly Dictionary<string, (long Modified, long Length, string Title)> _titleCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _expandedGroups = new(StringComparer.Ordinal);
    private Microsoft.Web.WebView2.Wpf.WebView2? CanvasView;
    private Task? _webInitialization;
    private JsonElement? _sleepViewport;
    private string? _sleepDocumentPath;
    private bool _sidebarDirty;
    private bool _exiting;
    private bool _canvasLoading;
    private readonly ObsidianImporter _obsidianImporter;
    private ProjectLibraryService _projectLibrary;
    private Forms.NotifyIcon? _notifyIcon;
    private System.Drawing.Icon? _trayIcon;
    private Forms.ToolStripMenuItem? _trayTargetItem;
    private Forms.ToolStripMenuItem? _trayPauseItem;
    private Forms.ToolStripMenuItem? _trayOpenItem;
    private Forms.ToolStripMenuItem? _trayCaptureItem;
    private Forms.ToolStripMenuItem? _trayPixPinItem;
    private Forms.ToolStripMenuItem? _trayExitItem;
    private FileSystemWatcher? _libraryWatcher;
    private PinboardDocument? _activeDocument;
    private bool _webReady;
    private bool _sidebarSelectionChanging;
    private bool _explicitExit;
    private bool _initialized;

    public MainWindow(AppSettings settings, bool startHidden)
    {
        InitializeComponent();
        _settings = settings;
        _startHidden = startHidden;
        _obsidianImporter = new ObsidianImporter(_imageProcessor, _ocrService);
        _projectLibrary = new ProjectLibraryService(settings.LibraryPath);
        var sidebarView = CollectionViewSource.GetDefaultView(_sidebarDocuments);
        sidebarView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SidebarDocument.SectionDisplayName)));
        SidebarList.ItemsSource = sidebarView;
        ApplySidebarState();
        ApplyAlwaysOnTopState();
        RefreshSidebarDocuments();
        ConfigureLibraryWatcher();
        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
        _canvasIdleTimer.Tick += async (_, _) => await ReleaseIdleCanvasAsync();
        _sidebarRefreshTimer.Tick += (_, _) => { _sidebarRefreshTimer.Stop(); RefreshSidebarDocuments(); };
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) _canvasIdleTimer.Start();
            else { _canvasIdleTimer.Stop(); if (IsVisible && _initialized) _ = EnsureVisibleCanvasAsync(); }
        };
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        _clipboardCapture.ConfigureTextCapture(_settings);
        _clipboardCapture.ImageCaptured += ClipboardCapture_ImageCaptured;
        _clipboardCapture.TextCaptured += ClipboardCapture_TextCaptured;
        _clipboardCapture.CaptureTimedOut += (_, _) => Dispatcher.Invoke(() => SetStatus(L("StatusCaptureTimedOut")));
        _defaultPixPinShortcut.ShortcutPressed += (_, _) => Dispatcher.BeginInvoke(ArmCaptureForDefaultPixPinShortcut);
        UpdateCaptureTargetText();
    }

    public async Task InitializeAsync(string[] startupArgs)
    {
        if (_initialized)
        {
            await HandleActivationAsync(startupArgs);
            return;
        }
        _initialized = true;

        if (_startHidden)
        {
            // Windows can restore the previous maximized state at sign-in. WPF does
            // not allow that state to be shown with ShowActivated disabled, so reset
            // it before creating the hidden background window.
            WindowState = WindowState.Normal;
            ShowActivated = false;
            Opacity = 0;
            ShowInTaskbar = false;
        }
        else
        {
            ShowActivated = true;
        }
        Show();

        try
        {
            _defaultPixPinShortcut.Start();
        }
        catch (Exception ex)
        {
            SetStatus(L("StatusDefaultPixPinShortcutFailedFormat", ex.Message));
        }

        InitializeTray();
        if (_startHidden)
        {
            Hide();
            Opacity = 1;
        }

        var handled = await HandleActivationAsync(startupArgs, revealOnEmptyInvocation: false);
        if (!_startHidden)
        {
            if (!handled && _settings.RecentFiles.FirstOrDefault(File.Exists) is { } recent)
            {
                await OpenDocumentAsync(recent, activate: true);
            }
            await EnsureVisibleCanvasAsync();
        }
    }

    public async Task<bool> HandleActivationAsync(string[] args, bool revealOnEmptyInvocation = true)
    {
        if (args.Any(ActivationParser.IsCaptureArgument))
        {
            ArmCaptureOnly();
            return true;
        }

        var backgroundRequested = args.Any(ActivationParser.IsBackgroundArgument);

        if (args.Length == 0 && revealOnEmptyInvocation)
        {
            ShowAndActivate();
            return true;
        }

        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--open", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                await OpenDocumentAsync(args[index + 1], activate: true);
                if (!backgroundRequested)
                {
                    ShowAndActivate();
                }
                return true;
            }
            if (string.Equals(args[index], "--import-obsidian", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                await ImportObsidianAsync(args[index + 1]);
                if (!backgroundRequested)
                {
                    ShowAndActivate();
                }
                return true;
            }
            if (File.Exists(args[index]) && Path.GetExtension(args[index]).Equals(".pinboard", StringComparison.OrdinalIgnoreCase))
            {
                await OpenDocumentAsync(args[index], activate: true);
                if (!backgroundRequested)
                {
                    ShowAndActivate();
                }
                return true;
            }
        }
        return backgroundRequested;
    }

    private async Task EnsureVisibleCanvasAsync()
    {
        if (!IsVisible || WindowState == WindowState.Minimized || _exiting) return;
        if (_sidebarDirty) RefreshSidebarDocuments();
        if (CanvasView is null)
        {
            _webInitialization ??= InitializeWebViewAsync();
            try { await _webInitialization; }
            finally { _webInitialization = null; }
        }
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var userData = Path.Combine(AppSettings.SettingsDirectory, "WebView2");
            Directory.CreateDirectory(userData);
            var environment = await CoreWebView2Environment.CreateAsync(null, userData);
            var view = new Microsoft.Web.WebView2.Wpf.WebView2
            {
                DefaultBackgroundColor = System.Drawing.Color.White
            };
            CanvasView = view;
            CanvasHost.Content = view;
            await view.EnsureCoreWebView2Async(environment);
            var core = view.CoreWebView2;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            core.NewWindowRequested += (_, eventArgs) => eventArgs.Handled = true;
            core.NavigationStarting += (_, eventArgs) =>
            {
                if (!IsTrustedCanvasUri(eventArgs.Uri))
                {
                    eventArgs.Cancel = true;
                }
            };
            core.FrameNavigationStarting += (_, eventArgs) =>
            {
                if (!IsTrustedCanvasUri(eventArgs.Uri))
                {
                    eventArgs.Cancel = true;
                }
            };
            core.WebMessageReceived += Core_WebMessageReceived;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, eventArgs) =>
            {
                if (!IsTrustedCanvasUri(eventArgs.Request.Uri))
                {
                    eventArgs.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
                }
            };

            var webFolder = EmbeddedWebAssets.EnsureExtracted();
            core.SetVirtualHostNameToFolderMapping("app.pinboard", webFolder, CoreWebView2HostResourceAccessKind.DenyCors);
            view.Source = new Uri("https://app.pinboard/index.html");
        }
        catch (Exception ex)
        {
            CanvasHost.Content = null;
            CanvasView?.Dispose();
            CanvasView = null;
            SetStatus(L("StatusCanvasInitFailed"));
            MessageBox.Show(this, L("CanvasStartFailedFormat", ex.Message), "Pinboard", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _clipboardCapture.Attach(handle);
    }

    private async void Core_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsTrustedCanvasUri(e.Source))
        {
            return;
        }
        try
        {
            var message = JsonSerializer.Deserialize<BridgeMessage>(e.WebMessageAsJson);
            if (message is null || message.Version != 1)
            {
                return;
            }

            switch (message.Type)
            {
                case "Ready":
                    await _captureGate.WaitAsync();
                    try
                    {
                        _webReady = true;
                        if (_activeDocument is not null) await SendOpenDocumentAsync(_activeDocument);
                        else
                        {
                            PostToCanvas("SetLanguage", new { language = LocalizationService.CurrentLanguage });
                            PostToCanvas("CloseDocument", new { });
                        }
                    }
                    finally { _captureGate.Release(); }
                    break;
                case "SaveStatus":
                    if (message.DocumentId == _activeDocument?.FilePath
                        && message.Payload.TryGetProperty("status", out var status))
                    {
                        HeaderSaveStatus.Text = L(status.GetString() switch
                        {
                            "saving" => "SaveStateSaving",
                            "dirty" => "SaveStateDirty",
                            "failed" or "conflict" => "SaveStateFailed",
                            _ => "SaveStateSaved"
                        });
                    }
                    break;
                case "SceneChanged":
                    await SaveSceneFromBridgeAsync(message);
                    break;
                case "FlushResult":
                    CompleteCanvasRequest(message, succeeded: true);
                    break;
                case "FlushFailed":
                    CompleteCanvasRequest(message, succeeded: false);
                    break;
                case "ExternalMutationReady":
                    CompleteCanvasRequest(message, succeeded: true);
                    break;
                case "ExternalMutationFailed":
                    CompleteCanvasRequest(message, succeeded: false);
                    break;
                case "DocumentRendered":
                    if (message.DocumentId == _activeDocument?.FilePath) _canvasLoading = false;
                    SetStatus(L("StatusReady"));
                    break;
                case "ExportResult":
                    CompleteCanvasRequest(message, succeeded: true);
                    break;
                case "ExportFailed":
                    CompleteCanvasRequest(message, succeeded: false);
                    break;
            }
        }
        catch (Exception ex)
        {
            SetStatus(L("StatusCanvasMessageFailedFormat", ex.Message));
        }
    }

    private async Task SaveSceneFromBridgeAsync(BridgeMessage message)
    {
        var documentPath = message.DocumentId ?? _activeDocument?.FilePath;
        var saveId = message.Payload.TryGetProperty("saveId", out var saveIdElement)
            ? saveIdElement.GetString()
            : message.RequestId;
        if (string.IsNullOrWhiteSpace(documentPath) || !_documents.TryGetValue(documentPath, out var document))
        {
            PostToCanvas(
                "SaveRejected",
                new { saveId, reason = "document-not-open", error = L("CanvasOperationFailed") },
                documentPath,
                message.RequestId);
            return;
        }

        var sceneJson = string.Empty;
        var assets = new List<(string FileId, AssetRecord Asset)>();
        var ocrJobs = new List<(AssetRecord Asset, byte[] Original, double X, double Y)>();
        try
        {
            sceneJson = message.Payload.GetProperty("sceneJson").GetString()
                ?? throw new InvalidDataException(L("CanvasMissingScene"));
            var referencedFileIds = CollectReferencedFileIds(sceneJson);
            if (message.Payload.TryGetProperty("newFiles", out var newFiles))
            {
                foreach (var file in newFiles.EnumerateArray())
                {
                    var fileId = file.GetProperty("id").GetString();
                    var dataUrl = file.GetProperty("dataUrl").GetString();
                    if (string.IsNullOrWhiteSpace(fileId) || string.IsNullOrWhiteSpace(dataUrl))
                    {
                        throw new InvalidDataException("The canvas returned an incomplete image payload.");
                    }
                    if (!referencedFileIds.Contains(fileId))
                    {
                        throw new InvalidDataException("The canvas returned an image that is not referenced by this scene.");
                    }
                    var decoded = ImageProcessor.DecodeDataUrl(dataUrl);
                    var asset = await Task.Run(() => _imageProcessor.Process(decoded.Bytes, _settings.WebpQuality));
                    assets.Add((fileId, asset));
                    var location = FindImageLocation(sceneJson, fileId);
                    ocrJobs.Add((asset, decoded.Bytes, location.X, location.Y));
                }
            }

            var revision = message.Payload.TryGetProperty("baseRevision", out var baseRevisionElement)
                && baseRevisionElement.TryGetInt64(out var baseRevision)
                ? await document.SaveSceneAsync(sceneJson, assets, expectedRevision: baseRevision)
                : await document.SaveSceneAsync(sceneJson, assets);
            PostToCanvas(
                "SaveAck",
                new
                {
                    saveId,
                    revision,
                    acceptedFileIds = assets.Select(item => item.FileId).ToArray()
                },
                documentPath,
                message.RequestId);
            SetStatus(L("StatusAutosavedFormat", DateTime.Now));

            foreach (var job in ocrJobs)
            {
                _ = RunOcrAsync(document, job.Asset, job.Original, job.X, job.Y);
            }
        }
        catch (RevisionConflictException ex)
        {
            var recovery = await SaveSceneRecoveryAsync(document, sceneJson, assets, "revision-conflict");
            if (recovery is not null)
            {
                SetStatus(L("StatusRecoverySavedFormat", recovery));
            }
            PostToCanvas(
                "SaveRejected",
                new
                {
                    saveId,
                    reason = "revision-conflict",
                    revision = ex.ActualRevision,
                    error = ex.Message
                },
                documentPath,
                message.RequestId);
            if (_activeDocument?.FilePath.Equals(documentPath, StringComparison.OrdinalIgnoreCase) == true)
            {
                await SendOpenDocumentAsync(document);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("canvas-save", ex);
            if (!string.IsNullOrWhiteSpace(sceneJson))
            {
                var recovery = await SaveSceneRecoveryAsync(document, sceneJson, assets, "save-failed");
                if (recovery is not null)
                {
                    SetStatus(L("StatusRecoverySavedFormat", recovery));
                }
                else
                {
                    SetStatus(L("StatusSaveFailedFormat", ex.Message));
                }
            }

            PostToCanvas(
                "SaveRejected",
                new { saveId, reason = "save-failed", error = ex.Message },
                documentPath,
                message.RequestId);
        }
    }

    private static HashSet<string> CollectReferencedFileIds(string sceneJson)
    {
        using var scene = JsonDocument.Parse(sceneJson);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (!scene.RootElement.TryGetProperty("elements", out var elements)
            || elements.ValueKind != JsonValueKind.Array)
        {
            return ids;
        }

        foreach (var element in elements.EnumerateArray())
        {
            if (element.TryGetProperty("fileId", out var fileId)
                && fileId.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(fileId.GetString()))
            {
                ids.Add(fileId.GetString()!);
            }
        }
        return ids;
    }

    private static async Task<string?> SaveSceneRecoveryAsync(
        PinboardDocument sourceDocument,
        string sceneJson,
        IReadOnlyList<(string FileId, AssetRecord Asset)> assets,
        string reason)
    {
        var recovery = Path.Combine(
            AppSettings.RecoveryDirectory,
            $"scene-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.pinboard");
        try
        {
            await sourceDocument.SaveCopyAsync(recovery);
            var recoveryDocument = new PinboardDocument(recovery);
            await recoveryDocument.InitializeAsync();
            await recoveryDocument.SaveSceneAsync(sceneJson, assets, checkpoint: true, checkpointReason: reason);
            return recovery;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("canvas-save-recovery", ex);
            return null;
        }
    }

    private async Task RunOcrAsync(PinboardDocument document, AssetRecord asset, byte[] originalBytes, double x, double y)
    {
        await _ocrGate.WaitAsync();
        try
        {
            var results = await _ocrService.RecognizeAsync(originalBytes, _settings.OcrChinese, _settings.OcrEnglish);
            foreach (var result in results)
            {
                await document.SaveOcrAsync(asset.Hash, result.Language, result.Text, result.Status, result.Error, x, y);
            }
            await Dispatcher.InvokeAsync(() => SetStatus(results.Any(result => result.Status == "complete")
                ? L("StatusOcrComplete")
                : L("StatusOcrUnavailable")));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("ocr", ex);
            await Dispatcher.InvokeAsync(() => SetStatus(L("StatusOcrUnavailable")));
        }
        finally
        {
            _ocrGate.Release();
        }
    }

    private async Task<PinboardDocument> OpenDocumentAsync(string path, bool activate, bool refreshCanvas = true, bool captureGateHeld = false)
    {
        if (!captureGateHeld)
        {
            await _captureGate.WaitAsync();
            try { return await OpenDocumentAsync(path, activate, refreshCanvas, captureGateHeld: true); }
            finally { _captureGate.Release(); }
        }
        path = Path.GetFullPath(path);
        if (!_documents.TryGetValue(path, out var document))
        {
            document = new PinboardDocument(path);
            await document.InitializeAsync();
            _documents[path] = document;
        }

        if (_settings.RecentFiles.FirstOrDefault() != path)
        {
            _settings.Remember(path);
            _settings.Save();
        }
        if (activate)
        {
            _activeDocument = document;
            HeaderDocumentTitle.Text = document.Title;
            Title = $"{document.Title} - Pinboard";
        }

        RefreshSidebarDocuments();
        if (activate && refreshCanvas)
        {
            if (_webReady)
            {
                await SendOpenDocumentAsync(document);
            }
        }
        UpdateCaptureTargetText();
        return document;
    }

    private async Task SendOpenDocumentAsync(PinboardDocument document)
    {
        var snapshot = await document.LoadSnapshotAsync();
        if (!_webReady || CanvasView?.CoreWebView2 is null || _activeDocument != document) return;
        var payload = new
        {
            version = 1,
            type = "OpenDocument",
            documentId = document.FilePath,
            payload = new
            {
                path = document.FilePath,
                title = document.Title,
                sceneJson = snapshot.SceneJson,
                language = LocalizationService.CurrentLanguage,
                files = snapshot.Files.Select(file => new
                {
                    fileId = file.FileId,
                    mimeType = file.MimeType,
                    dataUrl = file.DataUrl,
                    createdAt = file.CreatedAt
                }),
                revision = snapshot.Revision
                , viewport = _sleepDocumentPath == document.FilePath ? _sleepViewport : null
            }
        };
        _canvasLoading = true;
        CanvasView?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
        _sleepViewport = null;
        _sleepDocumentPath = null;
        SetStatus(L("StatusOpenedFormat", document.Title));
    }

    private async Task<bool> BeginExternalMutationIfActiveAsync(string targetPath)
    {
        if (!_webReady
            || _activeDocument is null
            || !_activeDocument.FilePath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (_canvasLoading && DateTime.UtcNow < deadline) await Task.Delay(50);
        if (_canvasLoading) throw new TimeoutException(L("CanvasRequestTimeout"));
        await RequestCanvasAsync("BeginExternalMutation", new { reason = "capture" }, targetPath, TimeSpan.FromSeconds(30));
        return true;
    }

    private void BeginPixPinCapture()
    {
        if (_settings.CapturePaused)
        {
            SetStatus(L("StatusPixPinPaused"));
            return;
        }
        if (_clipboardCapture.IsArmed)
        {
            SetStatus(L("StatusWaitingPreviousCapture"));
            return;
        }

        try
        {
            _clipboardCapture.Arm(TimeSpan.FromSeconds(120));
            _pixPin.StartCapture();
            SetStatus(L("StatusWaitingPixPin"));
        }
        catch (Exception ex)
        {
            _clipboardCapture.Disarm();
            SetStatus(ex.Message);
        }
    }

    private void ArmCaptureOnly()
    {
        ArmCaptureOnly(L("StatusWaitingClipboard"));
    }

    private void ArmCaptureOnly(string waitingStatus)
    {
        if (_settings.CapturePaused)
        {
            return;
        }
        _clipboardCapture.Arm(TimeSpan.FromSeconds(120));
        SetStatus(waitingStatus);
    }

    private void ArmCaptureForDefaultPixPinShortcut()
    {
        if (_settings.CapturePaused)
        {
            return;
        }

        ArmCaptureOnly(L("StatusWaitingDefaultPixPinClipboard"));
    }

    private async void ClipboardCapture_ImageCaptured(object? sender, byte[] bytes)
    {
        if (_exiting) return;
        await _captureGate.WaitAsync();
        AssetRecord? processedAsset = null;
        string? targetPath = null;
        bool externalMutationStarted = false;
        bool canvasReloaded = false;
        try
        {
            SetStatus(L("StatusProcessingCapture"));
            var asset = await Task.Run(() => _imageProcessor.Process(bytes, _settings.WebpQuality));
            processedAsset = asset;
            targetPath = Path.GetFullPath(_settings.ResolveCaptureTarget(DateTimeOffset.Now));
            externalMutationStarted = await BeginExternalMutationIfActiveAsync(targetPath);
            var document = await OpenDocumentAsync(
                targetPath,
                activate: _activeDocument is null || _activeDocument.FilePath.Equals(targetPath, StringComparison.OrdinalIgnoreCase),
                refreshCanvas: false, captureGateHeld: true);
            var result = await document.InsertCapturedImageAsync(asset, "pixpin");
            _lastInsertedElements[targetPath] = result.ElementId;
            _ = RunOcrAsync(document, asset, bytes, result.X, result.Y);

            if (_activeDocument?.FilePath.Equals(targetPath, StringComparison.OrdinalIgnoreCase) == true && _webReady)
            {
                await SendOpenDocumentAsync(document);
                canvasReloaded = true;
            }
            ShowNotification(L("CaptureAddedTitleFormat", document.Title), L("CaptureAddedBody"));
            SetStatus(L("StatusCaptureAddedFormat", document.Title));
        }
        catch (Exception ex)
        {
            if (processedAsset is not null)
            {
                try
                {
                    var recovery = Path.Combine(AppSettings.RecoveryDirectory, $"capture-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.pinboard");
                    var recoveryDocument = new PinboardDocument(recovery);
                    await recoveryDocument.InitializeAsync();
                    await recoveryDocument.InsertCapturedImageAsync(processedAsset, "pixpin-recovery");
                    SetStatus(L("StatusCaptureRecoveryFormat", recovery));
                    ShowNotification(L("CaptureRecoveredTitle"), L("CaptureRecoveredBodyFormat", Path.GetFileName(recovery)));
                    return;
                }
                catch
                {
                    // Fall through to the original error when recovery storage also fails.
                }
            }
            SetStatus(L("StatusCaptureFailedFormat", ex.Message));
            ShowNotification(L("CaptureFailedTitle"), ex.Message);
        }
        finally
        {
            if (externalMutationStarted && !canvasReloaded && targetPath is not null)
            {
                await ReloadActiveDocumentAfterExternalMutationFailureAsync(targetPath);
            }
            _captureGate.Release();
        }
    }

    private async void ClipboardCapture_TextCaptured(object? sender, string text)
    {
        if (_exiting || !_settings.TextCaptureEnabled || _settings.TextCapturePaused || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await _captureGate.WaitAsync();
        string? targetPath = null;
        bool externalMutationStarted = false;
        bool canvasReloaded = false;
        try
        {
            SetStatus(L("StatusSavingTextClip"));
            targetPath = Path.GetFullPath(_settings.ResolveTextCaptureTarget());
            externalMutationStarted = await BeginExternalMutationIfActiveAsync(targetPath);
            var document = await OpenDocumentAsync(targetPath,
                activate: _activeDocument is null || _activeDocument.FilePath.Equals(targetPath, StringComparison.OrdinalIgnoreCase),
                refreshCanvas: false, captureGateHeld: true);
            var result = await document.InsertCapturedTextAsync(text, "clipboard");
            _lastInsertedElements[targetPath] = result.ElementId;

            if (_activeDocument?.FilePath.Equals(targetPath, StringComparison.OrdinalIgnoreCase) == true && _webReady)
            {
                await SendOpenDocumentAsync(document);
                canvasReloaded = true;
            }

            SetStatus(L("StatusTextClipAddedFormat", document.Title));
        }
        catch (Exception ex)
        {
            try
            {
                var recovery = Path.Combine(AppSettings.RecoveryDirectory, $"text-clip-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.pinboard");
                var recoveryDocument = new PinboardDocument(recovery);
                await recoveryDocument.InitializeAsync();
                await recoveryDocument.InsertCapturedTextAsync(text, "clipboard-recovery");
                SetStatus(L("StatusTextClipRecoveryFormat", recovery));
                ShowNotification(L("TextClipRecoveredTitle"), L("TextClipRecoveredBodyFormat", Path.GetFileName(recovery)));
                return;
            }
            catch
            {
                // The original exception below is more useful when recovery storage also fails.
            }

            SetStatus(L("StatusTextClipFailedFormat", ex.Message));
            ShowNotification(L("TextClipFailedTitle"), ex.Message);
        }
        finally
        {
            if (externalMutationStarted && !canvasReloaded && targetPath is not null)
            {
                await ReloadActiveDocumentAfterExternalMutationFailureAsync(targetPath);
            }
            _captureGate.Release();
        }
    }

    private async Task ReloadActiveDocumentAfterExternalMutationFailureAsync(string targetPath)
    {
        if (!_webReady
            || _activeDocument is null
            || !_activeDocument.FilePath.Equals(targetPath, StringComparison.OrdinalIgnoreCase)
            || !_documents.TryGetValue(targetPath, out var document))
        {
            return;
        }

        try
        {
            await SendOpenDocumentAsync(document);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("external-mutation-reload", ex);
        }
    }

    private async Task ImportObsidianAsync(string sourcePath)
    {
        SetStatus(L("StatusImportingFormat", Path.GetFileName(sourcePath)));
        var destinationName = Path.GetFileName(sourcePath).EndsWith(".excalidraw.md", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(sourcePath)[..^".excalidraw.md".Length] + ".pinboard"
            : Path.GetFileNameWithoutExtension(sourcePath) + ".pinboard";
        var destination = Path.Combine(_settings.LibraryPath, destinationName);
        var report = sourcePath.EndsWith(".excalidraw.md", StringComparison.OrdinalIgnoreCase)
            ? await _obsidianImporter.ImportAsync(sourcePath, destination, _settings.WebpQuality, _settings.OcrChinese, _settings.OcrEnglish)
            : await _obsidianImporter.ImportStandardAsync(sourcePath, destination, _settings.WebpQuality, _settings.OcrChinese, _settings.OcrEnglish);
        await OpenDocumentAsync(report.DestinationPath, activate: true);
        SetStatus(L("StatusImportCompleteFormat", report.ImageCount, report.PlaceholderCount));
    }

    private async void NewButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowNewBoardDialogAsync();
    }

    private async void NewTextBoardButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowNewTextBoardDialogAsync();
    }

    private async void NewBoardForProjectButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is System.Windows.Controls.Button { CommandParameter: string projectName })
        {
            if (projectName.Equals(AppSettings.TextClipsDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = (System.Windows.Controls.Button)sender };
                var board = new System.Windows.Controls.MenuItem { Header = L("TextNewBoardHeading") };
                board.Click += async (_, _) => await ShowNewTextBoardDialogAsync();
                var folder = new System.Windows.Controls.MenuItem { Header = L("TextNewFolder") };
                folder.Click += NewTextFolderButton_Click;
                menu.Items.Add(board);
                menu.Items.Add(folder);
                menu.IsOpen = true;
            }
            else if (projectName.StartsWith(AppSettings.TextClipsDirectoryName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                await ShowNewTextBoardDialogAsync(projectName[(AppSettings.TextClipsDirectoryName.Length + 1)..]);
            }
            else
            {
                await ShowNewBoardDialogAsync(projectName);
            }
        }
    }

    private void NewProjectButton_Click(object sender, RoutedEventArgs e)
    {
        ShowNewProjectDialog();
    }

    private string? ShowNewProjectDialog()
    {
        var window = new NewProjectWindow(_projectLibrary) { Owner = this };
        if (window.ShowDialog() != true || window.ResultProjectName is not { } projectName)
        {
            return null;
        }
        RefreshSidebarDocuments();
        SetStatus(L("StatusProjectCreatedFormat", projectName));
        return projectName;
    }

    private async Task ShowNewBoardDialogAsync(string? suggestedProject = null)
    {
        suggestedProject ??= _activeDocument is null
            ? ProjectLibraryService.DefaultProjectName
            : _projectLibrary.GetProjectName(_activeDocument.FilePath);
        var window = new NewBoardWindow(_projectLibrary, suggestedProject)
        {
            Owner = this
        };
        if (window.ShowDialog() == true && window.ResultPath is not null)
        {
            try
            {
                await OpenDocumentAsync(window.ResultPath, activate: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, L("MessageCreateBoardFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus(L("StatusCreateBoardFailed"));
            }
        }
    }

    private async Task ShowNewTextBoardDialogAsync(string? suggestedFolder = null)
    {
        var window = new NewTextBoardWindow(_settings.LibraryPath, suggestedFolder) { Owner = this };
        if (window.ShowDialog() == true && window.ResultPath is not null)
        {
            try
            {
                await OpenDocumentAsync(window.ResultPath, activate: true);
                RefreshSidebarDocuments();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, L("MessageCreateBoardFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void NewTextFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var prompt = new TextEntryDialog(L("TextFolderTitle"), L("TextFolderPrompt")) { Owner = this };
        if (prompt.ShowDialog() != true) return;
        var name = prompt.Value;
        try
        {
            var folder = TextClipsLibrary.CreateFolder(_settings.LibraryPath, name);
            RefreshSidebarDocuments();
            SetStatus(L("TextFolderCreatedFormat", folder));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, L("MessageCreateBoardFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BoardActionsButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is System.Windows.Controls.Button { CommandParameter: SidebarDocument item } button && !item.IsPlaceholder)
        {
            ShowBoardActionsMenu(button, item);
        }
    }

    private void BoardItem_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: SidebarDocument item } element && !item.IsPlaceholder)
        {
            ShowBoardActionsMenu(element, item);
        }
    }

    private void ShowBoardActionsMenu(FrameworkElement placementTarget, SidebarDocument item)
    {
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = placementTarget };
        menu.Items.Add(new System.Windows.Controls.MenuItem
        {
            Header = L("BoardActualPathFormat", EscapeAccessKey(item.Path)),
            IsEnabled = false
        });
        menu.Items.Add(new System.Windows.Controls.Separator());

        if (item.IsTextBoard)
        {
            if (item.IsCaptureTarget)
            {
                var automaticItem = new System.Windows.Controls.MenuItem { Header = L("BoardMenuUseDailyTextTarget") };
                automaticItem.Click += (_, _) => UseAutomaticTextCaptureTarget();
                menu.Items.Add(automaticItem);
            }
            else
            {
                var fixedItem = new System.Windows.Controls.MenuItem { Header = L("BoardMenuSetTextCaptureTarget") };
                fixedItem.Click += (_, _) => SetFixedTextCaptureTarget(item);
                menu.Items.Add(fixedItem);
            }

            if (item.CanMove)
            {
                menu.Items.Add(new System.Windows.Controls.Separator());
                var renameItem = new System.Windows.Controls.MenuItem { Header = L("BoardMenuRename") };
                renameItem.Click += async (_, _) => await RenameBoardAsync(item);
                menu.Items.Add(renameItem);
                var moveItem = new System.Windows.Controls.MenuItem { Header = L("TextBoardMove") };
                moveItem.Click += async (_, _) => await MoveTextBoardAsync(item);
                menu.Items.Add(moveItem);
            }

            menu.Items.Add(new System.Windows.Controls.Separator());
        }
        else
        {
            if (item.IsCaptureTarget)
            {
                var monthlyTargetItem = new System.Windows.Controls.MenuItem { Header = L("BoardMenuUseMonthlyTarget") };
                monthlyTargetItem.Click += (_, _) => UseMonthlyCaptureTarget();
                menu.Items.Add(monthlyTargetItem);
            }
            else
            {
                var fixedTargetItem = new System.Windows.Controls.MenuItem { Header = L("BoardMenuSetCaptureTarget") };
                fixedTargetItem.Click += (_, _) => SetFixedCaptureTarget(item);
                menu.Items.Add(fixedTargetItem);
            }
            menu.Items.Add(new System.Windows.Controls.Separator());

            var renameItem = new System.Windows.Controls.MenuItem { Header = L("BoardMenuRename") };
            renameItem.Click += async (_, _) => await RenameBoardAsync(item);
            menu.Items.Add(renameItem);

            if (item.IsArchived)
            {
                var restoreItem = new System.Windows.Controls.MenuItem { Header = L("BoardMenuRestore") };
                restoreItem.Click += async (_, _) => await PromptMoveBoardAsync(item);
                menu.Items.Add(restoreItem);
            }
            else if (item.CanMove)
            {
                var moveItem = new System.Windows.Controls.MenuItem { Header = L("BoardMenuMove") };
                moveItem.Click += async (_, _) => await PromptMoveBoardAsync(item);
                menu.Items.Add(moveItem);

                var archiveItem = new System.Windows.Controls.MenuItem { Header = L("BoardMenuArchive") };
                archiveItem.Click += async (_, _) => await ArchiveBoardAsync(item);
                menu.Items.Add(archiveItem);
            }

            menu.Items.Add(new System.Windows.Controls.Separator());
        }
        var showItem = new System.Windows.Controls.MenuItem { Header = L("BoardMenuShowInExplorer") };
        showItem.Click += (_, _) => ShowFileInExplorer(item.Path);
        menu.Items.Add(showItem);

        var copyItem = new System.Windows.Controls.MenuItem { Header = L("BoardMenuCopyPath") };
        copyItem.Click += (_, _) =>
        {
            ClipboardCaptureService.SetTextWithoutCapture(item.Path);
            SetStatus(L("StatusPathCopied"));
        };
        menu.Items.Add(copyItem);

        menu.Items.Add(new System.Windows.Controls.Separator());
        var deleteItem = new System.Windows.Controls.MenuItem { Header = L("BoardMenuDelete") };
        deleteItem.Click += async (_, _) => await DeleteBoardAsync(item);
        menu.Items.Add(deleteItem);
        placementTarget.ContextMenu = menu;
        menu.Closed += (_, _) => placementTarget.ClearValue(FrameworkElement.ContextMenuProperty);
        menu.IsOpen = true;
    }

    private void SetFixedCaptureTarget(SidebarDocument item)
    {
        _settings.FixedCaptureTarget = Path.GetFullPath(item.Path);
        _settings.Save();
        RefreshSidebarDocuments();
        UpdateCaptureTargetText();
        SetStatus(L("StatusCaptureTargetSetFormat", item.Title));
    }

    private void SetFixedTextCaptureTarget(SidebarDocument item)
    {
        _settings.FixedTextCaptureTarget = Path.GetFullPath(item.Path);
        _settings.Save();
        RefreshSidebarDocuments();
        UpdateCaptureTargetText();
        SetStatus(L("StatusTextCaptureTargetSetFormat", item.Title));
    }

    private void UseAutomaticTextCaptureTarget()
    {
        _settings.FixedTextCaptureTarget = null;
        _settings.TextCaptureBoardMode = TextCaptureBoardMode.Daily;
        _settings.Save();
        RefreshSidebarDocuments();
        UpdateCaptureTargetText();
        SetStatus(L("StatusTextCaptureTargetAutoFormat", Path.GetFileNameWithoutExtension(_settings.ResolveTextCaptureTarget(DateTimeOffset.Now))));
    }

    private async Task MoveTextBoardAsync(SidebarDocument item)
    {
        var prompt = new TextEntryDialog(L("TextBoardMove"), L("TextBoardMovePrompt")) { Owner = this };
        if (prompt.ShowDialog() != true) return;
        await _captureGate.WaitAsync();
        try
        {
            var wasActive = _activeDocument?.FilePath.Equals(item.Path, StringComparison.OrdinalIgnoreCase) == true;
            if (wasActive && _webReady)
            {
                var flush = await RequestCanvasAsync("Flush", new { }, item.Path, TimeSpan.FromSeconds(30));
                if (flush.TryGetProperty("skipped", out var skipped) && skipped.ValueKind == JsonValueKind.True)
                    throw new InvalidOperationException(L("CanvasNotReady"));
            }
            var destination = TextClipsLibrary.MoveManualBoard(_settings.LibraryPath, item.Path, prompt.Value);
            _documents.Remove(item.Path);
            _settings.RecentFiles.RemoveAll(path => path.Equals(item.Path, StringComparison.OrdinalIgnoreCase));
            if (_lastInsertedElements.Remove(item.Path, out var lastElement)) _lastInsertedElements[destination] = lastElement;
            if (_settings.FixedTextCaptureTarget?.Equals(item.Path, StringComparison.OrdinalIgnoreCase) == true)
                _settings.FixedTextCaptureTarget = destination;
            _settings.Save();
            if (wasActive) await OpenDocumentAsync(destination, activate: true, captureGateHeld: true);
            RefreshSidebarDocuments();
            UpdateCaptureTargetText();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, L("MessageMoveBoardFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _captureGate.Release(); }
    }

    private void UseMonthlyCaptureTarget()
    {
        _settings.FixedCaptureTarget = null;
        _settings.Save();
        var target = GetCaptureTargetTitle();
        RefreshSidebarDocuments();
        UpdateCaptureTargetText();
        SetStatus(L("StatusCaptureTargetAutoFormat", target));
    }

    private async Task RenameBoardAsync(SidebarDocument item)
    {
        var window = new RenameBoardWindow(item.Title, item.Path) { Owner = this };
        if (window.ShowDialog() != true || window.ResultDisplayName is not { } displayName)
        {
            return;
        }

        try
        {
            if (!_documents.TryGetValue(item.Path, out var document))
            {
                document = new PinboardDocument(item.Path);
                await document.InitializeAsync();
                _documents[item.Path] = document;
            }
            await document.RenameDisplayTitleAsync(displayName);
            if (_activeDocument?.FilePath.Equals(item.Path, StringComparison.OrdinalIgnoreCase) == true)
            {
                HeaderDocumentTitle.Text = document.Title;
                Title = $"{document.Title} - Pinboard";
                PostToCanvas("SetDocumentTitle", new { title = document.Title }, document.FilePath);
            }
            RefreshSidebarDocuments();
            SetStatus(L("StatusRenameSuccessFormat", displayName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, L("MessageRenameFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void ShowFileInExplorer(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true
        });
    }

    private static string EscapeAccessKey(string value) => value.Replace("_", "__", StringComparison.Ordinal);

    private async Task PromptMoveBoardAsync(SidebarDocument item)
    {
        if (!item.CanMove)
        {
            return;
        }

        var destinations = _projectLibrary.GetProjectNames()
            .Where(project => !project.Equals(item.ProjectName, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        if (destinations.Count == 0)
        {
            var newProject = ShowNewProjectDialog();
            if (newProject is null)
            {
                return;
            }
            destinations.Add(newProject);
        }

        var window = new MoveBoardWindow(item.Title, item.ProjectName, destinations) { Owner = this };
        if (window.ShowDialog() != true || window.ResultProjectName is not { } destinationProject)
        {
            return;
        }

        try
        {
            await MoveBoardAsync(item, destinationProject);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, L("MessageMoveBoardFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus(L("StatusMoveBoardFailed"));
        }
    }

    private async Task ArchiveBoardAsync(SidebarDocument item)
    {
        if (!item.CanMove || item.IsArchived)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            L("ArchiveConfirmMessageFormat", item.Title),
            L("ArchiveConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await MoveBoardAsync(item, ProjectLibraryService.ArchiveProjectName, archive: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, L("MessageMoveBoardFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus(L("StatusMoveBoardFailed"));
        }
    }

    private async Task MoveBoardAsync(SidebarDocument item, string destinationProject, bool archive = false)
    {
        var wasActive = _activeDocument?.FilePath.Equals(item.Path, StringComparison.OrdinalIgnoreCase) == true;
        if (wasActive && _webReady)
        {
            SetStatus(L("StatusSavingAndMoving"));
            await RequestCanvasAsync("Flush", new { }, item.Path, TimeSpan.FromSeconds(15));
        }

        var destinationPath = archive
            ? _projectLibrary.ArchiveBoard(item.Path)
            : _projectLibrary.MoveBoard(item.Path, destinationProject);
        _documents.Remove(item.Path);
        var movedDocument = new PinboardDocument(destinationPath);
        await movedDocument.InitializeAsync();
        _documents[destinationPath] = movedDocument;

        if (_lastInsertedElements.Remove(item.Path, out var lastElement))
        {
            _lastInsertedElements[destinationPath] = lastElement;
        }
        _settings.RecentFiles.RemoveAll(path => path.Equals(item.Path, StringComparison.OrdinalIgnoreCase));
        _settings.Remember(destinationPath);
        if (_settings.FixedCaptureTarget?.Equals(item.Path, StringComparison.OrdinalIgnoreCase) == true)
        {
            _settings.FixedCaptureTarget = destinationPath;
        }
        _settings.Save();

        if (wasActive)
        {
            _activeDocument = movedDocument;
            HeaderDocumentTitle.Text = movedDocument.Title;
            Title = $"{movedDocument.Title} - Pinboard";
            if (_webReady)
            {
                await SendOpenDocumentAsync(movedDocument);
            }
        }
        RefreshSidebarDocuments();
        UpdateCaptureTargetText();
        SetStatus(archive
            ? L("StatusArchivedFormat", item.Title)
            : L("StatusMovedFormat", LocalizationService.ProjectDisplayName(destinationProject)));
    }

    private async Task DeleteBoardAsync(SidebarDocument item)
    {
        var confirmation = MessageBox.Show(
            this,
            L("DeleteConfirmMessageFormat", item.Title),
            L("DeleteConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        var wasActive = _activeDocument?.FilePath.Equals(item.Path, StringComparison.OrdinalIgnoreCase) == true;
        try
        {
            if (wasActive && _webReady)
            {
                SetStatus(L("StatusSavingAndMoving"));
                await RequestCanvasAsync("Flush", new { }, item.Path, TimeSpan.FromSeconds(15));
            }

            if (!File.Exists(item.Path))
            {
                throw new FileNotFoundException(L("BoardMoveSourceMissing"), item.Path);
            }

            FileSystem.DeleteFile(item.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            _documents.Remove(item.Path);
            _lastInsertedElements.Remove(item.Path);
            _settings.RecentFiles.RemoveAll(path => path.Equals(item.Path, StringComparison.OrdinalIgnoreCase));
            if (_settings.FixedCaptureTarget?.Equals(item.Path, StringComparison.OrdinalIgnoreCase) == true)
            {
                _settings.FixedCaptureTarget = null;
            }
            if (_settings.FixedTextCaptureTarget?.Equals(item.Path, StringComparison.OrdinalIgnoreCase) == true)
            {
                _settings.FixedTextCaptureTarget = null;
            }
            _settings.Save();

            if (wasActive)
            {
                _activeDocument = null;
                HeaderDocumentTitle.Text = L("MainNoBoardOpen");
                Title = "Pinboard";
                PostToCanvas("CloseDocument", new { }, item.Path);
            }

            RefreshSidebarDocuments();
            UpdateCaptureTargetText();
            SetStatus(L("StatusDeletedFormat", item.Title));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, L("MessageDeleteBoardFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus(L("StatusDeleteBoardFailed"));
        }
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = L("FilterPinboard"),
            InitialDirectory = _settings.LibraryPath,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            await OpenDocumentAsync(dialog.FileName, activate: true);
        }
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = L("FilterImport"),
            InitialDirectory = _settings.LastImportDirectory
                ?? (Directory.Exists(_settings.LibraryPath)
                    ? _settings.LibraryPath
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            _settings.LastImportDirectory = Path.GetDirectoryName(dialog.FileName);
            _settings.Save();
            await ImportObsidianAsync(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, L("MessageImportFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus(L("StatusImportFailed"));
        }
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowSearchAsync(allDocuments: true);
    }

    private async Task ShowSearchAsync(bool allDocuments)
    {
        var documentPath = allDocuments ? null : _activeDocument?.FilePath;
        if (!allDocuments && documentPath is null)
        {
            return;
        }
        var window = new SearchWindow(_settings.LibraryPath, _searchService, documentPath) { Owner = this };
        if (window.ShowDialog() != true || window.SelectedHit is not { } hit)
        {
            return;
        }
        var document = await OpenDocumentAsync(hit.DocumentPath, activate: true);
        var elementId = hit.Kind == "text" ? hit.ReferenceId : await ResolveOcrElementIdAsync(document, hit.ReferenceId);
        PostToCanvas("FocusElement", new { elementId, x = hit.X, y = hit.Y }, document.FilePath);
    }

    private void CaptureButton_Click(object sender, RoutedEventArgs e) => BeginPixPinCapture();

    private void GoToBottomButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeDocument is not null)
        {
            PostToCanvas("FocusBottom", new { }, _activeDocument.FilePath);
        }
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement target)
        {
            return;
        }
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = target };
        void Add(string text, RoutedEventHandler handler)
        {
            var item = new System.Windows.Controls.MenuItem { Header = text };
            item.Click += handler;
            menu.Items.Add(item);
        }
        Add(L("MainNewBoardButton"), NewButton_Click);
        Add(L("TextNewBoardHeading"), NewTextBoardButton_Click);
        Add(L("TextNewFolder"), NewTextFolderButton_Click);
        Add(L("MainOpenButton"), OpenButton_Click);
        Add(L("MainImportButton"), ImportButton_Click);
        menu.Items.Add(new System.Windows.Controls.Separator());
        Add(L("MainExportButton"), ExportButton_Click);
        Add(L("MainSaveCopyButton"), SaveCopyButton_Click);
        Add(L("MainLocateLatest"), LocateButton_Click);
        Add(L("MainResetInboxButton"), ResetInboxButton_Click);
        menu.Items.Add(new System.Windows.Controls.Separator());
        Add(_settings.AlwaysOnTop ? L("MainTopmostOn") : L("MainTopmostOff"), AlwaysOnTopButton_Click);
        Add(L("LanguageSwitchLabel"), LanguageButton_Click);
        Add(L("MainSettingsButton"), SettingsButton_Click);
        target.ContextMenu = menu;
        menu.Closed += (_, _) => target.ClearValue(FrameworkElement.ContextMenuProperty);
        menu.IsOpen = true;
    }

    private async void LocateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeDocument is null)
        {
            return;
        }
        if (_lastInsertedElements.TryGetValue(_activeDocument.FilePath, out var elementId))
        {
            PostToCanvas("FocusElement", new { elementId }, _activeDocument.FilePath);
        }
        else
        {
            var snapshot = await _activeDocument.LoadSnapshotAsync(includeFiles: false);
            var last = FindLastInboxElement(snapshot.SceneJson);
            if (last is not null)
            {
                PostToCanvas("FocusElement", new { elementId = last }, _activeDocument.FilePath);
            }
            else
            {
                SetStatus(L("StatusNoInbox"));
            }
        }
    }

    private async void SaveCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeDocument is null)
        {
            return;
        }
        var dialog = new SaveFileDialog
        {
            Filter = L("FilterPinboard"),
            InitialDirectory = Path.GetDirectoryName(_activeDocument.FilePath),
            FileName = $"{_activeDocument.Title}-{L("CopyFileSuffix")}-{DateTime.Now:yyyyMMdd}.pinboard"
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _activeDocument.SaveCopyAsync(dialog.FileName);
            RefreshSidebarDocuments();
            SetStatus(L("StatusCopySavedFormat", dialog.FileName));
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeDocument is null)
        {
            return;
        }
        var dialog = new SaveFileDialog
        {
            Filter = L("FilterExport"),
            InitialDirectory = Path.GetDirectoryName(_activeDocument.FilePath),
            FileName = _activeDocument.Title + ".png",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
        var kind = extension switch
        {
            ".svg" => "svg",
            ".excalidraw" => "excalidraw",
            _ => "png"
        };
        try
        {
            SetStatus(L("StatusExportingFormat", Path.GetFileName(dialog.FileName)));
            var result = await RequestCanvasAsync("Export", new { kind }, _activeDocument.FilePath, TimeSpan.FromSeconds(30));
            if (kind == "png")
            {
                var dataUrl = result.GetProperty("dataUrl").GetString()
                    ?? throw new InvalidDataException(L("CanvasMissingPng"));
                var decoded = ImageProcessor.DecodeDataUrl(dataUrl);
                await File.WriteAllBytesAsync(dialog.FileName, decoded.Bytes);
            }
            else
            {
                var text = result.GetProperty("text").GetString()
                    ?? throw new InvalidDataException(L("CanvasMissingExport"));
                await File.WriteAllTextAsync(dialog.FileName, text, Encoding.UTF8);
            }
            SetStatus(L("StatusExportedFormat", dialog.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, L("MessageExportFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus(L("StatusExportFailed"));
        }
    }

    private async void ResetInboxButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeDocument is null)
        {
            return;
        }
        await _activeDocument.ResetInboxAsync();
        SetStatus(L("StatusInboxReset"));
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_settings, _activeDocument?.FilePath, _pixPin) { Owner = this };
        if (window.ShowDialog() == true)
        {
            _clipboardCapture.ConfigureTextCapture(_settings);
            _projectLibrary = new ProjectLibraryService(_settings.LibraryPath);
            ConfigureLibraryWatcher();
            RefreshSidebarDocuments();
            UpdateCaptureTargetText();
            SetStatus(L("StatusSettingsSaved"));
        }
    }

    private async void SidebarList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_sidebarSelectionChanging || SidebarList.SelectedItem is not SidebarDocument item || item.IsPlaceholder)
        {
            return;
        }

        if (_activeDocument?.FilePath.Equals(item.Path, StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        try
        {
            await OpenDocumentAsync(item.Path, activate: true);
        }
        catch (Exception ex)
        {
            RefreshSidebarDocuments();
            MessageBox.Show(this, ex.Message, L("MessageOpenBoardFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SidebarToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.SidebarCollapsed = !_settings.SidebarCollapsed;
        ApplySidebarState();
        _settings.Save();
    }

    private void ApplySidebarState()
    {
        var collapsed = _settings.SidebarCollapsed;
        SidebarPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.Width = collapsed ? new GridLength(0) : new GridLength(248);
        SidebarGapColumn.Width = collapsed ? new GridLength(0) : new GridLength(1);
        SidebarToggleButton.ToolTip = collapsed ? L("MainExpandSidebarToolTip") : L("MainCollapseSidebarToolTip");
    }

    private void AlwaysOnTopButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
        ApplyAlwaysOnTopState();
        _settings.Save();
    }

    private void ApplyAlwaysOnTopState()
    {
        Topmost = _settings.AlwaysOnTop;
    }

    private void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.Language = LocalizationService.CurrentLanguage == LocalizationService.English
            ? LocalizationService.Chinese
            : LocalizationService.English;
        LocalizationService.Apply(_settings.Language);
        _settings.Save();
        ApplySidebarState();
        ApplyAlwaysOnTopState();
        RefreshSidebarDocuments();
        RefreshTrayMenuText();
        UpdateCaptureTargetText();
        PostToCanvas("SetLanguage", new { language = LocalizationService.CurrentLanguage }, _activeDocument?.FilePath);
        SetStatus(L("LanguageChanged"));
    }

    private void RefreshSidebarDocuments()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshSidebarDocuments);
            return;
        }

        if (_initialized && (!IsVisible || WindowState == WindowState.Minimized))
        {
            _sidebarDirty = true;
            return;
        }
        _sidebarDirty = false;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Directory.CreateDirectory(_settings.LibraryPath);
            foreach (var path in Directory.EnumerateFiles(_settings.LibraryPath, "*.pinboard", SearchOption.AllDirectories))
            {
                paths.Add(Path.GetFullPath(path));
            }
        }
        catch (Exception ex)
        {
            SetStatus(L("StatusReadBoardsFailedFormat", ex.Message));
        }

        foreach (var path in _settings.RecentFiles.Where(File.Exists))
        {
            paths.Add(Path.GetFullPath(path));
        }

        var libraryFiles = paths
            .Select(path => new FileInfo(path))
            .Select(file => new
            {
                File = file,
                TextKind = TextClipsLibrary.GetDocumentKind(_settings.LibraryPath, file.FullName),
                ScreenshotKind = ScreenshotBoardsLibrary.GetDocumentKind(_settings.LibraryPath, file.FullName)
            })
            .ToList();
        var textBoardItems = libraryFiles
            .Where(item => item.TextKind != TextClipsDocumentKind.None)
            .ToList();
        var screenshotBoardItems = libraryFiles
            .Where(item => item.ScreenshotKind != ScreenshotDocumentKind.None)
            .ToList();
        var boardItems = libraryFiles
            .Where(item => item.TextKind == TextClipsDocumentKind.None && item.ScreenshotKind == ScreenshotDocumentKind.None)
            .Select(item => new { item.File, Project = _projectLibrary.GetProjectName(item.File.FullName) })
            .ToList();

        var projectNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase)
        {
            ProjectLibraryService.DefaultProjectName
        };
        try
        {
            foreach (var project in _projectLibrary.GetProjectNames())
            {
                projectNames.Add(project);
            }
        }
        catch (Exception ex)
        {
            SetStatus(L("StatusReadProjectsFailedFormat", ex.Message));
        }
        foreach (var item in boardItems)
        {
            projectNames.Add(item.Project);
        }

        string? captureTarget = null;
        string? textCaptureTarget = null;
        try
        {
            captureTarget = Path.GetFullPath(_settings.ResolveCaptureTarget(DateTimeOffset.Now));
            textCaptureTarget = Path.GetFullPath(_settings.ResolveTextCaptureTarget(DateTimeOffset.Now));
        }
        catch
        {
            // A malformed old setting should not prevent the board list from loading.
        }

        var projectCounts = boardItems
            .GroupBy(item => item.Project, StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.CurrentCultureIgnoreCase);
        var regularItems = boardItems
            .Select(item => new SidebarDocument(
                item.File.FullName,
                ReadCachedTitle(item.File),
                FormatSidebarDetail(item.File),
                item.Project,
                GetSidebarProjectDisplayName(item.File, item.Project),
                projectCounts[item.Project],
                IsPlaceholder: false,
                CanMove: !item.Project.Equals(ProjectLibraryService.OtherLocationProjectName, StringComparison.CurrentCultureIgnoreCase),
                CanCreateBoard: !item.Project.Equals(ProjectLibraryService.OtherLocationProjectName, StringComparison.CurrentCultureIgnoreCase)
                    && !item.Project.Equals(ProjectLibraryService.ArchiveProjectName, StringComparison.CurrentCultureIgnoreCase),
                IsCaptureTarget: captureTarget is not null
                    && item.File.FullName.Equals(captureTarget, StringComparison.OrdinalIgnoreCase),
                Kind: SidebarDocumentKind.Board))
            .ToList();
        foreach (var project in projectNames.Where(project => !projectCounts.ContainsKey(project)))
        {
            regularItems.Add(new SidebarDocument(
                string.Empty,
                L("SidebarNoBoardPlaceholder"),
                L("SidebarNoBoardHelp"),
                project,
                LocalizationService.ProjectDisplayName(project),
                0,
                IsPlaceholder: true,
                CanMove: false,
                CanCreateBoard: !project.Equals(ProjectLibraryService.OtherLocationProjectName, StringComparison.CurrentCultureIgnoreCase)
                    && !project.Equals(ProjectLibraryService.ArchiveProjectName, StringComparison.CurrentCultureIgnoreCase),
                IsCaptureTarget: false,
                Kind: SidebarDocumentKind.Board));
        }
        var textItems = textBoardItems
            .Select(item => new SidebarDocument(
                item.File.FullName,
                item.TextKind == TextClipsDocumentKind.Legacy
                    ? L("SidebarTextClipsHistory")
                    : item.TextKind == TextClipsDocumentKind.Manual ? ReadCachedTitle(item.File)
                    : Path.GetFileNameWithoutExtension(item.File.Name),
                FormatSidebarDetail(item.File),
                item.TextKind == TextClipsDocumentKind.Manual
                    ? Path.GetRelativePath(_settings.LibraryPath, item.File.DirectoryName!) : AppSettings.TextClipsDirectoryName,
                TextFolderDisplayName(item.File.DirectoryName!),
                textBoardItems.Count,
                IsPlaceholder: false,
                CanMove: item.TextKind == TextClipsDocumentKind.Manual,
                CanCreateBoard: true,
                IsCaptureTarget: textCaptureTarget is not null
                    && item.File.FullName.Equals(textCaptureTarget, StringComparison.OrdinalIgnoreCase),
                Kind: item.TextKind switch
                {
                    TextClipsDocumentKind.Daily => SidebarDocumentKind.TextClipsDaily,
                    TextClipsDocumentKind.Manual => SidebarDocumentKind.TextClipsManual,
                    TextClipsDocumentKind.Legacy => SidebarDocumentKind.TextClipsLegacy,
                    _ => SidebarDocumentKind.TextClipsMonthly
                }))
            .OrderBy(item => item.Kind is SidebarDocumentKind.TextClipsLegacy or SidebarDocumentKind.TextClipsMonthly ? 2
                : item.Kind == SidebarDocumentKind.TextClipsManual ? 1 : 0)
            .ThenBy(item => item.Kind == SidebarDocumentKind.TextClipsManual ? item.ProjectName : "", StringComparer.CurrentCultureIgnoreCase)
            .ThenByDescending(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (!textItems.Any(item => item.Kind == SidebarDocumentKind.TextClipsDaily))
        {
            textItems.Insert(0, new SidebarDocument("", L("SidebarNoBoardPlaceholder"), "", AppSettings.TextClipsDirectoryName,
                L("SidebarTextClips"), 0, true, false, true, false, SidebarDocumentKind.TextClipsDaily));
        }
        foreach (var folder in TextClipsLibrary.GetManualFolders(_settings.LibraryPath))
        {
            var project = Path.Combine(AppSettings.TextClipsDirectoryName, folder);
            if (!textItems.Any(item => item.ProjectName.Equals(project, StringComparison.OrdinalIgnoreCase)))
                textItems.Add(new SidebarDocument("", L("SidebarNoBoardPlaceholder"), "", project,
                    L("SidebarTextClips") + " · " + folder, 0, true, false, true, false, SidebarDocumentKind.TextClipsManual));
        }
        var screenshotItems = screenshotBoardItems
            .Select(item => new SidebarDocument(
                item.File.FullName,
                Path.GetFileNameWithoutExtension(item.File.Name),
                FormatSidebarDetail(item.File),
                AppSettings.ScreenshotsDirectoryName,
                L("SidebarScreenshots"),
                screenshotBoardItems.Count,
                IsPlaceholder: false,
                CanMove: false,
                CanCreateBoard: false,
                IsCaptureTarget: captureTarget is not null
                    && item.File.FullName.Equals(captureTarget, StringComparison.OrdinalIgnoreCase),
                Kind: item.ScreenshotKind == ScreenshotDocumentKind.Daily
                    ? SidebarDocumentKind.ScreenshotDaily
                    : SidebarDocumentKind.ScreenshotHistory))
            .OrderBy(item => item.Kind == SidebarDocumentKind.ScreenshotHistory ? 1 : 0)
            .ThenByDescending(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        regularItems = regularItems
            .OrderBy(item => item.ProjectName.Equals(ProjectLibraryService.DefaultProjectName, StringComparison.CurrentCultureIgnoreCase)
                ? 0
                : item.ProjectName.Equals(ProjectLibraryService.ArchiveProjectName, StringComparison.CurrentCultureIgnoreCase) ? 2
                : item.ProjectName.Equals(ProjectLibraryService.OtherLocationProjectName, StringComparison.CurrentCultureIgnoreCase) ? 3 : 1)
            .ThenBy(item => item.ProjectName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.IsPlaceholder)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var items = screenshotItems.Concat(textItems).Concat(regularItems).ToList();

        _sidebarSelectionChanging = true;
        try
        {
            _sidebarDocuments.Clear();
            foreach (var item in items)
            {
                _sidebarDocuments.Add(item);
            }

            SidebarList.SelectedItem = _activeDocument is null
                ? null
                : _sidebarDocuments.FirstOrDefault(item => item.Path.Equals(_activeDocument.FilePath, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _sidebarSelectionChanging = false;
        }

        SidebarCountText.Text = paths.Count.ToString();
        SidebarEmptyPanel.Visibility = Visibility.Collapsed;
        SidebarLibraryText.Text = _settings.LibraryPath;
        foreach (var removed in _titleCache.Keys.Where(path => !paths.Contains(path)).ToArray()) _titleCache.Remove(removed);
    }

    private string ReadCachedTitle(FileInfo file)
    {
        if (_titleCache.TryGetValue(file.FullName, out var cached)
            && cached.Modified == file.LastWriteTimeUtc.Ticks && cached.Length == file.Length) return cached.Title;
        var title = PinboardDocument.ReadDisplayTitle(file.FullName);
        _titleCache[file.FullName] = (file.LastWriteTimeUtc.Ticks, file.Length, title);
        return title;
    }

    private string TextFolderDisplayName(string directory)
    {
        var relative = Path.GetRelativePath(Path.Combine(_settings.LibraryPath, AppSettings.TextClipsDirectoryName), directory);
        return relative == "." || relative.StartsWith("..", StringComparison.Ordinal)
            ? L("SidebarTextClips") : L("SidebarTextClips") + " · " + relative;
    }

    private void SidebarGroup_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Expander { DataContext: CollectionViewGroup group } expander
            && _expandedGroups.TryGetValue(group.Name.ToString()!, out var expanded))
            expander.IsExpanded = expanded;
    }

    private void SidebarGroup_Changed(object sender, RoutedEventArgs e)
    {
        if (!_sidebarSelectionChanging && sender is System.Windows.Controls.Expander { IsLoaded: true, DataContext: CollectionViewGroup group } expander)
            _expandedGroups[group.Name.ToString()!] = expander.IsExpanded;
    }

    private void OpenLibraryButton_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_settings.LibraryPath) { UseShellExecute = true });

    private void LibraryAddButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = (System.Windows.Controls.Button)sender };
        void Add(string key, RoutedEventHandler handler)
        {
            var item = new System.Windows.Controls.MenuItem { Header = L(key) };
            item.Click += handler;
            menu.Items.Add(item);
        }
        Add("MainNewBoardButton", NewButton_Click);
        Add("MainNewProjectButton", NewProjectButton_Click);
        menu.Items.Add(new System.Windows.Controls.Separator());
        Add("TextNewBoardHeading", NewTextBoardButton_Click);
        Add("TextNewFolder", NewTextFolderButton_Click);
        menu.IsOpen = true;
    }

    private string GetSidebarProjectDisplayName(FileInfo file, string projectName)
    {
        if (projectName.Equals(ProjectLibraryService.DefaultProjectName, StringComparison.CurrentCultureIgnoreCase)
            && Path.GetFileNameWithoutExtension(file.Name).StartsWith("新画板 ", StringComparison.CurrentCulture))
        {
            return L("SidebarToSort");
        }
        return LocalizationService.ProjectDisplayName(projectName);
    }

    private void ConfigureLibraryWatcher()
    {
        _libraryWatcher?.Dispose();
        _libraryWatcher = null;

        try
        {
            Directory.CreateDirectory(_settings.LibraryPath);
            _libraryWatcher = new FileSystemWatcher(_settings.LibraryPath, "*.pinboard")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            _libraryWatcher.Created += LibraryWatcher_Changed;
            _libraryWatcher.Deleted += LibraryWatcher_Changed;
            _libraryWatcher.Renamed += LibraryWatcher_Changed;
        }
        catch (Exception ex)
        {
            SetStatus(L("StatusWatchBoardsFailedFormat", ex.Message));
        }
    }

    private void LibraryWatcher_Changed(object sender, FileSystemEventArgs e)
    {
        Dispatcher.BeginInvoke(() => { _sidebarRefreshTimer.Stop(); _sidebarRefreshTimer.Start(); });
    }

    private static string FormatSidebarDetail(FileInfo file)
    {
        var modified = file.LastWriteTime;
        return modified.Date == DateTime.Today
            ? L("SidebarTodayFormat", modified)
            : modified.Year == DateTime.Today.Year
                ? modified.ToString("MM-dd  HH:mm")
                : modified.ToString("yyyy-MM-dd");
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            NewButton_Click(sender, e);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            OpenButton_Click(sender, e);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.F)
        {
            SearchButton_Click(sender, e);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            _ = ShowSearchAsync(allDocuments: false);
            e.Handled = true;
        }
    }

    private void InitializeTray()
    {
        var menu = new Forms.ContextMenuStrip();
        _trayOpenItem = new Forms.ToolStripMenuItem(L("TrayOpen"), null, (_, _) => Dispatcher.Invoke(ShowAndActivate));
        menu.Items.Add(_trayOpenItem);
        _trayCaptureItem = new Forms.ToolStripMenuItem(L("TrayCaptureDefault"), null, (_, _) => Dispatcher.Invoke(BeginPixPinCapture));
        menu.Items.Add(_trayCaptureItem);
        _trayTargetItem = new Forms.ToolStripMenuItem(L("TrayTarget"));
        _trayTargetItem.DropDownOpening += (_, _) => Dispatcher.Invoke(PopulateTrayTargetMenu);
        menu.Items.Add(_trayTargetItem);
        _trayPauseItem = new Forms.ToolStripMenuItem(L("TrayPause"))
        {
            Checked = _settings.CapturePaused,
            CheckOnClick = true
        };
        _trayPauseItem.CheckedChanged += (_, _) => Dispatcher.Invoke(() =>
        {
            _settings.CapturePaused = _trayPauseItem.Checked;
            _settings.Save();
            SetStatus(_settings.CapturePaused ? L("StatusPixPinPaused") : L("StatusPixPinResumed"));
        });
        menu.Items.Add(_trayPauseItem);
        _trayPixPinItem = new Forms.ToolStripMenuItem(L("TrayPixPin"), null, (_, _) => Dispatcher.Invoke(() => new PixPinIntegrationWindow(_pixPin) { Owner = this }.ShowDialog()));
        menu.Items.Add(_trayPixPinItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        _trayExitItem = new Forms.ToolStripMenuItem(L("TrayExit"), null, (_, _) => Dispatcher.Invoke(ExitApplication));
        menu.Items.Add(_trayExitItem);

        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            _trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
        }

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Pinboard",
            Icon = _trayIcon ?? System.Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == Forms.MouseButtons.Left)
            {
                Dispatcher.Invoke(ShowAndActivate);
            }
        };
        _notifyIcon.BalloonTipClicked += (_, _) => Dispatcher.Invoke(async () =>
        {
            ShowAndActivate();
            LocateButton_Click(this, new RoutedEventArgs());
            await Task.CompletedTask;
        });
        RefreshTrayMenuText();
        UpdateCaptureTargetText();
    }

    private void ShowNotification(string title, string text)
    {
        if (_notifyIcon is null)
        {
            return;
        }
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.ShowBalloonTip(4000);
    }

    private void PopulateTrayTargetMenu()
    {
        if (_trayTargetItem is null)
        {
            return;
        }

        _trayTargetItem.DropDownItems.Clear();
        var automaticItem = new Forms.ToolStripMenuItem(L("BoardMenuUseMonthlyTarget"))
        {
            Checked = string.IsNullOrWhiteSpace(_settings.FixedCaptureTarget)
        };
        automaticItem.Click += (_, _) => Dispatcher.Invoke(UseMonthlyCaptureTarget);
        _trayTargetItem.DropDownItems.Add(automaticItem);

        var boards = _sidebarDocuments
            .Where(item => !item.IsPlaceholder && !item.IsArchived && !item.IsTextBoard)
            .GroupBy(item => item.ProjectDisplayName)
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (boards.Count == 0)
        {
            return;
        }

        _trayTargetItem.DropDownItems.Add(new Forms.ToolStripSeparator());
        foreach (var project in boards)
        {
            var projectItem = new Forms.ToolStripMenuItem(project.Key);
            foreach (var board in project.OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase))
            {
                var boardItem = new Forms.ToolStripMenuItem(board.Title)
                {
                    Checked = board.IsCaptureTarget,
                    ToolTipText = board.Path
                };
                boardItem.Click += (_, _) => Dispatcher.Invoke(() => SetFixedCaptureTarget(board));
                projectItem.DropDownItems.Add(boardItem);
            }
            _trayTargetItem.DropDownItems.Add(projectItem);
        }
    }

    private void ShowAndActivate()
    {
        _canvasIdleTimer.Stop();
        ShowInTaskbar = true;
        ShowActivated = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = _settings.AlwaysOnTop;
        Focus();
        _ = EnsureVisibleCanvasAsync();
    }

    private async Task ReleaseIdleCanvasAsync()
    {
        _canvasIdleTimer.Stop();
        if (IsVisible && WindowState != WindowState.Minimized || _exiting) return;
        await _captureGate.WaitAsync();
        try
        {
            if (IsVisible && WindowState != WindowState.Minimized || CanvasView is null) return;
            if (!_webReady || _canvasLoading || _pendingCanvasRequests.Count > 0) { _canvasIdleTimer.Start(); return; }
            if (_activeDocument is { } document)
            {
                var result = await RequestCanvasAsync("Flush", new { }, document.FilePath, TimeSpan.FromSeconds(30));
                if (result.TryGetProperty("skipped", out var skipped) && skipped.ValueKind == JsonValueKind.True)
                {
                    _canvasIdleTimer.Start();
                    return;
                }
                if (IsVisible && WindowState != WindowState.Minimized) return;
                if (result.TryGetProperty("viewport", out var viewport))
                {
                    _sleepViewport = viewport.Clone();
                    _sleepDocumentPath = document.FilePath;
                }
            }
            _webReady = false;
            CanvasHost.Content = null;
            var view = CanvasView;
            CanvasView = null;
            view.CoreWebView2.WebMessageReceived -= Core_WebMessageReceived;
            view.Dispose();
            SetStatus(L("StatusCanvasSleeping"));
        }
        catch (Exception ex)
        {
            // A failed flush must leave the live canvas intact so edits remain recoverable.
            DiagnosticLog.Write("canvas-sleep", ex);
            SetStatus(L("SaveStateFailed"));
        }
        finally { _captureGate.Release(); }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_explicitExit)
        {
            e.Cancel = true;
            SetStatus(L("StatusBackgroundDefaultCapture"));
            ShowInTaskbar = false;
            Hide();
            _canvasIdleTimer.Start();
            return;
        }
        _notifyIcon?.Dispose();
        _trayIcon?.Dispose();
        _libraryWatcher?.Dispose();
        _clipboardCapture.Dispose();
        _defaultPixPinShortcut.Dispose();
        _canvasIdleTimer.Stop();
        _sidebarRefreshTimer.Stop();
        CanvasView?.Dispose();
    }

    private async void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        await _captureGate.WaitAsync();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (_canvasLoading && DateTime.UtcNow < deadline) await Task.Delay(100);
            if (_canvasLoading) throw new TimeoutException("The canvas is still loading.");
            if (_webReady && _activeDocument is { } document)
            {
                var result = await RequestCanvasAsync("Flush", new { }, document.FilePath, TimeSpan.FromSeconds(30));
                if (result.TryGetProperty("skipped", out var skipped) && skipped.ValueKind == JsonValueKind.True)
                    throw new InvalidOperationException("The active canvas has not confirmed its save.");
            }
            _explicitExit = true;
            Close();
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _exiting = false;
            DiagnosticLog.Write("exit-save", ex);
            ShowAndActivate();
            MessageBox.Show(this, L("ExitSaveFailed"), "Pinboard", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _captureGate.Release(); }
    }

    private void PostToCanvas(string type, object payload, string? documentId = null, string? requestId = null)
    {
        if (!_webReady || CanvasView?.CoreWebView2 is null)
        {
            return;
        }
        CanvasView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { version = 1, type, requestId, documentId, payload }));
    }

    private static bool IsTrustedCanvasUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && uri.Host.Equals("app.pinboard", StringComparison.OrdinalIgnoreCase);

    private async Task<JsonElement> RequestCanvasAsync(string type, object payload, string documentId, TimeSpan timeout)
    {
        if (!_webReady || CanvasView?.CoreWebView2 is null)
        {
            throw new InvalidOperationException(L("CanvasNotReady"));
        }
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCanvasRequests[requestId] = completion;
        CanvasView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { version = 1, type, requestId, documentId, payload }));
        using var cancellation = new CancellationTokenSource(timeout);
        using var registration = cancellation.Token.Register(() => completion.TrySetException(new TimeoutException(L("CanvasRequestTimeout"))));
        try
        {
            return await completion.Task;
        }
        finally
        {
            _pendingCanvasRequests.Remove(requestId);
        }
    }

    private void CompleteCanvasRequest(BridgeMessage message, bool succeeded)
    {
        if (string.IsNullOrWhiteSpace(message.RequestId) || !_pendingCanvasRequests.TryGetValue(message.RequestId, out var completion))
        {
            return;
        }
        if (succeeded)
        {
            completion.TrySetResult(message.Payload.Clone());
        }
        else
        {
            var error = message.Payload.TryGetProperty("error", out var value)
                ? value.GetString()
                : message.Payload.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
            completion.TrySetException(new InvalidOperationException(error ?? L("CanvasOperationFailed")));
        }
    }

    private void SetStatus(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetStatus(text));
            return;
        }
        StatusText.Text = text;
    }

    private void UpdateCaptureTargetText()
    {
        var target = GetCaptureTargetTitle();
        CaptureTargetText.Text = L("CaptureTargetFormat", target);
        if (_trayTargetItem is not null)
        {
            _trayTargetItem.Text = L("TrayCurrentTargetFormat", target);
        }
        if (_trayPauseItem is not null)
        {
            _trayPauseItem.Checked = _settings.CapturePaused;
        }
    }

    private string GetCaptureTargetTitle()
    {
        var path = _settings.ResolveCaptureTarget(DateTimeOffset.Now);
        if (_documents.TryGetValue(path, out var document))
        {
            return document.Title;
        }

        return File.Exists(path)
            ? PinboardDocument.ReadDisplayTitle(path)
            : Path.GetFileNameWithoutExtension(path);
    }

    private void RefreshTrayMenuText()
    {
        if (_trayOpenItem is not null) _trayOpenItem.Text = L("TrayOpen");
        if (_trayCaptureItem is not null) _trayCaptureItem.Text = L("TrayCaptureDefault");
        if (_trayPauseItem is not null) _trayPauseItem.Text = L("TrayPause");
        if (_trayPixPinItem is not null) _trayPixPinItem.Text = L("TrayPixPin");
        if (_trayExitItem is not null) _trayExitItem.Text = L("TrayExit");
        if (_trayTargetItem is not null) _trayTargetItem.Text = L("TrayTarget");
    }

    private static string L(string key, params object?[] arguments) => LocalizationService.T(key, arguments);

    private static (double X, double Y) FindImageLocation(string sceneJson, string fileId)
    {
        using var json = JsonDocument.Parse(sceneJson);
        if (!json.RootElement.TryGetProperty("elements", out var elements))
        {
            return (0, 0);
        }
        foreach (var element in elements.EnumerateArray())
        {
            if (element.TryGetProperty("fileId", out var value) && value.GetString() == fileId)
            {
                return (
                    element.TryGetProperty("x", out var x) ? x.GetDouble() : 0,
                    element.TryGetProperty("y", out var y) ? y.GetDouble() : 0);
            }
        }
        return (0, 0);
    }

    private static string? FindLastInboxElement(string sceneJson)
    {
        using var json = JsonDocument.Parse(sceneJson);
        if (!json.RootElement.TryGetProperty("elements", out var elements))
        {
            return null;
        }
        return elements.EnumerateArray()
            .Where(element => element.TryGetProperty("type", out var type) && type.GetString() == "image")
            .Where(element => !element.TryGetProperty("isDeleted", out var deleted) || !deleted.GetBoolean())
            .OrderByDescending(element => element.TryGetProperty("updated", out var updated) ? updated.GetInt64() : 0)
            .Select(element => element.TryGetProperty("id", out var id) ? id.GetString() : null)
            .FirstOrDefault();
    }

    private static async Task<string?> ResolveOcrElementIdAsync(PinboardDocument document, string referenceId)
    {
        var hash = referenceId.Split(':', 2)[0];
        var snapshot = await document.LoadSnapshotAsync(includeFiles: false);
        var fileIds = (await document.FindFileIdsByHashAsync(hash)).ToHashSet(StringComparer.Ordinal);
        using var json = JsonDocument.Parse(snapshot.SceneJson);
        if (!json.RootElement.TryGetProperty("elements", out var elements))
        {
            return null;
        }
        foreach (var element in elements.EnumerateArray())
        {
            if (element.TryGetProperty("fileId", out var value) && value.GetString() is { } fileId && fileIds.Contains(fileId))
            {
                return element.TryGetProperty("id", out var id) ? id.GetString() : null;
            }
        }
        return null;
    }

    private enum SidebarDocumentKind
    {
        Board,
        ScreenshotDaily,
        ScreenshotHistory,
        TextClipsDaily,
        TextClipsMonthly,
        TextClipsLegacy,
        TextClipsManual
    }

    private sealed record SidebarDocument(
        string Path,
        string Title,
        string Detail,
        string ProjectName,
        string ProjectDisplayName,
        int ProjectBoardCount,
        bool IsPlaceholder,
        bool CanMove,
        bool CanCreateBoard,
        bool IsCaptureTarget,
        SidebarDocumentKind Kind)
    {
        public string SectionDisplayName => Kind switch
        {
            SidebarDocumentKind.ScreenshotDaily => DailySection(LocalizationService.T("SidebarScreenshots")),
            SidebarDocumentKind.ScreenshotHistory => LocalizationService.T("SidebarScreenshotsHistory"),
            SidebarDocumentKind.TextClipsMonthly or SidebarDocumentKind.TextClipsLegacy => LocalizationService.T("SidebarTextClipsHistory"),
            SidebarDocumentKind.TextClipsDaily => DailySection(LocalizationService.T("SidebarTextClips")),
            SidebarDocumentKind.TextClipsManual => ProjectDisplayName,
            _ => LocalizationService.T("SidebarProjects") + " · " + ProjectDisplayName
        };
        private string DailySection(string section) =>
            Title.Length >= 10 && Title[..7] != DateTime.Now.ToString("yyyy-MM")
                ? section + " · " + Title[..7] : section;
        public string Glyph => IsTextBoard ? "\uE8A5" : "\uEB9F";
        public bool IsTextBoard => Kind is SidebarDocumentKind.TextClipsDaily or SidebarDocumentKind.TextClipsMonthly
            or SidebarDocumentKind.TextClipsLegacy or SidebarDocumentKind.TextClipsManual;
        public bool IsArchived => ProjectName.Equals(ProjectLibraryService.ArchiveProjectName, StringComparison.CurrentCultureIgnoreCase);
        public bool IsDefaultExpanded => !IsArchived && Kind is not SidebarDocumentKind.ScreenshotHistory
            and not SidebarDocumentKind.TextClipsMonthly and not SidebarDocumentKind.TextClipsLegacy
            && (Kind is not SidebarDocumentKind.ScreenshotDaily and not SidebarDocumentKind.TextClipsDaily
                || IsPlaceholder || Title.StartsWith(DateTime.Now.ToString("yyyy-MM"), StringComparison.Ordinal));

        public override string ToString() => Title;
    }
}
