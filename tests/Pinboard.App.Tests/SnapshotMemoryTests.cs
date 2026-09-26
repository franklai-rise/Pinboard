using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pinboard.App.Models;
using Pinboard.App.Services;

namespace Pinboard.App.Tests;

[TestClass]
public sealed class SnapshotMemoryTests
{
    [TestMethod]
    public async Task Snapshot_LoadsOnlyLiveImagesWhileKeepingCheckpointAndDeletedAssetsRecoverable()
    {
        using var temp = new TemporaryDirectory();
        var document = new PinboardDocument(Path.Combine(temp.Path, "images.pinboard"));
        var history = CreateAsset(1);
        var current = CreateAsset(2);
        var deleted = CreateAsset(3);
        var orphan = CreateAsset(4);
        var historyScene = CreateScene(("history", false));
        var historyRevision = await document.SaveSceneAsync(historyScene,
            [("history", history)], checkpoint: true, checkpointReason: "history");
        var currentScene = CreateScene(("current", false), ("deleted", true), ("current", false));
        await document.SaveSceneAsync(currentScene,
            [("current", current), ("deleted", deleted), ("orphan", orphan)]);

        var snapshot = await document.LoadSnapshotAsync();

        Assert.AreEqual(currentScene, snapshot.SceneJson);
        Assert.AreEqual(1, snapshot.Files.Count);
        Assert.AreEqual("current", snapshot.Files[0].FileId);
        CollectionAssert.AreEqual(current.Bytes, ImageProcessor.DecodeDataUrl(snapshot.Files[0].DataUrl).Bytes);
        Assert.AreEqual(4L, await CountAssetsAsync(document.FilePath));

        await document.RestoreCheckpointAsync(historyRevision);
        var restored = await document.LoadSnapshotAsync();
        Assert.AreEqual(historyScene, restored.SceneJson);
        Assert.AreEqual(1, restored.Files.Count);
        Assert.AreEqual("history", restored.Files[0].FileId);
        CollectionAssert.AreEqual(history.Bytes, ImageProcessor.DecodeDataUrl(restored.Files[0].DataUrl).Bytes);

        // A deleted image can become live again without having to reinsert its BLOB.
        await document.SaveSceneAsync(CreateScene(("deleted", false)), []);
        Assert.AreEqual("deleted", (await document.LoadSnapshotAsync()).Files.Single().FileId);
        Assert.AreEqual(4L, await CountAssetsAsync(document.FilePath));
    }

    [TestMethod]
    public async Task SceneOnlySnapshot_PreservesRevisionAndSceneWithoutMaterializingImages()
    {
        using var temp = new TemporaryDirectory();
        var document = new PinboardDocument(Path.Combine(temp.Path, "scene-only.pinboard"));
        await document.SaveSceneAsync(CreateScene(("visible", false)), [("visible", CreateAsset(9))]);

        var complete = await document.LoadSnapshotAsync();
        var sceneOnly = await document.LoadSnapshotAsync(includeFiles: false);

        Assert.AreEqual(1, complete.Files.Count);
        Assert.AreEqual(0, sceneOnly.Files.Count);
        Assert.AreEqual(complete.SceneJson, sceneOnly.SceneJson);
        Assert.AreEqual(complete.Revision, sceneOnly.Revision);
        Assert.AreEqual(complete.Title, sceneOnly.Title);
        Assert.AreEqual(complete.Path, sceneOnly.Path);
    }

    [TestMethod]
    public async Task HashLookup_ReturnsAllAliasesWithoutLoadingImagePayloads()
    {
        using var temp = new TemporaryDirectory();
        var document = new PinboardDocument(Path.Combine(temp.Path, "lookup.pinboard"));
        var sharedAsset = CreateAsset(6);
        await document.SaveSceneAsync(CreateScene(("first", false), ("second", false), ("other", false)),
            [("first", sharedAsset), ("second", sharedAsset), ("other", CreateAsset(7))]);

        var fileIds = await document.FindFileIdsByHashAsync(sharedAsset.Hash.ToLowerInvariant());

        CollectionAssert.AreEquivalent(new[] { "first", "second" }, fileIds.ToArray());
        Assert.AreEqual(0, (await document.FindFileIdsByHashAsync(new string('0', 64))).Count);
    }

    [TestMethod]
    public async Task EmptyScene_DoesNotLoadImagesRetainedFromEarlierScenes()
    {
        using var temp = new TemporaryDirectory();
        var document = new PinboardDocument(Path.Combine(temp.Path, "empty.pinboard"));
        await document.SaveSceneAsync(CreateScene(("old", false)), [("old", CreateAsset(5))]);
        await document.SaveSceneAsync(CreateScene(), []);

        Assert.AreEqual(0, (await document.LoadSnapshotAsync()).Files.Count);
        Assert.AreEqual(1L, await CountAssetsAsync(document.FilePath));
    }

    private static AssetRecord CreateAsset(byte marker)
    {
        byte[] bytes = [137, 80, 78, 71, marker];
        return new AssetRecord(Convert.ToHexString(SHA256.HashData(bytes)), "image/png", 1, 1, bytes, DateTimeOffset.UtcNow);
    }

    private static string CreateScene(params (string FileId, bool Deleted)[] images) =>
        JsonSerializer.Serialize(new
        {
            type = "excalidraw",
            version = 2,
            source = "pinboard-memory-test",
            elements = images.Select((image, index) => new
            {
                id = $"image-{index}",
                type = "image",
                fileId = image.FileId,
                isDeleted = image.Deleted,
                x = index * 100,
                y = 0,
                width = 50,
                height = 50
            }),
            appState = new { viewBackgroundColor = "#ffffff" },
            files = new { }
        });

    private static async Task<long> CountAssetsAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM assets";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PinboardSnapshotTests", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
