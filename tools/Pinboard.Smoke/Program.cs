using System.Diagnostics;
using System.IO;
using System.Reflection;
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

internal static class Program
{
    private static MainWindow Window = null!;
    private static string Output = null!;
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
            catch (Exception ex) { Console.Error.WriteLine(ex); File.WriteAllText(Path.Combine(Output, "failure.txt"), ex.ToString()); app.Shutdown(1); }
        };
        return app.Run();
    }

    private static async Task RunAsync()
    {
        var settings = new AppSettings
        {
            LibraryPath = Path.Combine(Output, "Library"), StoragePath = Path.Combine(Output, "settings.json"),
            StartAtLogin = false, TextCaptureEnabled = false, TextCaptureBoardMode = TextCaptureBoardMode.Daily,
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

        await Window.HandleActivationAsync(["--open", picturePath]);
        await WaitReadyAsync();
        await Task.Delay(500);
        await ScreenshotAsync("screenshot-inbox");

        await Window.HandleActivationAsync(["--open", textPath]);
        await WaitReadyAsync();
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
        await Task.Delay(600);
        Check(Field("CanvasView") is null, "Background text does not recreate WebView");
        Check((await text.SearchAsync("sleeps")).Count == 1, "Sleeping receiver saves and indexes text");
        await Window.HandleActivationAsync([]);
        await WaitReadyAsync();
        var after = await FlushAsync(textPath);
        Check(Math.Abs(after.GetProperty("viewport").GetProperty("scrollY").GetDouble() - expectedY) < 1, "Wake restores reading position");
        Check((await text.SearchAsync("sleeps")).Count == 1, "Wake retains background capture");
        // Simulate the host being ahead of the rendered board during a rapid switch.
        typeof(MainWindow).GetField("_activeDocument", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Window, pictures);
        Window.Close();
        await (Task)Call("ReleaseIdleCanvasAsync")!;
        Check(Field("CanvasView") is not null, "Skipped flush cannot discard a mismatched live canvas");
        typeof(MainWindow).GetField("_activeDocument", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Window, text);
        Window.Close();
        await (Task)Call("ReleaseIdleCanvasAsync")!;
        Check(Field("CanvasView") is null, "Second sleep releases recreated renderer");
        File.WriteAllText(Path.Combine(Output, "passed.txt"), "All desktop smoke checks passed.\n" + DateTimeOffset.Now.ToString("O"));
        Console.WriteLine("Output: " + Output);
        // Cleanly release only this harness's window/listeners, never the installed app.
        typeof(MainWindow).GetField("_explicitExit", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Window, true);
        Window.Close();
    }

    private static Task<JsonElement> FlushAsync(string document) =>
        (Task<JsonElement>)Call("RequestCanvasAsync", "Flush", new { }, document, TimeSpan.FromSeconds(15))!;

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
        Console.WriteLine("PASS " + message);
    }

    private static async Task ScreenshotAsync(string name)
    {
        // Only this isolated demonstration window is changed. Never expose a local
        // machine path in public screenshots, or alter the installed user's library.
        ((System.Windows.Controls.TextBlock)Window.FindName("SidebarLibraryText")).Text = "Documents · Pinboard Demo";
        Window.UpdateLayout();
        var view = (WebView2)Field("CanvasView")!;
        await using var preview = new MemoryStream();
        await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, preview);
        preview.Position = 0;
        var canvas = BitmapFrame.Create(preview, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var surface = (FrameworkElement)Window.Content;
        var chrome = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        chrome.Render(surface);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(chrome, new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
            var origin = view.TranslatePoint(new System.Windows.Point(), surface);
            drawing.DrawImage(canvas, new Rect(origin, new System.Windows.Size(view.ActualWidth, view.ActualHeight)));
        }
        var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using var target = File.Create(Path.Combine(Output, name + ".png")); encoder.Save(target);
    }

    private static byte[] CreateDemoScreenshot(bool checklist)
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
            Label("DEMO CONTENT", 548, 25, 12, "#8B94A5");
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
