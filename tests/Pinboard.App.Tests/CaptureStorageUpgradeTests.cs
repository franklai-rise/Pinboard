using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pinboard.App.Models;
using Pinboard.App.Services;

namespace Pinboard.App.Tests;

[TestClass]
public sealed class CaptureStorageUpgradeTests
{
    [TestMethod]
    public async Task CaptureTimestamp_PreservesTheClipboardEventTimeForImageAndText()
    {
        using var temp = new TemporaryDirectory();
        var document = new PinboardDocument(Path.Combine(temp.Path, "timestamp.pinboard"));
        var capturedAt = new DateTimeOffset(2026, 9, 30, 23, 59, 58, TimeSpan.FromHours(8));
        var image = await document.InsertCapturedImageAsync(CreateAsset(1), "pixpin", capturedAt: capturedAt);
        var text = await document.InsertCapturedTextAsync("Copied before midnight", "clipboard", capturedAt: capturedAt);

        using var scene = JsonDocument.Parse(text.SceneJson);
        foreach (var element in scene.RootElement.GetProperty("elements").EnumerateArray()
                     .Where(element => element.GetProperty("id").GetString() == image.ElementId ||
                                       element.GetProperty("id").GetString() == text.ElementId))
        {
            Assert.AreEqual(capturedAt.ToString("O"), element.GetProperty("customData").GetProperty("pinboard").GetProperty("capturedAt").GetString());
            Assert.AreEqual(capturedAt.ToUnixTimeMilliseconds(), element.GetProperty("updated").GetInt64());
        }
        var snapshot = await document.LoadSnapshotAsync();
        Assert.AreEqual(capturedAt.ToUnixTimeMilliseconds(), snapshot.Files.Single().CreatedAt);
    }

    [TestMethod]
    public async Task DeltaSnapshot_SendsOnlyUnacknowledgedLiveImagePayloads()
    {
        using var temp = new TemporaryDirectory();
        var document = new PinboardDocument(Path.Combine(temp.Path, "delta.pinboard"));
        var first = await document.InsertCapturedImageAsync(CreateAsset(1), "test");
        var acknowledged = new HashSet<string>(StringComparer.Ordinal) { first.FileId, "unused-file" };
        var second = await document.InsertCapturedImageAsync(CreateAsset(2), "test");
        var complete = await document.LoadSnapshotAsync();
        var delta = await document.LoadSnapshotAsync(includeFiles: true, acknowledgedFileIds: acknowledged);

        Assert.AreEqual(complete.SceneJson, delta.SceneJson);
        Assert.AreEqual(complete.Revision, delta.Revision);
        Assert.AreEqual(2, complete.Files.Count);
        Assert.AreEqual(second.FileId, delta.Files.Single().FileId);
        acknowledged.Add(second.FileId);
        Assert.AreEqual(0, (await document.LoadSnapshotAsync(includeFiles: true, acknowledgedFileIds: acknowledged)).Files.Count);
        Assert.AreEqual(2, (await document.LoadSnapshotAsync()).Files.Count);
    }

    [TestMethod]
    public async Task CaptureReceipt_PreventsCrashReplayDuplicatesButKeepsIndependentCopies()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "receipt.pinboard");
        var document = new PinboardDocument(path);
        var asset = CreateAsset(3);
        var first = await document.InsertCapturedImageAsync(asset, "test", captureId: "image-event-1");
        var firstRevision = await document.GetCurrentRevisionAsync();
        var replay = await new PinboardDocument(path).InsertCapturedImageAsync(asset, "test", captureId: "image-event-1");
        Assert.AreEqual(first.ElementId, replay.ElementId);
        Assert.AreEqual(first.Y, replay.Y);
        Assert.AreEqual(firstRevision, await document.GetCurrentRevisionAsync());

        var copiedAgain = await document.InsertCapturedImageAsync(asset, "test", captureId: "image-event-2");
        Assert.AreNotEqual(first.ElementId, copiedAgain.ElementId);
        var text = await document.InsertCapturedTextAsync("Repeated text remains intentional", "clipboard", captureId: "text-event-1");
        var secondText = await document.InsertCapturedTextAsync("Repeated text remains intentional", "clipboard", captureId: "text-event-2");
        var lastRevision = await document.GetCurrentRevisionAsync();
        var replayText = await new PinboardDocument(path).InsertCapturedTextAsync("Repeated text remains intentional", "clipboard", captureId: "text-event-1");

        Assert.AreEqual(text.ElementId, replayText.ElementId);
        Assert.AreNotEqual(text.ElementId, secondText.ElementId);
        Assert.AreEqual(lastRevision, await document.GetCurrentRevisionAsync());
        Assert.AreEqual((await document.LoadSnapshotAsync()).SceneJson, replayText.SceneJson);
        Assert.AreEqual(2, (await document.SearchAsync("intentional")).Count);
        using var parsed = JsonDocument.Parse(replayText.SceneJson);
        Assert.AreEqual(2, parsed.RootElement.GetProperty("elements").EnumerateArray().Count(element => element.GetProperty("type").GetString() == "image"));
    }

    [TestMethod]
    public async Task AppendingText_PreservesExistingSearchRowsAndIndexesBothNewTextElements()
    {
        using var temp = new TemporaryDirectory();
        var document = new PinboardDocument(Path.Combine(temp.Path, "search-append.pinboard"));
        var first = await document.InsertCapturedTextAsync("First distinctive original text", "clipboard");
        long originalRowId;
        await using (var connection = new SqliteConnection($"Data Source={document.FilePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM search_content WHERE ref_id=$id";
            command.Parameters.AddWithValue("$id", first.ElementId);
            originalRowId = Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        var second = await document.InsertCapturedTextAsync("Second freshly captured text", "clipboard");
        Assert.AreEqual(second.ElementId, (await document.SearchAsync("freshly")).Single().ReferenceId);
        Assert.AreEqual(first.ElementId, (await document.SearchAsync("distinctive")).Single().ReferenceId);
        await using var check = new SqliteConnection($"Data Source={document.FilePath};Pooling=False");
        await check.OpenAsync();
        await using var count = check.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM search_content WHERE kind='text'";
        Assert.AreEqual(4L, await count.ExecuteScalarAsync());
        count.CommandText = "SELECT id FROM search_content WHERE ref_id=$id";
        count.Parameters.AddWithValue("$id", first.ElementId);
        Assert.AreEqual(originalRowId, await count.ExecuteScalarAsync());
    }

    [TestMethod]
    public async Task RoutineSceneAndCheckpoint_ReuseIdenticalBrotliPayloads()
    {
        using var temp = new TemporaryDirectory();
        var document = new PinboardDocument(Path.Combine(temp.Path, "checkpoint.pinboard"));
        await document.InsertCapturedTextAsync(string.Concat(Enumerable.Repeat("Searchable text ", 1000)), "clipboard");
        var revision = await document.GetCurrentRevisionAsync();
        await using var connection = new SqliteConnection($"Data Source={document.FilePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT c.scene_data, r.scene_data FROM scene_current c JOIN scene_revisions r ON r.revision=c.revision WHERE c.revision=$revision";
        command.Parameters.AddWithValue("$revision", revision);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        CollectionAssert.AreEqual((byte[])reader[0], (byte[])reader[1]);
        Assert.AreEqual((await document.LoadSnapshotAsync()).SceneJson, await document.LoadCheckpointSceneAsync(revision));
    }

    private static AssetRecord CreateAsset(byte marker)
    {
        byte[] bytes = [137, 80, 78, 71, marker];
        return new AssetRecord(Convert.ToHexString(SHA256.HashData(bytes)), "image/png", 100, 80, bytes, DateTimeOffset.UtcNow);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PinboardCaptureStorageTests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
