using System.Text.Json;
using LZStringCSharp;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pinboard.App.Models;
using Pinboard.App.Services;
using SkiaSharp;

namespace Pinboard.App.Tests;

[TestClass]
public sealed class CoreTests
{
    private static bool Overlaps(InsertResult first, InsertResult second) =>
        first.X < second.X + second.Width &&
        first.X + first.Width > second.X &&
        first.Y < second.Y + second.Height &&
        first.Y + first.Height > second.Y;

    [TestMethod]
    public void AppSettings_DefaultLanguageIsEnglishAndInvalidValuesNormalizeToEnglish()
    {
        var defaults = new AppSettings();
        Assert.AreEqual(LocalizationService.English, defaults.Language);
        Assert.IsTrue(defaults.StartAtLogin);
        Assert.AreEqual(LocalizationService.English, LocalizationService.Normalize(null));
        Assert.AreEqual(LocalizationService.English, LocalizationService.Normalize("fr"));
        Assert.AreEqual(LocalizationService.Chinese, LocalizationService.Normalize("ZH-cn"));
    }

    [TestMethod]
    public void AppSettings_SwitchesBetweenFixedAndMonthlyCaptureTargets()
    {
        using var temp = new TemporaryDirectory();
        var fixedTarget = Path.Combine(temp.Path, "Research", "paper-notes.pinboard");
        var settings = new AppSettings
        {
            LibraryPath = temp.Path,
            FixedCaptureTarget = fixedTarget
        };
        var date = new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero);

        Assert.AreEqual(Path.GetFullPath(fixedTarget), settings.ResolveCaptureTarget(date));

        settings.FixedCaptureTarget = null;
        Assert.AreEqual(Path.Combine(temp.Path, "2026-08.pinboard"), settings.ResolveCaptureTarget(date));
    }

    [TestMethod]
    public void AppSettings_UsesOneDedicatedTextClipsBoard()
    {
        using var temp = new TemporaryDirectory();
        var settings = new AppSettings { LibraryPath = temp.Path };

        Assert.IsFalse(settings.TextCaptureEnabled);
        Assert.AreEqual(
            Path.Combine(temp.Path, AppSettings.TextClipsBoardFileName),
            settings.ResolveTextCaptureTarget());
    }

    [TestMethod]
    public void AppSettings_LegacySettingsKeepTextCaptureDisabledByDefault()
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>("{\"LibraryPath\":\"Pinboards\"}");

        Assert.IsNotNull(legacy);
        Assert.IsFalse(legacy.TextCaptureEnabled);
    }

    [TestMethod]
    public void PixPinDefaultShortcutListener_StartsWithoutTakingOverTheShortcut()
    {
        using var listener = new PixPinDefaultShortcutListener();

        listener.Start();

        Assert.IsTrue(listener.IsListening);
    }

    [TestMethod]
    public void ImageProcessor_ConvertsPngToWebpAndPreservesDimensions()
    {
        var source = CreatePng(64, 32, transparent: true);
        var asset = new ImageProcessor().Process(source, 80);

        Assert.AreEqual("image/webp", asset.MimeType);
        Assert.AreEqual(64, asset.Width);
        Assert.AreEqual(32, asset.Height);
        Assert.AreEqual("image/webp", ImageProcessor.DetectMimeType(asset.Bytes));
        Assert.AreEqual(64, asset.Hash.Length);
    }

    [TestMethod]
    public void SceneBuilder_AppendsImagesAtRequestedPositionAndCapsWidth()
    {
        var asset = new AssetRecord("a".PadLeft(64, '0'), "image/webp", 2240, 1120, [1], DateTimeOffset.UtcNow);
        var result = SceneBuilder.AppendImage(SceneBuilder.CreateDefaultScene(), "file-1", asset, 80, 144, "test");

        Assert.AreEqual(80, result.X);
        Assert.AreEqual(144, result.Y);
        Assert.AreEqual(1120, result.Width);
        Assert.AreEqual(560, result.Height);
        using var scene = JsonDocument.Parse(result.SceneJson);
        Assert.AreEqual(2, scene.RootElement.GetProperty("elements").GetArrayLength());
    }

    [TestMethod]
    public void SceneBuilder_AppendsCopiedTextAsOneNeatGroupedCard()
    {
        const string copied = "第一行记录\nSecond line of copied text";
        var result = SceneBuilder.AppendTextClip(SceneBuilder.CreateTextClipsScene(), copied, 80, 120, "clipboard");

        Assert.AreEqual(80d, result.X);
        Assert.AreEqual(120d, result.Y);
        Assert.IsTrue(result.Height >= 116d);
        using var scene = JsonDocument.Parse(result.SceneJson);
        var elements = scene.RootElement.GetProperty("elements");
        Assert.AreEqual(4, elements.GetArrayLength());
        Assert.AreEqual("rectangle", elements[1].GetProperty("type").GetString());
        Assert.AreEqual("text", elements[3].GetProperty("type").GetString());
        Assert.AreEqual(copied, elements[3].GetProperty("originalText").GetString());
        Assert.AreEqual("text-clip", elements[3].GetProperty("customData").GetProperty("pinboard").GetProperty("role").GetString());
        Assert.AreEqual(
            elements[1].GetProperty("groupIds")[0].GetString(),
            elements[3].GetProperty("groupIds")[0].GetString());
    }

    [TestMethod]
    public void ObsidianDecoder_RoundTripsCompressedSceneAndEmbeddedFiles()
    {
        const string scene = "{\"type\":\"excalidraw\",\"version\":2,\"elements\":[],\"appState\":{},\"files\":{}}";
        var markdown = $"""
            # Test

            ## Drawing
            ```compressed-json
            {LZString.CompressToBase64(scene)}
            ```

            ## Embedded Files
            abc123: [[sample.png]]
            placeholder: markdown-image
            """;

        Assert.AreEqual(scene, ObsidianImporter.DecodeScene(markdown));
        var mappings = ObsidianImporter.ParseEmbeddedFiles(markdown);
        Assert.AreEqual("sample.png", mappings["abc123"]);
        Assert.AreEqual("markdown-image", mappings["placeholder"]);
    }

    [TestMethod]
    public async Task Document_PersistsSceneDeduplicatesAssetsSearchesAndCopies()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "2026-08.pinboard");
        var copy = Path.Combine(temp.Path, "copy.pinboard");
        var document = new PinboardDocument(path);
        await document.InitializeAsync();
        var processor = new ImageProcessor();
        var asset = processor.Process(CreatePng(200, 100), 80);

        var first = await document.InsertCapturedImageAsync(asset, "test");
        var second = await document.InsertCapturedImageAsync(asset, "test");
        Assert.AreEqual(first.Y, second.Y);
        Assert.AreEqual(1184d, second.X - first.X);

        var scene = await document.LoadSnapshotAsync();
        var sceneNode = JsonDocument.Parse(scene.SceneJson);
        Assert.AreEqual(3, sceneNode.RootElement.GetProperty("elements").GetArrayLength());
        Assert.AreEqual(1, scene.Files.Count);

        var withText = scene.SceneJson.Replace(
            "] ,\"appState\"",
            "] ,\"appState\"",
            StringComparison.Ordinal);
        var root = System.Text.Json.Nodes.JsonNode.Parse(scene.SceneJson)!.AsObject();
        root["elements"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse("""
            {"id":"note-1","type":"text","x":900,"y":200,"width":160,"height":25,"angle":0,"strokeColor":"#000000","backgroundColor":"transparent","fillStyle":"solid","strokeWidth":2,"strokeStyle":"solid","roughness":0,"opacity":100,"groupIds":[],"frameId":null,"roundness":null,"seed":1,"version":1,"versionNonce":2,"isDeleted":false,"boundElements":null,"updated":1,"link":null,"locked":false,"text":"推进器故障记录","fontSize":20,"fontFamily":5,"textAlign":"left","verticalAlign":"top","containerId":null,"originalText":"推进器故障记录","autoResize":true,"lineHeight":1.25}
            """));
        await document.SaveSceneAsync(root.ToJsonString(), []);
        var hits = await document.SearchAsync("推进器");
        Assert.AreEqual(1, hits.Count);
        Assert.AreEqual("note-1", hits[0].ReferenceId);

        await document.SaveCopyAsync(copy);
        File.Delete(path);
        var copied = await new PinboardDocument(copy).LoadSnapshotAsync();
        Assert.AreEqual(1, copied.Files.Count);

        await using var connection = new SqliteConnection($"Data Source={copy}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM assets";
        Assert.AreEqual(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [TestMethod]
    public async Task Document_PlacementKeepsOneHundredScreenshotsSeparate()
    {
        using var temp = new TemporaryDirectory();
        var document = new PinboardDocument(Path.Combine(temp.Path, "many.pinboard"));
        var asset = new ImageProcessor().Process(CreatePng(32, 24), 80);
        var insertedItems = new List<InsertResult>();

        for (var index = 0; index < 100; index++)
        {
            var inserted = await document.InsertCapturedImageAsync(asset, "test");
            insertedItems.Add(inserted);
        }

        Assert.AreEqual(2, insertedItems.Select(item => item.X).Distinct().Count());
        for (var left = 0; left < insertedItems.Count; left++)
        {
            for (var right = left + 1; right < insertedItems.Count; right++)
            {
                Assert.IsFalse(Overlaps(insertedItems[left], insertedItems[right]),
                    $"Screenshot {left} overlaps screenshot {right}.");
            }
        }

        var snapshot = await document.LoadSnapshotAsync();
        using var json = JsonDocument.Parse(snapshot.SceneJson);
        Assert.AreEqual(101, json.RootElement.GetProperty("elements").GetArrayLength());
        Assert.AreEqual(1, snapshot.Files.Count);
    }

    [TestMethod]
    public async Task Document_UsesLegacySingleColumnCursorAsTheTwoColumnStartingPoint()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "layout-legacy.pinboard");
        var document = new PinboardDocument(path);
        await document.InitializeAsync();
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE meta SET value='1440' WHERE key='inbox_cursor_y'";
            await command.ExecuteNonQueryAsync();
        }

        var asset = new ImageProcessor().Process(CreatePng(200, 100), 80);
        var first = await document.InsertCapturedImageAsync(asset, "test");
        var second = await document.InsertCapturedImageAsync(asset, "test");

        Assert.AreEqual(80d, first.X);
        Assert.AreEqual(1440d, first.Y);
        Assert.AreEqual(1264d, second.X);
        Assert.AreEqual(1440d, second.Y);
    }

    [TestMethod]
    public async Task Document_TextClipsAreStackedAndSearchable()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, AppSettings.TextClipsBoardFileName);
        var document = new PinboardDocument(path);

        var first = await document.InsertCapturedTextAsync("First copied note with a distinctive phrase.", "clipboard");
        var second = await document.InsertCapturedTextAsync("Second copied note.", "clipboard");

        Assert.AreEqual(first.X, second.X);
        Assert.IsTrue(second.Y >= first.Y + first.Height + 28d);
        var snapshot = await document.LoadSnapshotAsync();
        using var scene = JsonDocument.Parse(snapshot.SceneJson);
        Assert.AreEqual(7, scene.RootElement.GetProperty("elements").GetArrayLength());
        var hits = await document.SearchAsync("distinctive phrase");
        Assert.AreEqual(1, hits.Count);
        Assert.AreEqual(first.ElementId, hits[0].ReferenceId);
    }

    [TestMethod]
    public async Task Document_DisplayTitlePersistsWithoutRenamingTheActualFile()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "actual-file-name.pinboard");
        var document = new PinboardDocument(path);
        await document.InitializeAsync();

        await document.RenameDisplayTitleAsync("Research Screenshots");

        Assert.AreEqual(path, document.FilePath);
        Assert.AreEqual("actual-file-name", document.FileName);
        Assert.AreEqual("Research Screenshots", document.Title);
        Assert.IsTrue(File.Exists(path));
        Assert.IsFalse(File.Exists(Path.Combine(temp.Path, "Research Screenshots.pinboard")));
        Assert.AreEqual("Research Screenshots", PinboardDocument.ReadDisplayTitle(path));

        var reopened = new PinboardDocument(path);
        var snapshot = await reopened.LoadSnapshotAsync();
        Assert.AreEqual("Research Screenshots", reopened.Title);
        Assert.AreEqual("Research Screenshots", snapshot.Title);
    }

    [TestMethod]
    public void EmbeddedCanvasPackage_ContainsOfflineEntryPoint()
    {
        var resources = typeof(EmbeddedWebAssets).Assembly.GetManifestResourceNames();
        CollectionAssert.Contains(resources, "Pinboard.Web/index.html");
        Assert.IsTrue(resources.Any(name => name.StartsWith("Pinboard.Web/assets", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Ocr_DoesNotCloseItsImageStreamBeforeRecognition()
    {
        var results = await new OcrService().RecognizeAsync(CreatePng(120, 50), chinese: false, english: true);
        Assert.AreEqual(1, results.Count);
        Assert.AreNotEqual("failed", results[0].Status, results[0].Error);
    }

    [TestMethod]
    public async Task Document_ReadOnlyFileCanOpenButCannotBeOverwritten()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "readonly.pinboard");
        await new PinboardDocument(path).InitializeAsync();
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        try
        {
            var document = new PinboardDocument(path);
            var snapshot = await document.LoadSnapshotAsync();
            Assert.IsTrue(document.IsReadOnly);
            Assert.IsFalse(string.IsNullOrWhiteSpace(snapshot.SceneJson));
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => document.SaveSceneAsync(snapshot.SceneJson, []));
        }
        finally
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }
    }

    [TestMethod]
    public async Task Document_RetainsOnlyLatestTwentyCheckpoints()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "checkpoints.pinboard");
        var document = new PinboardDocument(path);
        await document.InitializeAsync();
        var scene = (await document.LoadSnapshotAsync()).SceneJson;
        for (var index = 0; index < 25; index++)
        {
            await document.SaveSceneAsync(scene, [], checkpoint: true, checkpointReason: "test");
        }
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM scene_revisions";
        Assert.AreEqual(20L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [TestMethod]
    public async Task SearchLibrary_FindsBoardsInsideProjectFolders()
    {
        using var temp = new TemporaryDirectory();
        var projectDirectory = Path.Combine(temp.Path, "船舶项目");
        Directory.CreateDirectory(projectDirectory);
        var document = new PinboardDocument(Path.Combine(projectDirectory, "讨论.pinboard"));
        await document.InitializeAsync();
        var snapshot = await document.LoadSnapshotAsync();
        var root = System.Text.Json.Nodes.JsonNode.Parse(snapshot.SceneJson)!.AsObject();
        root["elements"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse("""
            {"id":"project-note","type":"text","x":0,"y":0,"width":160,"height":25,"angle":0,"strokeColor":"#000000","backgroundColor":"transparent","fillStyle":"solid","strokeWidth":2,"strokeStyle":"solid","roughness":0,"opacity":100,"groupIds":[],"frameId":null,"roundness":null,"seed":1,"version":1,"versionNonce":2,"isDeleted":false,"boundElements":null,"updated":1,"link":null,"locked":false,"text":"系泊系统项目记录","fontSize":20,"fontFamily":5,"textAlign":"left","verticalAlign":"top","containerId":null,"originalText":"系泊系统项目记录","autoResize":true,"lineHeight":1.25}
            """));
        await document.SaveSceneAsync(root.ToJsonString(), []);

        var hits = await new SearchService().SearchLibraryAsync(temp.Path, "系泊系统");

        Assert.AreEqual(1, hits.Count);
        Assert.AreEqual(document.FilePath, hits[0].DocumentPath);
    }

    [TestMethod]
    public async Task ProjectLibrary_CreatesEmptyProjectAndMovesBoardsWithoutOverwriting()
    {
        using var temp = new TemporaryDirectory();
        var library = new ProjectLibraryService(temp.Path);

        var project = library.CreateProject("海洋工程");
        Assert.AreEqual("海洋工程", project);
        Assert.IsTrue(Directory.Exists(Path.Combine(temp.Path, project)));
        CollectionAssert.Contains(library.GetProjectNames().ToList(), project);

        var sourcePath = library.CreateBoardPath("会议截图", ProjectLibraryService.DefaultProjectName);
        await new PinboardDocument(sourcePath).InitializeAsync();
        var movedPath = library.MoveBoard(sourcePath, project);
        Assert.IsFalse(File.Exists(sourcePath));
        Assert.IsTrue(File.Exists(movedPath));
        Assert.AreEqual(project, library.GetProjectName(movedPath));

        var nextPath = library.CreateBoardPath("会议截图", project);
        Assert.AreNotEqual(movedPath, nextPath);
        StringAssert.Contains(Path.GetFileName(nextPath), "(2)");
        Assert.IsFalse(new AppSettings().AlwaysOnTop);
    }

    [TestMethod]
    public async Task ProjectLibrary_ArchivesAndRestoresBoardWithoutChangingDisplayName()
    {
        using var temp = new TemporaryDirectory();
        var library = new ProjectLibraryService(temp.Path);
        var sourcePath = library.CreateBoardPath("original-file-name", ProjectLibraryService.DefaultProjectName);
        var document = new PinboardDocument(sourcePath);
        await document.InitializeAsync();
        await document.RenameDisplayTitleAsync("Keep this display name");

        var archivePath = library.ArchiveBoard(sourcePath);

        Assert.IsFalse(File.Exists(sourcePath));
        Assert.IsTrue(File.Exists(archivePath));
        Assert.AreEqual(ProjectLibraryService.ArchiveProjectName, library.GetProjectName(archivePath));
        Assert.AreEqual("Keep this display name", PinboardDocument.ReadDisplayTitle(archivePath));
        CollectionAssert.DoesNotContain(library.GetProjectNames().ToList(), ProjectLibraryService.ArchiveProjectName);
        Assert.IsNotNull(ProjectLibraryService.ValidateProjectName(ProjectLibraryService.ArchiveProjectName, allowDefaultProject: false));

        var restoredProject = library.CreateProject("Restored");
        var restoredPath = library.MoveBoard(archivePath, restoredProject);
        Assert.IsTrue(File.Exists(restoredPath));
        Assert.AreEqual(restoredProject, library.GetProjectName(restoredPath));
        Assert.AreEqual("Keep this display name", PinboardDocument.ReadDisplayTitle(restoredPath));
    }

    [TestMethod]
    public async Task StandardExcalidrawImport_EmbedsItsImageInOnePinboardFile()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "sample.excalidraw");
        var destination = Path.Combine(temp.Path, "sample.pinboard");
        var png = CreatePng(90, 60);
        var dataUrl = ImageProcessor.ToDataUrl("image/png", png);
        var scene = new
        {
            type = "excalidraw",
            version = 2,
            elements = new object[]
            {
                new { id = "image-1", type = "image", fileId = "file-1", x = 20, y = 30, width = 90, height = 60, isDeleted = false, status = "saved" }
            },
            appState = new { viewBackgroundColor = "#ffffff" },
            files = new Dictionary<string, object>
            {
                ["file-1"] = new { id = "file-1", dataURL = dataUrl, mimeType = "image/png", created = 1 }
            }
        };
        await File.WriteAllTextAsync(source, JsonSerializer.Serialize(scene));

        var importer = new ObsidianImporter(new ImageProcessor(), new OcrService());
        var report = await importer.ImportStandardAsync(source, destination, 80, false, false);
        Assert.AreEqual(1, report.ImageCount);
        var snapshot = await new PinboardDocument(report.DestinationPath).LoadSnapshotAsync();
        Assert.AreEqual(1, snapshot.Files.Count);
        Assert.AreEqual("image/webp", snapshot.Files[0].MimeType);
    }

    private static byte[] CreatePng(int width, int height, bool transparent = false)
    {
        using var bitmap = new SKBitmap(width, height, true);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(transparent ? new SKColor(25, 80, 160, 128) : new SKColor(25, 80, 160));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PinboardTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch
            {
                // Test cleanup must not hide the assertion result.
            }
        }
    }
}
