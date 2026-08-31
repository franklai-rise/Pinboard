using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pinboard.App.Models;
using Pinboard.App.Services;

namespace Pinboard.App.Tests;

[TestClass]
public sealed class PinboardDocumentStorageTests
{
    [TestMethod]
    public async Task VersionOneDatabase_MigratesTransactionallyAndKeepsScenesReadable()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "legacy.pinboard");
        var scene = CreateTextScene("legacy-note", string.Concat(Enumerable.Repeat("legacy scene content ", 300)));
        await CreateVersionOneDatabaseAsync(path, scene);

        var document = new PinboardDocument(path);
        var snapshot = await document.LoadSnapshotAsync();

        Assert.AreEqual(scene, snapshot.SceneJson);
        Assert.AreEqual(scene, await document.LoadCheckpointSceneAsync(7));
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        Assert.AreEqual("2", await ScalarStringAsync(connection, "SELECT value FROM meta WHERE key='schema_version'"));
        Assert.AreEqual("blob", await ScalarStringAsync(connection, "SELECT typeof(scene_data) FROM scene_current WHERE id=1"));
        Assert.AreEqual("br-v1", await ScalarStringAsync(connection, "SELECT scene_encoding FROM scene_current WHERE id=1"));
        Assert.AreEqual(0L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('scene_current') WHERE name='scene_json'"));
    }

    [TestMethod]
    public async Task UnsupportedSchemaVersion_IsRejectedWithoutChangingIt()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "future.pinboard");
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL); INSERT INTO meta VALUES('schema_version', '99');";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new PinboardDocument(path).InitializeAsync());
        await using var check = new SqliteConnection($"Data Source={path}");
        await check.OpenAsync();
        Assert.AreEqual("99", await ScalarStringAsync(check, "SELECT value FROM meta WHERE key='schema_version'"));
    }

    [TestMethod]
    public async Task CurrentSceneAndCheckpoints_AreBrotliCompressedAndRoundTrip()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "compressed.pinboard");
        var document = new PinboardDocument(path);
        var scene = CreateTextScene("large-note", string.Concat(Enumerable.Repeat("compressible pinboard text ", 1000)));

        var revision = await document.SaveSceneAsync(scene, [], checkpoint: true, checkpointReason: "compression-test");
        var snapshot = await document.LoadSnapshotAsync();
        var checkpoints = await document.ListCheckpointsAsync();

        Assert.AreEqual(scene, snapshot.SceneJson);
        Assert.AreEqual(scene, await document.LoadCheckpointSceneAsync(revision));
        Assert.AreEqual(1, checkpoints.Count);
        Assert.IsTrue(checkpoints[0].StoredBytes < Encoding.UTF8.GetByteCount(scene) / 4);
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        Assert.AreEqual("blob", await ScalarStringAsync(connection, "SELECT typeof(scene_data) FROM scene_revisions LIMIT 1"));
    }

    [TestMethod]
    public async Task CheckpointCanBeListedLoadedAndRestoredAsANewRevision()
    {
        using var temp = new TemporaryDirectory();
        var document = new PinboardDocument(Path.Combine(temp.Path, "restore.pinboard"));
        var firstScene = CreateTextScene("first", "first checkpoint text");
        var secondScene = CreateTextScene("second", "second checkpoint text");
        var firstRevision = await document.SaveSceneAsync(firstScene, [], checkpoint: true, checkpointReason: "first");
        var secondRevision = await document.SaveSceneAsync(secondScene, [], checkpoint: true, checkpointReason: "second");

        var checkpoints = await document.ListCheckpointsAsync();
        CollectionAssert.AreEqual(new[] { secondRevision, firstRevision }, checkpoints.Select(item => item.Revision).ToArray());
        Assert.AreEqual(firstScene, await document.LoadCheckpointSceneAsync(firstRevision));

        var restoredRevision = await document.RestoreCheckpointAsync(firstRevision);
        Assert.IsTrue(restoredRevision > secondRevision);
        Assert.AreEqual(firstScene, (await document.LoadSnapshotAsync()).SceneJson);
        Assert.IsTrue((await document.SearchAsync("checkpoint")).Any(hit => hit.ReferenceId == "first"));
    }

    [TestMethod]
    public async Task ExpectedRevision_RejectsStaleSaveBeforeAssetsOrSceneAreWritten()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "revision-conflict.pinboard");
        var document = new PinboardDocument(path);
        await document.InitializeAsync();
        var initialRevision = await document.GetCurrentRevisionAsync();
        var acceptedScene = CreateTextScene("accepted", "accepted revision");
        var acceptedRevision = await document.SaveSceneAsync(acceptedScene, [], expectedRevision: initialRevision);
        var rejectedAsset = new AssetRecord(new string('f', 64), "image/png", 1, 1, [1, 2, 3], DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsExactlyAsync<RevisionConflictException>(() =>
            document.SaveSceneAsync(
                CreateImageScene("rejected-file"),
                [("rejected-file", rejectedAsset)],
                expectedRevision: initialRevision));

        Assert.AreEqual(initialRevision, exception.ExpectedRevision);
        Assert.AreEqual(acceptedRevision, exception.ActualRevision);
        Assert.AreEqual(acceptedRevision, await document.GetCurrentRevisionAsync());
        Assert.AreEqual(acceptedScene, (await document.LoadSnapshotAsync()).SceneJson);
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        Assert.AreEqual(0L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM assets WHERE hash='ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff'"));
    }

    [TestMethod]
    public async Task Search_UsesFtsForLongQueriesAndLikeForOneOrTwoCharacters()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "search.pinboard");
        var document = new PinboardDocument(path);
        await document.SaveSceneAsync(CreateTextScene("search-note", "推进器 distinctive phrase"), []);

        Assert.AreEqual(1, (await document.SearchAsync("推进器")).Count);
        Assert.AreEqual(1, (await document.SearchAsync("推进")).Count);

        // Long queries are served by the FTS table, independently of the LIKE fallback table.
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM search_content";
            await delete.ExecuteNonQueryAsync();
        }
        Assert.AreEqual(1, (await document.SearchAsync("distinctive")).Count);
        Assert.AreEqual(0, (await document.SearchAsync("推进")).Count);
    }

    [TestMethod]
    public async Task UnchangedTextSearchRows_AreNotRecreatedDuringIncrementalIndexing()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "incremental-index.pinboard");
        var document = new PinboardDocument(path);
        await document.SaveSceneAsync(CreateTextScene("stable", "stable searchable text"), []);
        long firstRowId;
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            firstRowId = await ScalarLongAsync(connection, "SELECT id FROM search_content WHERE ref_id='stable'");
        }

        await document.SaveSceneAsync(CreateTextScene(
            ("stable", "stable searchable text", 10d, 20d),
            ("new", "new searchable text", 30d, 40d)), []);

        await using var check = new SqliteConnection($"Data Source={path}");
        await check.OpenAsync();
        Assert.AreEqual(firstRowId, await ScalarLongAsync(check, "SELECT id FROM search_content WHERE ref_id='stable'"));
        Assert.AreEqual(2L, await ScalarLongAsync(check, "SELECT COUNT(*) FROM search_content WHERE kind='text'"));
    }

    [TestMethod]
    public async Task Optimize_RemovesOnlyUnreferencedAssetsAndProtectsCheckpointImages()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "gc.pinboard");
        var document = new PinboardDocument(path);
        var firstAsset = CreateAsset('a');
        var currentAsset = CreateAsset('b');
        var orphanAsset = CreateAsset('c');
        await document.SaveSceneAsync(CreateImageScene("history-file"), [("history-file", firstAsset)], checkpoint: true, checkpointReason: "history");
        await document.SaveSceneAsync(CreateImageScene("current-file"), [("current-file", currentAsset)], checkpoint: true, checkpointReason: "current");
        await document.SaveSceneAsync(CreateImageScene("current-file"), [("orphan-file", orphanAsset)]);

        var result = await document.OptimizeAsync(vacuum: true);

        Assert.AreEqual(1, result.RemovedFileMappings);
        Assert.AreEqual(1, result.RemovedAssets);
        Assert.IsTrue(result.Vacuumed);
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        Assert.AreEqual(2L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM assets"));
        Assert.AreEqual(1L, await ScalarLongAsync(connection, $"SELECT COUNT(*) FROM assets WHERE hash='{firstAsset.Hash}'"));
        Assert.AreEqual(1L, await ScalarLongAsync(connection, $"SELECT COUNT(*) FROM assets WHERE hash='{currentAsset.Hash}'"));
        Assert.AreEqual(0L, await ScalarLongAsync(connection, $"SELECT COUNT(*) FROM assets WHERE hash='{orphanAsset.Hash}'"));
    }

    private static AssetRecord CreateAsset(char hashCharacter) =>
        new(new string(hashCharacter, 64), "image/png", 1, 1, [(byte)hashCharacter], DateTimeOffset.UtcNow);

    private static string CreateTextScene(string id, string text) =>
        CreateTextScene((id, text, 10d, 20d));

    private static string CreateTextScene(params (string Id, string Text, double X, double Y)[] items) =>
        JsonSerializer.Serialize(new
        {
            type = "excalidraw",
            version = 2,
            elements = items.Select(item => new
            {
                id = item.Id,
                type = "text",
                x = item.X,
                y = item.Y,
                text = item.Text,
                originalText = item.Text,
                isDeleted = false
            }).ToArray(),
            appState = new { viewBackgroundColor = "#ffffff" },
            files = new { }
        });

    private static string CreateImageScene(string fileId) =>
        JsonSerializer.Serialize(new
        {
            type = "excalidraw",
            version = 2,
            elements = new[]
            {
                new { id = $"element-{fileId}", type = "image", fileId, x = 10, y = 20, isDeleted = false }
            },
            appState = new { viewBackgroundColor = "#ffffff" },
            files = new Dictionary<string, object>()
        });

    private static async Task CreateVersionOneDatabaseAsync(string path, string scene)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE scene_current(
                id INTEGER PRIMARY KEY CHECK(id=1),
                scene_json TEXT NOT NULL,
                revision INTEGER NOT NULL,
                updated_utc TEXT NOT NULL);
            CREATE TABLE scene_revisions(
                revision INTEGER PRIMARY KEY,
                scene_json TEXT NOT NULL,
                saved_utc TEXT NOT NULL,
                reason TEXT NOT NULL);
            INSERT INTO meta VALUES('schema_version', '1');
            INSERT INTO meta VALUES('title', 'Legacy board');
            INSERT INTO scene_current VALUES(1, $scene, 7, $now);
            INSERT INTO scene_revisions VALUES(7, $scene, $now, 'legacy');
            """;
        command.Parameters.AddWithValue("$scene", scene);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PinboardStorageTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Test cleanup must not hide the assertion result.
            }
        }
    }
}
