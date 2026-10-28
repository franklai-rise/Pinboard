using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pinboard.App.Models;
using Pinboard.App.Services.AiSessions;

namespace Pinboard.App.Tests;

[TestClass]
public sealed class AiSessionArchiveTests
{
    private string _root = null!;
    [TestInitialize] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "PinboardAiTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
    private SessionCatalog Catalog(string name = "Archive") => new(Path.Combine(_root, name));
    private string FilePath(string name, string text)
    { var path = Path.Combine(_root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, new UTF8Encoding(false)); return path; }
    private static SessionDescriptor Item(string path, string id = "one", string title = "Shared title") => new()
    { Key = "Imported:" + id, Source = "Imported", SourceId = id, Title = title, Path = path, UpdatedAt = File.GetLastWriteTimeUtc(path), Size = new FileInfo(path).Length };
    private SessionArchiveService Service(SessionCatalog catalog, ISessionCloud? cloud = null) => new(catalog, [new ImportedSessionSource(_root)], cloud);

    [TestMethod]
    public void Catalogue_PreservesIndependentSelectionAndRenameAcrossRestart()
    {
        var path = FilePath("source.txt", "hello"); var catalog = Catalog();
        catalog.Merge([Item(path), Item(path, "two") with { ParentKey = "Imported:one" }]);
        Assert.IsTrue(catalog.List().All(x => !x.Selected));
        catalog.Select("Imported:one", true); catalog.Rename("Imported:one", "Local alias");
        var reopened = Catalog(); reopened.Merge([Item(path, title: "Changed source title")]);
        Assert.IsTrue(reopened.Find("Imported:one")!.Selected);
        Assert.IsFalse(reopened.Find("Imported:two")!.Selected);
        Assert.AreEqual("Local alias", reopened.Find("Imported:one")!.Title);
        Assert.AreEqual("Imported:one", reopened.NextJob());
        reopened.Select("Imported:one", false); Assert.IsNull(reopened.NextJob());
        Assert.AreEqual("hello", File.ReadAllText(path));
    }
    [TestMethod]
    public async Task JsonlSnapshot_ExcludesIncompleteTrailingRecordAndDoesNotChangeSource()
    {
        var original = "{\"message\":\"完整消息\"}\n{\"incomplete\":"; var path = FilePath("source.jsonl", original);
        var stage = Path.Combine(_root, "snapshot"); Directory.CreateDirectory(stage);
        var copy = await SessionSnapshot.CopyAsync(path, stage, "conversation.jsonl", default);
        Assert.AreEqual("{\"message\":\"完整消息\"}\n", await File.ReadAllTextAsync(copy.Path));
        Assert.AreEqual(original, await File.ReadAllTextAsync(path));
    }
    [TestMethod]
    public async Task EmptyJsonl_IsAnEmptyValidSnapshot()
    {
        var path = FilePath("empty.jsonl", ""); var stage = Path.Combine(_root, "snapshot"); Directory.CreateDirectory(stage);
        var copy = await SessionSnapshot.CopyAsync(path, stage, "empty.jsonl", default); Assert.AreEqual(0L, new FileInfo(copy.Path).Length);
    }
    [TestMethod]
    public async Task Selection_DeduplicatesResourcesAndRestoreVerifiesOriginalBytes()
    {
        var path = FilePath("source.txt", "captured conversation"); var catalog = Catalog(); catalog.Merge([Item(path), Item(path, "two")]);
        var service = Service(catalog); await service.BackupAsync("Imported:one"); Assert.AreEqual(0, catalog.Versions("Imported:one").Count);
        catalog.Select("Imported:one", true); catalog.Select("Imported:two", true);
        await service.BackupAsync("Imported:one"); await service.BackupAsync("Imported:two");
        Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(catalog.Root, "Objects"), "*.bin", SearchOption.AllDirectories).Count());
        var first = catalog.Versions("Imported:one").Single();
        await service.BackupAsync("Imported:one"); Assert.AreEqual(1, catalog.Versions("Imported:one").Count);
        await File.AppendAllTextAsync(path, "\nnew turn"); catalog.Merge([Item(path)]); await service.BackupAsync("Imported:one");
        Assert.AreEqual(2, catalog.Versions("Imported:one").Count);
        var restored = await service.RestoreAsync(first, Path.Combine(_root, "Restore"), false);
        Assert.AreEqual("captured conversation", await File.ReadAllTextAsync(Path.Combine(restored, "source.txt")));
        catalog.Select("Imported:one", false); await File.AppendAllTextAsync(path, "future update"); catalog.Merge([Item(path)]);
        await service.BackupAsync("Imported:one"); Assert.AreEqual(2, catalog.Versions("Imported:one").Count);
    }
    [TestMethod]
    public async Task AppendOnlyLargeFile_ReusesUnchangedChunks()
    {
        var path = Path.Combine(_root, "export.zip"); var bytes = new byte[SessionArchiveService.ChunkSize * 2]; RandomNumberGenerator.Fill(bytes);
        await File.WriteAllBytesAsync(path, bytes); var catalog = Catalog(); catalog.Merge([Item(path)]); catalog.Select("Imported:one", true); var service = Service(catalog);
        await service.BackupAsync("Imported:one"); await using (var stream = new FileStream(path, FileMode.Append)) await stream.WriteAsync(new byte[] { 1, 2, 3 });
        catalog.Merge([Item(path)]); await service.BackupAsync("Imported:one");
        Assert.AreEqual(3, Directory.EnumerateFiles(Path.Combine(catalog.Root, "Objects"), "*.bin", SearchOption.AllDirectories).Count());
    }
    [TestMethod]
    public async Task Corruption_IsDetectedDuringRestore()
    {
        var path = FilePath("source.txt", "original"); var catalog = Catalog(); catalog.Merge([Item(path)]); catalog.Select("Imported:one", true); var service = Service(catalog);
        await service.BackupAsync("Imported:one"); var version = catalog.Versions("Imported:one").Single(); var manifest = await SessionArchiveService.ReadManifestAsync(version.ManifestPath);
        await File.WriteAllTextAsync(service.ObjectPath(manifest.Files[0].Chunks[0].Hash), "tampered");
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => service.RestoreAsync(version, Path.Combine(_root, "Restore"), false));
    }
    [TestMethod]
    public void RestorePaths_RejectTraversalAndAlternateDataStreams()
    {
        foreach (var path in new[] { "../escape", "..\\escape", "file:stream", "//server/share" })
            Assert.ThrowsException<InvalidDataException>(() => SessionArchiveService.SafeRestorePath(_root, path));
        Assert.AreEqual(Path.Combine(_root, "attachments", "image.png"), SessionArchiveService.SafeRestorePath(_root, "attachments/image.png"));
    }
    [TestMethod]
    public void SelectedDatabaseExport_DoesNotIncludeOtherThreadsAndSupportsLiveWal()
    {
        var source = Path.Combine(_root, "history.sqlite"); using var db = new SqliteConnection($"Data Source={source};Pooling=False"); db.Open();
        using (var cmd = db.CreateCommand()) { cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE thread_items(thread_id TEXT,item_json TEXT); INSERT INTO thread_items VALUES('chosen','hello'),('unselected','private');"; cmd.ExecuteNonQuery(); }
        var stage = Path.Combine(_root, "snapshot"); Directory.CreateDirectory(stage);
        var file = SessionSnapshot.ExportRows(source, stage, "selected.sqlite", "thread_id", "chosen", "thread_items");
        using var snapshot = SessionSnapshot.OpenRead(file.Path); using var query = snapshot.CreateCommand(); query.CommandText = "SELECT GROUP_CONCAT(thread_id) FROM thread_items";
        Assert.AreEqual("chosen", query.ExecuteScalar()); query.CommandText = "PRAGMA integrity_check"; Assert.AreEqual("ok", query.ExecuteScalar());
    }
    [TestMethod]
    public async Task MissingSource_DoesNotCreateConfirmedVersion()
    {
        var source = FilePath("source.txt", "record"); var catalog = Catalog(); catalog.Merge([Item(source)]); catalog.Select("Imported:one", true); File.Delete(source);
        await Assert.ThrowsExceptionAsync<FileNotFoundException>(() => Service(catalog).BackupAsync("Imported:one")); Assert.AreEqual(0, catalog.Versions("Imported:one").Count);
    }
    [TestMethod]
    public async Task CloudQuota_DoesNotLoseLocalVersionOrUpload()
    {
        var source = FilePath("source.txt", "more than one byte"); var catalog = Catalog(); catalog.Merge([Item(source)]); catalog.Select("Imported:one", true);
        var cloud = new MemoryCloud { Limit = 1 }; var service = Service(catalog, cloud);
        await Assert.ThrowsExceptionAsync<DriveQuotaException>(() => service.BackupAsync("Imported:one"));
        Assert.AreEqual(1, catalog.Versions("Imported:one").Count); Assert.AreEqual(0, cloud.Uploads); Assert.IsNull(catalog.Versions("Imported:one")[0].CloudFileId);
    }
    [TestMethod]
    public async Task CloudBackup_RestoresAfterLocalCatalogueAndObjectsAreLost()
    {
        var source = FilePath("source.txt", "non-sensitive demonstration"); var cloud = new MemoryCloud(); var catalog = Catalog();
        catalog.Merge([Item(source)]); catalog.Select("Imported:one", true); await Service(catalog, cloud).BackupAsync("Imported:one");
        var recovered = Catalog("Recovered"); var service = Service(recovered, cloud); Assert.AreEqual(1, await service.RecoverCloudIndexAsync());
        Assert.IsFalse(recovered.Find("Imported:one")!.Selected); var version = recovered.Versions("Imported:one").Single();
        var restore = await service.RestoreAsync(version, Path.Combine(_root, "CloudRestore"), true);
        Assert.AreEqual("non-sensitive demonstration", await File.ReadAllTextAsync(Path.Combine(restore, "source.txt")));
    }
    [TestMethod]
    public async Task CloudFailure_KeepsPersistentRetryJobAndLocalData()
    {
        var source = FilePath("source.txt", "kept locally"); var catalog = Catalog(); catalog.Merge([Item(source)]); catalog.Select("Imported:one", true);
        var cloud = new MemoryCloud { FailUpload = true }; var service = Service(catalog, cloud); service.Start();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (catalog.Find("Imported:one")!.Status != "Retry pending" && DateTime.UtcNow < deadline) await Task.Delay(30);
        await service.StopAsync(); Assert.AreEqual("Retry pending", catalog.Find("Imported:one")!.Status);
        Assert.AreEqual(1, catalog.Versions("Imported:one").Count);
        var reopened = Catalog(); reopened.Queue("Imported:one"); Assert.AreEqual("Imported:one", reopened.NextJob());
    }
    [TestMethod]
    public async Task Upload_ResumesFromServerAcknowledgementAfterRestart()
    {
        var bytes = new byte[SessionArchiveService.ChunkSize]; RandomNumberGenerator.Fill(bytes); var path = Path.Combine(_root, "chunk.bin"); await File.WriteAllBytesAsync(path, bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(); var handler = new UploadHandler(bytes) { InterruptOnce = true };
        using var http = new HttpClient(handler); var catalog = Catalog();
        var drive = new GoogleDriveSessionCloud(catalog, Path.Combine(_root, "Profile"), http, _ => Task.FromResult("test-token"));
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => drive.UploadObjectAsync(hash, path, default));
        Assert.AreEqual(1024 * 1024L, catalog.GetObject("test-account", hash)!.Offset);
        var restarted = new GoogleDriveSessionCloud(Catalog(), Path.Combine(_root, "Profile"), http, _ => Task.FromResult("test-token"));
        Assert.AreEqual("uploaded-file", await restarted.UploadObjectAsync(hash, path, default));
        Assert.IsTrue(handler.Probed); Assert.AreEqual(1, handler.Initiations); CollectionAssert.AreEqual(bytes, handler.Received.ToArray());
    }
    [TestMethod]
    public async Task Upload_ChecksumMismatchIsNotMarkedComplete()
    {
        var bytes = Encoding.UTF8.GetBytes("demo"); var path = Path.Combine(_root, "chunk.bin"); await File.WriteAllBytesAsync(path, bytes);
        var handler = new UploadHandler(bytes) { CorruptChecksum = true }; using var http = new HttpClient(handler); var catalog = Catalog();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var drive = new GoogleDriveSessionCloud(catalog, Path.Combine(_root, "Profile"), http, _ => Task.FromResult("test-token"));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => drive.UploadObjectAsync(hash, path, default));
        Assert.IsTrue(string.IsNullOrEmpty(catalog.GetObject("test-account", hash)?.FileId));
    }
    [TestMethod]
    public async Task Upload_ReportsAcknowledgedBytesAndVerifiedCompletion()
    {
        var bytes = new byte[3 * 1024 * 1024]; RandomNumberGenerator.Fill(bytes);
        var path = Path.Combine(_root, "progress.bin"); await File.WriteAllBytesAsync(path, bytes);
        var handler = new UploadHandler(bytes); using var http = new HttpClient(handler);
        var drive = new GoogleDriveSessionCloud(Catalog(), Path.Combine(_root, "Profile"), http, _ => Task.FromResult("test-token"));
        var acknowledged = new List<long>(); drive.TransferProgress += (completed, total) => { Assert.AreEqual(bytes.LongLength, total); acknowledged.Add(completed); };
        await drive.UploadObjectAsync(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), path, default);
        CollectionAssert.Contains(acknowledged, 1024 * 1024L); CollectionAssert.Contains(acknowledged, 2 * 1024 * 1024L);
        Assert.AreEqual(bytes.LongLength, acknowledged.Last()); Assert.IsTrue(acknowledged.Zip(acknowledged.Skip(1)).All(x => x.First <= x.Second));
    }
    [TestMethod]
    public async Task AuthorizationError_DoesNotBecomeGenericUploadSuccess()
    {
        using var http = new HttpClient(new FixedResponseHandler(HttpStatusCode.Unauthorized, "{}"));
        var drive = new GoogleDriveSessionCloud(Catalog(), Path.Combine(_root, "Profile"), http, _ => Task.FromResult("expired"));
        await Assert.ThrowsExceptionAsync<DriveAuthorizationException>(() => drive.GetAccountAsync(default));
    }
    [TestMethod]
    public void PkceAndProtectedStorage_DoNotPersistPlaintextTokens()
    {
        var pkce = GoogleDriveSessionCloud.CreatePkce(); Assert.AreEqual(43, pkce.Verifier.Length); Assert.AreEqual(43, pkce.Challenge.Length);
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(pkce.Verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_'); Assert.AreEqual(expected, pkce.Challenge);
        var path = Path.Combine(_root, "authorization.bin"); var token = new DriveTokens("private-demo-token", "private-demo-refresh", DateTimeOffset.UtcNow.AddHours(1), "demo-account");
        WindowsUserSecrets.Save(path, token); Assert.IsFalse(Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(token.AccessToken));
        Assert.AreEqual(token, WindowsUserSecrets.Load<DriveTokens>(path));
    }
    [TestMethod]
    public void TenThousandTitles_AreIndexedWithoutReadingTranscriptBodies()
    {
        var path = FilePath("record.txt", "small"); var descriptor = Item(path); var catalog = Catalog(); var timer = System.Diagnostics.Stopwatch.StartNew();
        catalog.Merge(Enumerable.Range(0, 10000).Select(i => descriptor with { Key = "Imported:" + i, SourceId = i.ToString(), Title = "Conversation " + i }));
        Assert.AreEqual(10000, catalog.List().Count); Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(10));
        Assert.IsFalse(Directory.Exists(Path.Combine(catalog.Root, "Objects")));
    }
    [TestMethod]
    public void ArchiveLocation_IsSeparateAndPreservesExplicitPreference()
    {
        var settings = new AppSettings { LibraryPath = Path.Combine(_root, "Pinboards") };
        Assert.AreEqual(Path.Combine(_root, "PinboardArchives", "AI Sessions"), settings.ResolveAiSessionsArchivePath());
        settings.AiSessionsArchivePath = Path.Combine(_root, "Custom"); Assert.AreEqual(settings.AiSessionsArchivePath, settings.ResolveAiSessionsArchivePath());
    }

    private sealed class MemoryCloud : ISessionCloud
    {
        public bool IsConnected => true; public long Limit = 100000000; public int Uploads; public bool FailUpload;
        private readonly Dictionary<string, byte[]> _data = []; private readonly Dictionary<string, string> _objects = []; private readonly Dictionary<string, string> _manifests = [];
        public Task<DriveAccount> GetAccountAsync(CancellationToken ct) => Task.FromResult(new DriveAccount("demo-account", "Demo", Limit, 0));
        public async Task<string> UploadObjectAsync(string hash, string path, CancellationToken ct)
        { if (FailUpload) throw new HttpRequestException("offline"); Uploads++; var id = "object-" + hash; _objects[hash] = id; _data[id] = await File.ReadAllBytesAsync(path, ct); return id; }
        public async Task<string> UploadManifestAsync(SessionManifest manifest, string path, CancellationToken ct)
        { var bytes = await File.ReadAllBytesAsync(path, ct); var id = "manifest-" + manifest.VersionId; _data[id] = bytes; _manifests[id] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(); return id; }
        public Task DownloadObjectAsync(string id, string target, CancellationToken ct) => File.WriteAllBytesAsync(target, _data[id], ct);
        public Task<string?> FindObjectAsync(string hash, CancellationToken ct) => Task.FromResult(_objects.GetValueOrDefault(hash));
        public Task<IReadOnlyList<(string FileId, string Hash)>> ListManifestsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<(string, string)>>(_manifests.Select(x => (x.Key, x.Value)).ToArray());
    }
    private sealed class FixedResponseHandler(HttpStatusCode status, string text) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(text) });
    }
    private sealed class UploadHandler(byte[] expected) : HttpMessageHandler
    {
        public bool InterruptOnce; public bool CorruptChecksum; public bool Probed; public int Initiations;
        public MemoryStream Received { get; } = new(); private bool _interrupted;
        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) };
        private HttpResponseMessage Complete() => Json(new { id = "uploaded-file", size = expected.Length.ToString(), md5Checksum = CorruptChecksum ? "wrong" : Convert.ToHexString(MD5.HashData(expected)).ToLowerInvariant() });
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/about")) return Json(new { user = new { permissionId = "test-account", displayName = "Demo" }, storageQuota = new { limit = "100000000", usage = "0" } });
            if (request.Method == HttpMethod.Get && uri.AbsolutePath.EndsWith("/files"))
            {
                var q = Uri.UnescapeDataString(uri.Query);
                return q.Contains("mimeType") ? Json(new { files = new[] { new { id = "folder" } } }) : Json(new { files = Array.Empty<object>() });
            }
            if (request.Method == HttpMethod.Post && uri.AbsolutePath.Contains("/upload/"))
            { Initiations++; var response = new HttpResponseMessage(HttpStatusCode.OK); response.Headers.Location = new Uri("https://www.googleapis.com/upload-session/1"); return response; }
            if (request.Method == HttpMethod.Put)
            {
                if (request.Content!.Headers.ContentRange?.From is null)
                { Probed = true; return Incomplete(); }
                if (InterruptOnce && !_interrupted && Received.Length > 0) { _interrupted = true; throw new HttpRequestException("simulated network interruption"); }
                Assert.AreEqual(Received.Length, request.Content.Headers.ContentRange.From!.Value);
                await request.Content.CopyToAsync(Received, ct);
                return Received.Length == expected.Length ? Complete() : Incomplete();
            }
            throw new AssertFailedException("Unexpected request " + request.Method + " " + uri.AbsolutePath);
        }
        private HttpResponseMessage Incomplete()
        { var response = new HttpResponseMessage((HttpStatusCode)308); if (Received.Length > 0) response.Headers.TryAddWithoutValidation("Range", "bytes=0-" + (Received.Length - 1)); return response; }
    }
}
