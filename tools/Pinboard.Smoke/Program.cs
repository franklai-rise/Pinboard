using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Pinboard.App;
using Pinboard.App.Models;
using Pinboard.App.Services;
using Pinboard.App.Services.AiSessions;
using System.Windows.Controls;

internal static class Program
{
    private static MainWindow Window = null!;
    private static string Output = null!;
    private static int _passed;
    private static object? Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Window);
    private static object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Window, args);

    [STAThread]
    public static int Main(string[] args)
    {
        Output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "Pinboard-smoke-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Output);
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Pinboard;component/Resources/Strings.en.xaml", UriKind.Relative) });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Pinboard;component/Styles/AppleTheme.xaml", UriKind.Relative) });
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(); app.Shutdown(0); }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                File.WriteAllText(Path.Combine(Output, "failure.txt"), ex.ToString());
                await StopHarnessAsync();
                app.Shutdown(1);
            }
        };
        return app.Run();
    }

    private static async Task RunAsync()
    {
        var settings = new AppSettings
        {
            LibraryPath = Path.Combine(Output, "Library"), StoragePath = Path.Combine(Output, "settings.json"),
            StartAtLogin = false, TextCaptureEnabled = false, TextCaptureBoardMode = TextCaptureBoardMode.Daily,
            AiSessionsSourceProfilePath = Path.Combine(Output, "SourceProfile"),
            OcrChinese = false, OcrEnglish = false
        };
        Window = new MainWindow(settings, startHidden: true);
        await Window.InitializeAsync(["--background"]);
        Check(Field("CanvasView") is null, "Background launch creates no WebView");
        Check(!Directory.EnumerateFiles(settings.LibraryPath, "*.pinboard", SearchOption.AllDirectories).Any(), "Background launch creates no empty boards");
        var textPath = Path.Combine(settings.LibraryPath, "Text Clips", DateTime.Now.ToString("yyyy-MM-dd") + ".pinboard");
        var text = new PinboardDocument(textPath);
        for (var index = 0; index < 9; index++)
            await text.InsertCapturedTextAsync(index == 0
                ? "A place for everything you want to remember.\n\nCollect screenshots, ideas and small discoveries. Organize them later."
                : "Design notes " + (index + 1) + "\n\nSpace makes the important things easier to see.\nKeep the canvas quiet and the ideas close.", "smoke");
        var longText = string.Join("\n", Enumerable.Range(1, 220).Select(index => $"Reference line {index}: keep useful details for later."))
            + "\nA late searchabletail keyword should stay readable.";
        var longClip = await text.InsertCapturedTextAsync(longText, "smoke");
        var picturePath = Path.Combine(settings.LibraryPath, "Screenshots", DateTime.Now.ToString("yyyy-MM-dd") + ".pinboard");
        var pictures = new PinboardDocument(picturePath);
        await pictures.InsertCapturedImageAsync(new ImageProcessor().Process(CreateDemoScreenshot(false), 80), "demo");
        await pictures.InsertCapturedImageAsync(new ImageProcessor().Process(CreateDemoScreenshot(true), 80), "demo");
        var demoScene = JsonNode.Parse((await pictures.LoadSnapshotAsync(includeFiles: false)).SceneJson)!.AsObject();
        var images = demoScene["elements"]!.AsArray().Where(element => element?["type"]?.GetValue<string>() == "image").ToArray();
        images[0]!["x"] = 0; images[0]!["y"] = 100;
        images[1]!["x"] = 784; images[1]!["y"] = 180;
        demoScene["appState"]!["zoom"] = new JsonObject { ["value"] = 0.55 };
        demoScene["appState"]!["scrollX"] = 80;
        demoScene["appState"]!["scrollY"] = 180;
        await pictures.SaveSceneAsync(demoScene.ToJsonString(), []);
        TextClipsLibrary.CreateFolder(settings.LibraryPath, "Ideas");
        Directory.CreateDirectory(Path.Combine(settings.LibraryPath, "Research"));
        var manual = new PinboardDocument(TextClipsLibrary.CreateManualBoardPath(settings.LibraryPath, "Ideas", "Reading notes"));
        await manual.InitializeAsync();
        await manual.RenameDisplayTitleAsync("Notes worth keeping");

        await CheckNativeReaderAsync(settings, text, longClip);
        // Existing canvas-specific checks explicitly opt out of the new native view.
        settings.PreferTextReadingView = false;
        settings.TextCaptureEnabled = false;

        await Window.HandleActivationAsync(["--open", picturePath]);
        await WaitReadyAsync();
        await CheckActiveImageDeltaAsync(settings, pictures);
        await Task.Delay(500);
        await ScreenshotAsync("screenshot-inbox");

        await Window.HandleActivationAsync(["--open", textPath]);
        await CheckLongTextCanvasFocusAsync(text, longClip);
        await Task.Delay(400);
        await ScreenshotAsync("text-library");
        Call("PostToCanvas", "FocusBottom", new { }, textPath, null);
        await Task.Delay(300);
        var before = await FlushAsync(textPath);
        var expectedY = before.GetProperty("viewport").GetProperty("scrollY").GetDouble();
        Check(expectedY < -300, "Bottom button reaches lower text without changing zoom");
        Check(before.GetProperty("viewport").GetProperty("zoom").GetProperty("value").GetDouble() > 0, "Flush records viewport");

        for (var index = 0; index < 3; index++)
        {
            await Window.HandleActivationAsync(["--open", picturePath]); await WaitReadyAsync();
            await Window.HandleActivationAsync(["--open", textPath]); await WaitReadyAsync();
        }
        Check(true, "Repeated board switching renders successfully");
        await Window.HandleActivationAsync(["--open", picturePath]);
        await WaitReadyAsync();
        settings.TextCaptureEnabled = true;
        await Window.HandleActivationAsync(["--open", textPath]);
        Call("ClipboardCapture_TextCaptured", null, "Make room for curiosity.\n\nSave useful references now. Return when the idea is ready.");
        await WaitReadyAsync();
        var captureDeadline = DateTime.UtcNow.AddSeconds(15);
        while ((await text.SearchAsync("curiosity")).Count == 0 && DateTime.UtcNow < captureDeadline) await Task.Delay(100);
        Check((await text.SearchAsync("curiosity")).Count == 1, "Capture during board switch reaches intended board");
        await WaitReadyAsync();
        Window.Width = 840;
        Window.Height = 640;
        await Task.Delay(800);
        Call("PostToCanvas", "FocusBottom", new { }, textPath, null);
        await Task.Delay(600);
        before = await FlushAsync(textPath);
        expectedY = before.GetProperty("viewport").GetProperty("scrollY").GetDouble();
        await ScreenshotAsync("compact-library");
        Window.Close();
        await (Task)Call("ReleaseIdleCanvasAsync")!;
        Check(Field("CanvasView") is null, "Tray sleep disposes WebView after save acknowledgement");
        Check(!Window.IsVisible, "Tray sleep does not reveal window");
        settings.TextCaptureEnabled = true;
        Call("ClipboardCapture_TextCaptured", null, "Background collection remains available while the canvas sleeps.");
        for (var index = 0; index < 3; index++)
            Call("ClipboardCapture_TextCaptured", null, "sleeping_repeat · journal every repeated background copy.");
        await WaitUntilAsync(async () => (await text.SearchAsync("sleeps")).Count == 1, "Background capture did not reach the sleeping board.");
        await WaitUntilAsync(async () => (await text.SearchAsync("sleeping_repeat")).Count == 3, "Background repeated captures were not all committed.");
        await WaitCaptureIdleAsync();
        Check(Field("CanvasView") is null, "Background text does not recreate WebView");
        Check((await text.SearchAsync("sleeps")).Count == 1, "Sleeping receiver saves and indexes text");
        Check((await text.SearchAsync("sleeping_repeat")).Count == 3, "Sleeping receiver journals each repeated copy independently");
        await Window.HandleActivationAsync([]);
        await WaitReadyAsync();
        var after = await FlushAsync(textPath);
        Check(Math.Abs(after.GetProperty("viewport").GetProperty("scrollY").GetDouble() - expectedY) < 1, "Wake restores reading position");
        Check((await text.SearchAsync("sleeps")).Count == 1, "Wake retains background capture");
        Check((await text.SearchAsync("sleeping_repeat")).Count == 3, "Wake retains all repeated background captures");
        // Simulate the host being ahead of the rendered board during a rapid switch.
        typeof(MainWindow).GetField("_activeDocument", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Window, pictures);
        Window.Close();
        await (Task)Call("ReleaseIdleCanvasAsync")!;
        Check(Field("CanvasView") is not null, "Skipped flush cannot discard a mismatched live canvas");
        typeof(MainWindow).GetField("_activeDocument", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Window, text);
        Window.Close();
        await (Task)Call("ReleaseIdleCanvasAsync")!;
        Check(Field("CanvasView") is null, "Second sleep releases recreated renderer");
        await CheckAiSessionsAsync(settings, picturePath);
        File.WriteAllText(Path.Combine(Output, "passed.txt"), $"All {_passed} desktop smoke checks passed.\n" + DateTimeOffset.Now.ToString("O"));
        Console.WriteLine("Output: " + Output);
        // Cleanly release only this harness's window/listeners, never the installed app.
        await StopHarnessAsync();
    }

    private static async Task CheckNativeReaderAsync(AppSettings settings, PinboardDocument document, TextClipInsertResult longClip)
    {
        Check(settings.PreferTextReadingView, "Native text reading is enabled by default");
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(document.FilePath));
        await Window.HandleActivationAsync(["--open", document.FilePath]);
        await WaitUntilAsync(() => Task.FromResult(Field("_readerDocumentPath") as string == document.FilePath
            && Field("_textReader") is TextReadingView && ((System.Windows.Controls.ContentControl)Window.FindName("CanvasHost")).Content is TextReadingView),
            "Native text view did not load.");
        Check(Field("CanvasView") is null, "Opening a text board creates no WebView");
        var reader = (TextReadingView)Field("_textReader")!;
        var list = (System.Windows.Controls.ListBox)reader.FindName("ClipsList");
        Check(list.Items.Count == 10, "Native reader lists copied content without timestamp-label duplicates");
        var readHash = SHA256.HashData(await File.ReadAllBytesAsync(document.FilePath));
        Check(originalHash.SequenceEqual(readHash), "Reading does not rewrite the source board");
        Check(reader.FocusElement(longClip.ElementId, "searchabletail"), "Reader focuses a late keyword in a long clip");
        await Task.Delay(300);
        var detail = (System.Windows.Controls.RichTextBox)reader.FindName("FullTextBox");
        Check(detail.FontSize >= 16 && detail.VerticalOffset > 100, "Native reader shows late search matches at readable size");
        await ScreenshotAsync("native-text-reader");

        settings.TextCaptureEnabled = true;
        for (var index = 0; index < 3; index++) Call("ClipboardCapture_TextCaptured", null, "repeat_keyword · every copy remains an independent record.");
        await WaitUntilAsync(async () => (await document.SearchAsync("repeat_keyword")).Count == 3, "Repeated captures were not all committed.");
        await WaitCaptureIdleAsync();
        Check(Field("CanvasView") is null, "Repeated capture refreshes the native reader without loading a browser");
        reader.FocusElement(longClip.ElementId, "");
        Check(list.Items.Count == 13, "Reader keeps all repeated records before grouping");
        ((System.Windows.Controls.CheckBox)reader.FindName("CollapseDuplicatesBox")).IsChecked = true;
        Check(list.Items.Count == 11, "Reader groups only the display of consecutive repeated clips");
        Check((await document.SearchAsync("repeat_keyword")).Count == 3, "Display grouping leaves all database records intact");
        ((System.Windows.Controls.CheckBox)reader.FindName("CollapseDuplicatesBox")).IsChecked = false;
    }

    private static async Task CheckActiveImageDeltaAsync(AppSettings settings, PinboardDocument document)
    {
        var view = (WebView2)Field("CanvasView")!;
        Check(((HashSet<string>)Field("_canvasFileIds")!).Count == 2, "Initial screenshot board acknowledges its image cache");
        await view.CoreWebView2.ExecuteScriptAsync("window.__pinboardSmokeInbound=[];window.chrome.webview.addEventListener('message',event=>{if(['OpenDocument','ExternalDocumentUpdated'].includes(event.data.type)){const p=event.data.payload;window.__pinboardSmokeInbound.push({type:event.data.type,revision:p.revision,fileIds:p.files.map(file=>file.fileId)});}});");
        var before = await FlushAsync(document.FilePath);
        var originalImages = CountImages((await document.LoadSnapshotAsync(includeFiles: false)).SceneJson);
        Call("ClipboardCapture_ImageCaptured", null, CreateDemoScreenshot(false, "CAPTURED AT 09:42"));
        await WaitUntilAsync(async () => CountImages((await document.LoadSnapshotAsync(includeFiles: false)).SceneJson) == originalImages + 1,
            "Active screenshot capture did not commit.");
        await WaitCaptureIdleAsync();
        await WaitReadyAsync();
        var after = await FlushAsync(document.FilePath);
        var inbound = JsonDocument.Parse(await view.CoreWebView2.ExecuteScriptAsync("JSON.stringify(window.__pinboardSmokeInbound)")).RootElement.GetString()!;
        using var messages = JsonDocument.Parse(inbound);
        var updates = messages.RootElement.EnumerateArray().ToArray();
        Check(updates.Length == 1 && updates[0].GetProperty("type").GetString() == "ExternalDocumentUpdated", "Active capture uses a delta update without falling back to a full reload");
        Check(updates[0].GetProperty("fileIds").GetArrayLength() == 1, "Delta transfers only the new image, not the whole board");
        Check(((HashSet<string>)Field("_canvasFileIds")!).Count == 3, "Delta render confirms both cached and new images");
        Check(before.GetProperty("viewport").GetRawText() == after.GetProperty("viewport").GetRawText(), "Background screenshot capture preserves reading position and zoom");
        Check(updates[0].GetProperty("revision").GetInt64() == (await document.LoadSnapshotAsync(includeFiles: false)).Revision,
            "Delta renderer acknowledges the committed database revision");
    }

    private static async Task CheckLongTextCanvasFocusAsync(PinboardDocument document, TextClipInsertResult clip)
    {
        // Go through the host's ready gate, so locating a search hit also works
        // immediately after a renderer remount or document change.
        Call("FocusCanvasWhenReady", new { elementId = clip.ElementId, query = "searchabletail" }, document.FilePath);
        await WaitReadyAsync();
        await Task.Delay(150);
        var viewport = (await FlushAsync(document.FilePath)).GetProperty("viewport");
        var zoom = viewport.GetProperty("zoom").GetProperty("value").GetDouble();
        Check(zoom >= 0.75 && zoom <= 1.25, "Canvas long-text search keeps a readable zoom instead of fitting the whole card");
        Check(viewport.GetProperty("scrollY").GetDouble() < -3000, "Canvas search reaches the matching line near the end of long text");
        Check(Field("_pendingFocus") is null, "Queued search navigation clears after the matching document renders");
    }

    private static int CountImages(string scene)
    {
        using var document = JsonDocument.Parse(scene);
        return document.RootElement.GetProperty("elements").EnumerateArray().Count(element =>
            element.GetProperty("type").GetString() == "image" && !element.GetProperty("isDeleted").GetBoolean());
    }

    private static async Task WaitCaptureIdleAsync()
    {
        await WaitUntilAsync(() => Task.FromResult(((HashSet<Task>)Field("_captureWrites")!).Count == 0
            && ((CaptureInbox)Field("_captureInbox")!).ReadPending().Count == 0
            && ((SemaphoreSlim)Field("_captureGate")!).CurrentCount == 1), "Capture journal did not finish processing.");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException(failure);
    }

    private static async Task StopHarnessAsync()
    {
        if (Window is null) return;
        await ((CaptureInbox)Field("_captureInbox")!).StopAsync();
        await ((OcrJobQueueService)Field("_ocrQueue")!).StopAsync();
        if (Field("_aiSessions") is SessionArchiveService ai) await ai.StopAsync();
        typeof(MainWindow).GetField("_explicitExit", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Window, true);
        Window.Close();
    }

    private static Task<JsonElement> FlushAsync(string document) =>
        (Task<JsonElement>)Call("RequestCanvasAsync", "Flush", new { }, document, TimeSpan.FromSeconds(15))!;

    private static async Task CheckAiSessionsAsync(AppSettings settings, string boardPath)
    {
        await (Task)Call("EnsureAiSessionsAsync")!;
        var service = (SessionArchiveService)Field("_aiSessions")!; service.Paused = true;
        var demonstration = Path.Combine(Output, "DemoExport.txt"); File.WriteAllText(demonstration, "A non-sensitive demo conversation.");
        var seed = new SessionDescriptor { Key = "Codex:demo-0", Source = "Codex", SourceId = "demo-0", Title = "Capture board polish",
            Project = "Design lab", Path = demonstration, UpdatedAt = DateTimeOffset.Now, Size = new FileInfo(demonstration).Length };
        service.Catalog.Merge(Enumerable.Range(0, 10000).Select(index => seed with { Key = "Codex:demo-" + index,
            SourceId = "demo-" + index, Title = index == 0 ? "Capture board polish" : index == 1 ? "Layout options" : index == 2 ? "Memory review" : "Research notes " + index,
            ParentKey = index is 1 or 2 ? "Codex:demo-0" : null, UpdatedAt = seed.UpdatedAt.AddMinutes(-index) }));
        Check(service.Catalog.List().All(x => !x.Selected), "New AI conversations are unchecked by default");
        settings.PreferTextReadingView = false;
        await Window.HandleActivationAsync(["--open", boardPath]); await WaitReadyAsync();
        Call("AiSessionsButton_Click", Window.FindName("AiSessionsSidebarButton"), new RoutedEventArgs());
        await WaitUntilAsync(() => Task.FromResult(Field("_aiSessionsView") is AiSessionsView view && view.IsLoaded), "AI Sessions view did not open");
        var sessions = (AiSessionsView)Field("_aiSessionsView")!; await sessions.ReloadAsync(); Window.UpdateLayout();
        var list = (ListBox)sessions.FindName("SessionList");
        Check(Field("CanvasView") is null, "AI Sessions releases WebView rather than loading transcripts");
        Check(list.Items.Count == 9998, "Child conversations are folded under their parent");
        Check(list.ItemContainerGenerator.ContainerFromIndex(5000) is null, "Ten thousand AI titles use virtualized containers");
        var captureBoard = new PinboardDocument(boardPath);
        var imageCount = CountImages((await captureBoard.LoadSnapshotAsync(includeFiles: false)).SceneJson);
        Call("ClipboardCapture_ImageCaptured", null, CreateDemoScreenshot(false, "AI LIBRARY BACKGROUND CAPTURE"));
        await WaitCaptureIdleAsync();
        Check(CountImages((await captureBoard.LoadSnapshotAsync(includeFiles: false)).SceneJson) == imageCount + 1,
            "Screenshot capture keeps saving while the AI library is open");
        Check(Field("CanvasView") is null && Field("_showAiSessions") is true,
            "Background screenshots do not navigate away from AI titles");
        await Task.Delay(400);
        Check(((ListBox)Window.FindName("SidebarList")).SelectedItem is null, "Only the AI sidebar entry stays selected during capture");
        var parent = (AiSessionsView.SessionRow)list.Items[0]; parent.Selected = true;
        await WaitUntilAsync(() => Task.FromResult(service.Catalog.Find(parent.Key)!.Selected), "AI selection did not persist");
        Check(!service.Catalog.Find("Codex:demo-1")!.Selected, "Selecting a parent does not select its subagents");
        await sessions.ReloadAsync();
        var selectAll = (System.Windows.Controls.Button)sessions.FindName("SelectAllButton");
        var clearSelection = (System.Windows.Controls.Button)sessions.FindName("ClearSelectionButton");
        Check(selectAll.Content?.ToString() == "Select all" && clearSelection.Content?.ToString() == "Deselect all",
            "Bulk selection controls are visible above the AI list");
        await ClickSelectAllAsync(sessions, accept: false);
        Check(service.Catalog.List().Count(x => x.Selected) == 1, "Cancelling bulk confirmation does not change any selection");
        await ClickSelectAllAsync(sessions, accept: true);
        Check(service.Catalog.List().Count(x => x.Selected) == 10000, "Select all includes all ten thousand titles and folded children");
        Check(!selectAll.IsEnabled && clearSelection.IsEnabled, "Bulk controls reflect an all-selected scope");
        clearSelection.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); await WaitForSelectionIdleAsync(sessions);
        Check(service.Catalog.List().All(x => !x.Selected) && service.Catalog.NextJob() is null, "Deselect all clears selection and queued jobs");
        await sessions.ReloadAsync(); await ScreenshotAsync("ai-sessions");
        var filter = (System.Windows.Controls.TextBox)sessions.FindName("FilterBox"); filter.Text = "Layout options"; await Task.Delay(350); Window.UpdateLayout();
        Check(list.Items.Cast<AiSessionsView.SessionRow>().Any(x => x.Key == "Codex:demo-1"), "Title search reveals a matching folded child");
        await ClickSelectAllAsync(sessions, accept: true);
        Check(service.Catalog.Find("Codex:demo-1")!.Selected && !service.Catalog.Find("Codex:demo-0")!.Selected,
            "Filtered select all checks the matching child, not its context parent");
        service.Catalog.Select("Codex:demo-0", true); await sessions.ReloadAsync();
        clearSelection.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); await WaitForSelectionIdleAsync(sessions);
        Check(!service.Catalog.Find("Codex:demo-1")!.Selected && service.Catalog.Find("Codex:demo-0")!.Selected,
            "Filtered deselection preserves selected conversations outside the filter");
        filter.Text = "No matching title"; await Task.Delay(350);
        Check(!selectAll.IsEnabled && !clearSelection.IsEnabled, "Empty search disables both bulk actions");
        filter.Text = ""; await Task.Delay(350);
        var sourceFilter = (ComboBox)sessions.FindName("SourceBox"); sourceFilter.SelectedIndex = 2;
        Check(!selectAll.IsEnabled && !clearSelection.IsEnabled, "Source filter limits the bulk selection scope");
        sourceFilter.SelectedIndex = 0;
        var previousWidth = Window.Width; Window.Width = 870; Window.Height = 700; Window.UpdateLayout(); await ScreenshotAsync("ai-sessions-compact");
        Check(sessions.ActualWidth >= 500, "AI list and details fit a compact window");
        LocalizationService.Apply(LocalizationService.Chinese); sessions.ApplyLanguage(); await ScreenshotAsync("ai-sessions-zh");
        Check(((TextBlock)sessions.FindName("Heading")).Text == "AI 对话", "AI view supports Chinese labels");
        Check(selectAll.Content?.ToString() == "全选" && clearSelection.Content?.ToString() == "取消全选",
            "Bulk selection controls support Chinese labels");
        LocalizationService.Apply(LocalizationService.English); sessions.ApplyLanguage(); Window.Width = previousWidth;
        await CheckAiProgressAsync(sessions);
        var driveSettings = new AiSessionsSettingsWindow(settings, service, (GoogleDriveSessionCloud)Field("_aiDrive")!);
        driveSettings.Owner = Window; driveSettings.Show(); await Task.Delay(100);
        Check(!((System.Windows.Controls.Button)driveSettings.FindName("ConnectButton")).IsEnabled,
            "An app configuration is needed before real cloud authorization");
        Check(!((System.Windows.Controls.Button)driveSettings.FindName("TestButton")).IsEnabled, "Real cloud test requires independent Google authorization");
        driveSettings.Close();
        await Window.HandleActivationAsync(["--open", boardPath]); await WaitReadyAsync();
        Check(Field("_showAiSessions") is false && Field("CanvasView") is not null, "Board navigation restores canvas after AI library");
        Window.Close(); Check(!Window.IsVisible, "AI upgrade preserves close-to-tray behavior");
        Check(!((System.Windows.Threading.DispatcherTimer)typeof(AiSessionsView).GetField("_progressTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sessions)!).IsEnabled,
            "Hidden AI views stop polling progress to avoid background UI work");
    }

    private static async Task CheckAiProgressAsync(AiSessionsView original)
    {
        var root = Path.Combine(Output, "ProgressDemo"); Directory.CreateDirectory(root);
        var sample = Path.Combine(root, "sample.txt"); File.WriteAllText(sample, "A non-sensitive demonstration of acknowledged backup progress.");
        var catalog = new SessionCatalog(Path.Combine(root, "Archive"));
        var names = new[] { "Design references", "Research planning", "Earlier notes", "Ideas to revisit", "Long conversation export" };
        catalog.Merge(Enumerable.Range(0, names.Length).Select(i => new SessionDescriptor
        {
            Key = "Imported:progress-" + i, Source = "Imported", SourceId = "progress-" + i, Title = names[i],
            Project = "Pinboard Demo", Path = sample, UpdatedAt = DateTimeOffset.Now.AddMinutes(-i), Size = new FileInfo(sample).Length,
            ParentKey = i == 2 ? "Imported:progress-0" : null
        }));
        catalog.SetSelection(catalog.List().Select(x => x.Key), true);
        Complete("Imported:progress-0"); Complete("Imported:progress-1");
        catalog.SetStatus("Imported:progress-2", "Retry pending", "The demo connection was interrupted; no personal information is involved.");
        catalog.RetryJob("Imported:progress-2");
        var cloud = new ProgressDemoCloud(); var service = new SessionArchiveService(catalog, [new ProgressDemoSource()], cloud);
        var view = new AiSessionsView(service, () => { }, paused => service.Paused = paused);
        var host = (ContentControl)Window.FindName("CanvasHost"); host.Content = view;
        var backup = service.BackupAsync("Imported:progress-4");
        try
        {
            await cloud.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => Task.FromResult(view.IsLoaded), "Progress demonstration did not load"); await view.ReloadAsync();
            await WaitUntilAsync(() => Task.FromResult(((ProgressBar)view.FindName("OverallBackupProgress")).Value > 40), "Overall progress did not refresh");
            var overall = (ProgressBar)view.FindName("OverallBackupProgress");
            Check(overall.Value > 40 && overall.Value < 100, "Overall backup bar combines completed conversations and the active transfer");
            Check(((TextBlock)view.FindName("BackupCurrentLabel")).Text.Contains("Long conversation export"), "Current backup title and acknowledged bytes are visible");
            Check(((TextBlock)view.FindName("BackupProgressSummary")).Text.Contains("1 retrying"), "Retrying backups are shown separately from successful completion");
            await WaitUntilAsync(() => Task.FromResult(Math.Abs(overall.Value - service.GetProgress().Percentage) < .001), "UI did not settle at the acknowledged transfer percentage");
            await ScreenshotAsync("ai-backup-progress");
            var filter = (System.Windows.Controls.TextBox)view.FindName("FilterBox"); var before = overall.Value; filter.Text = "Ideas"; await Task.Delay(350);
            Check(Math.Abs(overall.Value - before) < .001, "List filters do not change global backup progress"); filter.Text = ""; await Task.Delay(350);
            ((System.Windows.Controls.Button)view.FindName("ProgressIssuesButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var issues = (ListBox)view.FindName("SessionList");
            Check(issues.Items.Cast<AiSessionsView.SessionRow>().Any(x => x.Key == "Imported:progress-2"), "View issues reveals a failing folded child conversation");
            Check(catalog.List().Count(x => x.Selected) == 5, "Viewing failures does not alter backup selection");
            Check(((DockPanel)view.FindName("IssueActions")).Visibility == Visibility.Visible, "Issue tools appear only in the focused issues view");
            var issueFilter = (ComboBox)view.FindName("IssueFilterBox");
            Check(issueFilter.Items.Count == 4, "Issues are separated into source, images and transfer categories");
            issueFilter.SelectedIndex = 3;
            Check(issues.Items.Cast<AiSessionsView.SessionRow>().Any(row => row.Key == "Imported:progress-2"), "Transfer filter includes the failed child without selecting its parent");
            issues.SelectedItem = issues.Items.Cast<AiSessionsView.SessionRow>().First(row => row.Key == "Imported:progress-2");
            await WaitUntilAsync(() => Task.FromResult(((TextBlock)view.FindName("DetailHelp")).Text.Contains("Automatically retries")), "Next retry time did not appear");
            Check(((System.Windows.Controls.Button)view.FindName("DetailRetryButton")).Visibility == Visibility.Visible, "Failed entry offers an individual retry action");
            ((System.Windows.Controls.TextBox)view.FindName("PathBox")).Text = "Documents / Pinboard Demo / Conversation export";
            await ScreenshotAsync("ai-backup-repair");
            ((System.Windows.Controls.Button)view.FindName("RetryIssuesButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await WaitUntilAsync(() => Task.FromResult(catalog.GetJob("Imported:progress-2")!.DueAt <= DateTimeOffset.UtcNow), "Filtered retry did not queue its failed entry");
            Check(catalog.GetJob("Imported:progress-0") is null && catalog.GetJob("Imported:progress-1") is null, "Retry shown issues preserves successful backups and their progress");
            issueFilter.SelectedIndex = 1;
            Check(issues.Items.Count == 0 && ((TextBlock)view.FindName("EmptyLabel")).Text.Contains("No issues"), "An empty issue category shows a specific empty state");
            issueFilter.SelectedIndex = 0; issues.SelectedItem = null;
            await ScreenshotAsync("ai-backup-issues");
            ((System.Windows.Controls.Button)view.FindName("ProgressIssuesButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var previousWidth = Window.Width; var previousHeight = Window.Height; Window.Width = 870; Window.Height = 700; Window.UpdateLayout();
            LocalizationService.Apply(LocalizationService.Chinese); view.ApplyLanguage(); await view.ReloadAsync();
            Check(((TextBlock)view.FindName("BackupProgressTitle")).Text.Contains("Google Drive"), "Backup progress supports Chinese presentation");
            await ScreenshotAsync("ai-backup-progress-zh");
            Check(((Border)view.FindName("BackupProgressPanel")).ActualWidth > 400, "Compact window keeps the full progress card readable");
            var reportText = SessionArchiveDiagnostics.Present(new(5, 3, 1, 0, [], 2048, 0, 0, 2, new(1, 3, 3, 0, 0, 0, 0, 0)), true);
            var reportWindow = new AiSessionReportWindow("备份状态与重复检查", reportText) { Owner = Window };
            reportWindow.Show(); await Task.Delay(120); reportWindow.UpdateLayout();
            Check(((System.Windows.Controls.TextBox)reportWindow.FindName("Report")).IsReadOnly, "Backup diagnostic report is read-only");
            Check(((System.Windows.Controls.TextBox)reportWindow.FindName("Report")).ActualHeight > 200, "Multiline diagnostic report fills the card instead of inheriting single-line field height");
            Check(reportText.Contains("同名不等于重复") && reportText.Contains("不等同于逐文件下载恢复验证"), "Diagnostic report explains same-title matches and metadata-check limits");
            var reportSurface = (FrameworkElement)reportWindow.Content;
            var reportBitmap = new RenderTargetBitmap((int)reportSurface.ActualWidth, (int)reportSurface.ActualHeight, 96, 96, PixelFormats.Pbgra32); reportBitmap.Render(reportSurface);
            var reportEncoder = new PngBitmapEncoder(); reportEncoder.Frames.Add(BitmapFrame.Create(reportBitmap));
            await using (var file = File.Create(Path.Combine(Output, "ai-backup-health.png"))) reportEncoder.Save(file);
            reportWindow.Close();
            Check(catalog.List().Count(entry => entry.Selected) == 5, "Viewing diagnostics leaves selections unchanged");
            service.Paused = true; await Task.Delay(650);
            Check(((TextBlock)view.FindName("BackupProgressTitle")).Text == "备份已暂停", "Pause state is explicit in the progress header");
            service.Paused = false;
            cloud.Release.TrySetResult(); await backup; catalog.FinishJob("Imported:progress-4");
            Complete("Imported:progress-2"); Complete("Imported:progress-3");
            await WaitUntilAsync(() => Task.FromResult(((TextBlock)view.FindName("BackupProgressTitle")).Text == "云端备份已完成"), "Completion banner did not refresh");
            Check(overall.Value == 100, "Only settled confirmed versions produce one hundred percent");
            await ScreenshotAsync("ai-backup-complete");
            LocalizationService.Apply(LocalizationService.English); Window.Width = previousWidth; Window.Height = previousHeight;
        }
        finally { cloud.Release.TrySetResult(); await backup; host.Content = original; await original.ReloadAsync(); }
        void Complete(string key)
        {
            var id = Guid.NewGuid().ToString("N");
            var manifest = new SessionManifest(1, id, catalog.Find(key)!.Descriptor, DateTimeOffset.UtcNow, []);
            catalog.AddVersion(manifest, Path.Combine(catalog.Root, id + ".json"));
            catalog.MarkUploaded(id, "demo-account", "demo-version-" + id); catalog.SetStatus(key, "Cloud backup"); catalog.FinishJob(key);
        }
    }
    private sealed class ProgressDemoSource : ISessionSourceAdapter
    {
        public string Name => "Imported";
        public Task<IReadOnlyList<SessionDescriptor>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SessionDescriptor>>([]);
        public async Task<IReadOnlyList<SnapshotFile>> SnapshotAsync(SessionDescriptor item, string staging, CancellationToken ct)
            => [await SessionSnapshot.CopyAsync(item.Path!, staging, "sample.txt", ct)];
    }
    private sealed class ProgressDemoCloud : ISessionCloud, ISessionTransferProgressSource
    {
        public bool IsConnected => true; public string? ConfirmedAccountId => "demo-account";
        public event Action<long, long>? TransferProgress;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DriveAccount> GetAccountAsync(CancellationToken ct) => Task.FromResult(new DriveAccount("demo-account", "Pinboard Demo", 100000000, 0));
        public async Task<string> UploadObjectAsync(string hash, string path, CancellationToken ct)
        {
            var length = new FileInfo(path).Length; TransferProgress?.Invoke(length / 2, length); Started.TrySetResult();
            await Release.Task.WaitAsync(ct); TransferProgress?.Invoke(length, length); return "demo-object";
        }
        public Task<string> UploadManifestAsync(SessionManifest manifest, string path, CancellationToken ct) => Task.FromResult("demo-manifest");
        public Task DownloadObjectAsync(string fileId, string destination, CancellationToken ct) => throw new NotSupportedException();
    }

    private static Task WaitForSelectionIdleAsync(AiSessionsView view) => WaitUntilAsync(() => Task.FromResult(
        typeof(AiSessionsView).GetField("_bulkSelectionBusy", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view) is false),
        "Bulk selection did not finish");

    private static async Task ClickSelectAllAsync(AiSessionsView view, bool accept)
    {
        var observed = false;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            var dialog = System.Windows.Application.Current.Windows.OfType<AiSessionSelectionWindow>().FirstOrDefault();
            if (dialog is null) return;
            timer.Stop(); observed = true;
            Check(((TextBlock)dialog.FindName("WarningLabel")).Text.Contains("sensitive content"), "Bulk selection warns about sensitive uploads");
            Check(((System.Windows.Controls.Button)dialog.FindName("CancelButton")).IsDefault, "Bulk backup defaults to cancel");
            dialog.UpdateLayout();
            var preview = new RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            preview.Render(dialog);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(preview));
            using (var target = File.Create(Path.Combine(Output, "ai-bulk-confirmation.png"))) encoder.Save(target);
            if (accept) ((System.Windows.Controls.Button)dialog.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            else dialog.Close();
        };
        timer.Start();
        try
        {
            ((System.Windows.Controls.Button)view.FindName("SelectAllButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await WaitUntilAsync(() => Task.FromResult(observed), "Bulk selection confirmation did not appear");
            await WaitForSelectionIdleAsync(view);
        }
        finally { timer.Stop(); }
    }

    private static async Task WaitReadyAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < deadline)
        {
            if (Field("_webReady") is true && Field("_canvasLoading") is false && Field("_activeDocument") is PinboardDocument document)
            {
                // Browser Ready can precede the asynchronous snapshot read. Require
                // an acknowledgement from the intended rendered document, not just flags.
                var result = await FlushAsync(document.FilePath);
                if (result.TryGetProperty("viewport", out _) && Field("_canvasLoading") is false) return;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("Canvas did not finish rendering.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _passed++;
        Console.WriteLine("PASS " + message);
    }

    private static async Task ScreenshotAsync(string name)
    {
        // Only this isolated demonstration window is changed. Never expose a local
        // machine path in public screenshots, or alter the installed user's library.
        ((System.Windows.Controls.TextBlock)Window.FindName("SidebarLibraryText")).Text = "Documents · Pinboard Demo";
        Window.UpdateLayout();
        var view = Field("CanvasView") as WebView2;
        BitmapFrame? canvas = null;
        await using var preview = new MemoryStream();
        if (view is not null)
        {
            await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, preview);
            preview.Position = 0;
            canvas = BitmapFrame.Create(preview, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        var surface = (FrameworkElement)Window.Content;
        var chrome = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        chrome.Render(surface);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(chrome, new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
            if (view is not null && canvas is not null)
            {
                var origin = view.TranslatePoint(new System.Windows.Point(), surface);
                drawing.DrawImage(canvas, new Rect(origin, new System.Windows.Size(view.ActualWidth, view.ActualHeight)));
            }
        }
        var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using var target = File.Create(Path.Combine(Output, name + ".png")); encoder.Save(target);
    }

    private static byte[] CreateDemoScreenshot(bool checklist, string? caption = null)
    {
        // Synthetic source material rendered by WPF, not a screenshot of personal data.
        const int width = 720, height = 500;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            System.Windows.Media.Brush Color(string hex) => new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(hex));
            void Label(string value, double x, double y, double size, string color = "#20232B", bool bold = false)
                => drawing.DrawText(new FormattedText(value, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal,
                    bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), size, Color(color), 1), new System.Windows.Point(x, y));
            drawing.DrawRectangle(Color("#F7F9FC"), null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(Color("#FFFFFF"), null, new Rect(0, 0, width, 66));
            Label("FIELD NOTES", 36, 23, 15, "#1674E8", true);
            Label(caption ?? "DEMO CONTENT", 548, 25, 12, "#8B94A5");
            Label(checklist ? "A calmer workspace" : "Small ideas, big picture.", 36, 99, 32, bold: true);
            Label(checklist ? "Keep the useful parts. Let the rest go." : "Collect the things you want to come back to.", 36, 151, 20, "#6A7485");
            drawing.DrawRoundedRectangle(Color("#FFFFFF"), new Pen(Color("#E5EAF2"), 1), new Rect(36, 207, 648, 249), 18, 18);
            if (checklist)
            {
                var lines = new[] { "Save a screenshot before closing the tab", "Add a short note while the idea is fresh", "Group references for the next project", "Find it later with a keyword" };
                for (var i = 0; i < lines.Length; i++)
                {
                    var y = 236 + i * 51;
                    drawing.DrawRoundedRectangle(Color("#EAF3FF"), null, new Rect(61, y, 26, 26), 7, 7);
                    Label((i + 1).ToString(), 69, y + 2, 15, "#1674E8", true);
                    Label(lines[i], 105, y + 1, 18);
                }
            }
            else
            {
                Label("IDEAS COLLECTED", 62, 229, 13, "#8590A2", true);
                var heights = new[] { 45, 80, 62, 118, 93, 142 };
                for (var i = 0; i < heights.Length; i++)
                {
                    drawing.DrawRoundedRectangle(Color(i == 5 ? "#007AFF" : "#BFDFFF"), null,
                        new Rect(67 + 99 * i, 408 - heights[i], 55, heights[i]), 8, 8);
                    Label(new[] { "MON", "TUE", "WED", "THU", "FRI", "SAT" }[i], 79 + 99 * i, 420, 11, "#8590A2");
                }
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
}
