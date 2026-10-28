using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pinboard.App.Services.AiSessions;

namespace Pinboard.App.Tests;

[TestClass]
public sealed class AiSessionRepairTests
{
    private string _root = null!;
    [TestInitialize] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "PinboardRepairTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
    private SessionCatalog Catalog() => new(Path.Combine(_root, "Archive"));
    private string Write(string relative, string text)
    { var path = Path.Combine(_root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, new UTF8Encoding(false)); return path; }
    private static SessionDescriptor Item(string path, string id = "one") => new()
    { Key = "Imported:" + id, Source = "Imported", SourceId = id, Title = "A shared title", Path = path, Size = new FileInfo(path).Length, UpdatedAt = File.GetLastWriteTimeUtc(path) };

    [TestMethod]
    public void JobsMigration_PreservesSelectionsVersionsAndAttempts()
    {
        var directory = Path.Combine(_root, "Archive"); Directory.CreateDirectory(directory);
        using (var db = new SqliteConnection($"Data Source={Path.Combine(directory, "catalog.sqlite")};Pooling=False"))
        {
            db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TABLE jobs(session_key TEXT PRIMARY KEY,due_at TEXT NOT NULL,attempts INTEGER NOT NULL DEFAULT 0); INSERT INTO jobs VALUES('Imported:one','2026-10-18T00:00:00.0000000+00:00',125)";
            cmd.ExecuteNonQuery();
        }
        var catalog = Catalog(); var item = Item(Write("source.txt", "unchanged")); catalog.Merge([item]); catalog.Select(item.Key, true);
        Assert.AreEqual(125, catalog.GetJob(item.Key)!.Attempts); Assert.IsFalse(catalog.GetJob(item.Key)!.Blocked);
        Assert.AreEqual(125, Catalog().GetJob(item.Key)!.Attempts); Assert.IsTrue(Catalog().Find(item.Key)!.Selected);
    }
    [TestMethod]
    public void RetryPolicy_DistinguishesTransientFailuresFromRequiredActions()
    {
        Assert.AreEqual(SessionFailureKind.Transient, SessionRetryPolicy.Classify(new HttpRequestException("offline")));
        Assert.AreEqual(SessionFailureKind.Transient, SessionRetryPolicy.Classify(new HttpRequestException("limited", null, HttpStatusCode.TooManyRequests)));
        Assert.AreEqual(SessionFailureKind.Transient, SessionRetryPolicy.Classify(new TaskCanceledException("timeout")));
        Assert.AreEqual(SessionFailureKind.MissingSource, SessionRetryPolicy.Classify(new FileNotFoundException("missing")));
        Assert.AreEqual(SessionFailureKind.Authorization, SessionRetryPolicy.Classify(new DriveAuthorizationException()));
        Assert.AreEqual(SessionFailureKind.Quota, SessionRetryPolicy.Classify(new DriveQuotaException()));
        Assert.AreEqual(SessionFailureKind.Permission, SessionRetryPolicy.Classify(new HttpRequestException("denied", null, HttpStatusCode.Forbidden)));
        Assert.AreEqual(SessionFailureKind.Review, SessionRetryPolicy.Classify(new InvalidDataException("unsafe")));
        Assert.AreEqual(TimeSpan.FromSeconds(30), SessionRetryPolicy.Delay(1));
        Assert.AreEqual(TimeSpan.FromSeconds(60), SessionRetryPolicy.Delay(2));
        Assert.AreEqual(TimeSpan.FromMinutes(30), SessionRetryPolicy.Delay(125));
    }
    [TestMethod]
    public void BlockedJobs_PersistAndRequireSourceChangeOrExplicitRetry()
    {
        var catalog = Catalog(); var item = Item(Write("source.txt", "hello")); catalog.Merge([item]); catalog.Select(item.Key, true);
        catalog.SetStatus(item.Key, "Needs source", "Source missing"); catalog.RetryJob(item.Key, SessionFailureKind.MissingSource);
        Assert.IsNull(catalog.NextJob()); Assert.IsTrue(Catalog().GetJob(item.Key)!.Blocked);
        var job = catalog.GetJob(item.Key)!; catalog.QueueMany([item.Key], restartExisting: false);
        Assert.AreEqual(job, catalog.GetJob(item.Key)); catalog.Queue(item.Key);
        Assert.AreEqual(item.Key, catalog.NextJob()); Assert.IsFalse(catalog.GetJob(item.Key)!.Blocked);
        catalog.RetryJob(item.Key); Assert.AreEqual(2, catalog.GetJob(item.Key)!.Attempts);
        Assert.IsTrue(catalog.GetJob(item.Key)!.DueAt > DateTimeOffset.UtcNow.AddSeconds(50));
        var progress = catalog.ReadProgress(false, null); Assert.AreEqual(1, progress.AutoRetrying);
        catalog.RetryJob(item.Key, SessionFailureKind.MissingSource); progress = catalog.ReadProgress(false, null);
        Assert.AreEqual(0, progress.AutoRetrying); Assert.AreEqual(1, progress.Blocked);
        Assert.IsTrue(SessionProgressPresentation.Create(progress, true).Summary.Contains("需处理 1"));
    }
    [TestMethod]
    public async Task RestoredMissingSource_UnblocksAutomaticallyWithoutResettingOtherRetries()
    {
        var item = Item(Write("source.txt", "restorable")); var catalog = Catalog(); catalog.Merge([item]); catalog.Select(item.Key, true);
        var service = new SessionArchiveService(catalog, [new CountingSource(item)]); File.Delete(item.Path!);
        var other = Item(Write("other.txt", "waiting"), "two"); catalog.Merge([other]); catalog.Select(other.Key, true);
        catalog.SetStatus(other.Key, "Retry pending", "offline"); catalog.RetryJob(other.Key); var due = catalog.GetJob(other.Key)!.DueAt;
        catalog.SetStatus(item.Key, "Needs source", "missing"); catalog.RetryJob(item.Key, SessionFailureKind.MissingSource);
        await service.ScanAsync(); Assert.IsNull(catalog.NextJob()); await File.WriteAllTextAsync(item.Path!, "restorable");
        await service.ScanAsync(); Assert.AreEqual(item.Key, catalog.NextJob()); Assert.IsFalse(catalog.GetJob(item.Key)!.Blocked);
        Assert.AreEqual(due, catalog.GetJob(other.Key)!.DueAt);
    }
    [TestMethod]
    public async Task UnchangedPartialBackup_IsNotResnapshottedAndRecoveredImagesAreDetected()
    {
        var item = Item(Write("source.txt", "notes")); var catalog = Catalog(); catalog.Merge([item]); catalog.Select(item.Key, true);
        var image = Path.Combine(_root, "missing-image.png"); var source = new CountingSource(item, image); var service = new SessionArchiveService(catalog, [source]);
        await service.BackupAsync(item.Key); catalog.FinishJob(item.Key);
        Assert.AreEqual("Local backup (missing attachments)", catalog.Find(item.Key)!.Status);
        Assert.IsFalse(service.GetProgress().IsComplete); await service.ScanAsync(); Assert.IsNull(catalog.NextJob());
        service.QueueSelected(); Assert.IsNull(catalog.NextJob(), "Healthy partial snapshots are not uploaded over and over.");
        await service.BackupAsync(item.Key); Assert.AreEqual(1, source.Snapshots); Assert.AreEqual(1, catalog.Versions(item.Key).Count);
        await File.WriteAllTextAsync(image, "synthetic image"); await service.ScanAsync(); Assert.AreEqual(item.Key, catalog.NextJob());
        await service.BackupAsync(item.Key); catalog.FinishJob(item.Key);
        Assert.AreEqual(2, source.Snapshots); Assert.AreEqual("Local backup", catalog.Find(item.Key)!.Status); Assert.IsTrue(service.GetProgress().IsComplete);
    }
    [TestMethod]
    public async Task CompleteBackup_IsNotResetByBackUpSelected()
    {
        var item = Item(Write("source.txt", "notes")); var catalog = Catalog(); catalog.Merge([item]); catalog.Select(item.Key, true);
        var service = new SessionArchiveService(catalog, [new CountingSource(item)]); await service.BackupAsync(item.Key); catalog.FinishJob(item.Key);
        service.QueueSelected(); Assert.IsNull(catalog.NextJob()); Assert.IsTrue(service.GetProgress().IsComplete);
    }
    [TestMethod]
    public async Task DamagedPartialMetadata_DoesNotBlockOtherConversations()
    {
        var item = Item(Write("source.txt", "notes")); var catalog = Catalog(); catalog.Merge([item]); catalog.Select(item.Key, true);
        var service = new SessionArchiveService(catalog, [new CountingSource(item, Path.Combine(_root, "missing.png"))]);
        await service.BackupAsync(item.Key); catalog.FinishJob(item.Key);
        var manifest = await SessionArchiveService.ReadManifestAsync(catalog.Versions(item.Key).Single().ManifestPath);
        var report = manifest.Files.Single(file => file.RelativePath == "pinboard-attachment-report.json");
        File.Delete(service.ObjectPath(report.Chunks.Single().Hash));
        var other = Item(Write("other.txt", "other notes"), "two"); catalog.Merge([other]); catalog.Select(other.Key, true);
        await service.ScanAsync(); Assert.AreEqual("Needs review", catalog.Find(item.Key)!.Status);
        Assert.IsTrue(catalog.GetJob(item.Key)!.Blocked); Assert.AreEqual(other.Key, catalog.NextJob());
        Assert.AreEqual(1, catalog.Versions(item.Key).Count); Assert.AreEqual("notes", File.ReadAllText(item.Path!));
    }
    private (CodexSessionSource Source, SessionDescriptor Item) CodexFixture(bool hasHistory)
    {
        var root = Path.Combine(_root, ".codex"); Directory.CreateDirectory(Path.Combine(root, "sessions"));
        var path = Path.Combine(root, "sessions", "rollout-missing.jsonl"); var state = Path.Combine(root, "state_5.sqlite"); var history = Path.Combine(root, "thread_history_1.sqlite");
        using (var db = new SqliteConnection($"Data Source={state};Pooling=False"))
        {
            db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TABLE threads(id TEXT,title TEXT,rollout_path TEXT,cwd TEXT,updated_at INTEGER,archived INTEGER,source TEXT); INSERT INTO threads VALUES('chosen','Chosen',$path,'demo',1,0,'{}'),('other','Unselected','private','demo',1,0,'{}');";
            cmd.Parameters.AddWithValue("$path", path); cmd.ExecuteNonQuery();
        }
        using (var db = new SqliteConnection($"Data Source={history};Pooling=False"))
        {
            db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TABLE thread_items(thread_id TEXT,item_json TEXT); CREATE TABLE thread_turns(thread_id TEXT,data TEXT); INSERT INTO thread_items VALUES('other','{\"text\":\"must not leak\"}');";
            if (hasHistory) cmd.CommandText += " INSERT INTO thread_items VALUES('chosen','{\"text\":\"selected message\"}');";
            cmd.ExecuteNonQuery();
        }
        return (new CodexSessionSource(root), new() { Key = "Codex:chosen", Source = "Codex", SourceId = "chosen", Title = "Chosen", Project = "demo", Path = path,
            MetadataPath = state, HistoryPath = history, Availability = "Missing source", UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(1) });
    }
    [TestMethod]
    public async Task MissingCodexRecording_SalvagesOnlySelectedHistoryAndNeverClaimsComplete()
    {
        var (source, item) = CodexFixture(true); var catalog = Catalog(); catalog.Merge([item]); catalog.Select(item.Key, true);
        var service = new SessionArchiveService(catalog, [source]); await service.BackupAsync(item.Key); catalog.FinishJob(item.Key);
        var version = catalog.Versions(item.Key).Single(); var manifest = await SessionArchiveService.ReadManifestAsync(version.ManifestPath);
        Assert.IsTrue(manifest.Warnings!.Any(warning => warning.Contains("not a complete backup")));
        Assert.AreEqual("Local backup (missing source)", catalog.Find(item.Key)!.Status); Assert.IsFalse(service.GetProgress().IsComplete);
        var restored = await service.RestoreAsync(version, Path.Combine(_root, "Restore"), false);
        using var db = SessionSnapshot.OpenRead(Path.Combine(restored, "thread-history.sqlite")); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM thread_items WHERE thread_id<>'chosen'"; Assert.AreEqual(0L, cmd.ExecuteScalar());
        cmd.CommandText = "SELECT COUNT(*) FROM thread_items WHERE thread_id='chosen'"; Assert.AreEqual(1L, cmd.ExecuteScalar());
        Assert.IsTrue(File.Exists(Path.Combine(restored, "pinboard-source-report.json")));
        await service.ScanAsync(); Assert.IsNull(catalog.NextJob(), "Unchanged selected history is not re-exported every five minutes.");
    }
    [TestMethod]
    public async Task TitleOnlyMetadata_IsNotMistakenForRecoverableConversation()
    {
        var (source, item) = CodexFixture(false); var catalog = Catalog(); catalog.Merge([item]); catalog.Select(item.Key, true);
        await Assert.ThrowsExceptionAsync<FileNotFoundException>(() => new SessionArchiveService(catalog, [source]).BackupAsync(item.Key));
        Assert.AreEqual(0, catalog.Versions(item.Key).Count);
    }
    [TestMethod]
    public async Task Diagnostics_DistinguishesNamesVersionsAndSharedResourcesWithoutDeleting()
    {
        var item = Item(Write("source.txt", "same notes")); var catalog = Catalog(); var other = item with { Key = "Imported:two", SourceId = "two" };
        catalog.Merge([item, other]); catalog.SetSelection([item.Key, other.Key], true);
        var service = new SessionArchiveService(catalog, [new CountingSource(item)]); await service.BackupAsync(item.Key); await service.BackupAsync(other.Key);
        var audit = await service.InspectAsync(false); Assert.AreEqual(1, audit.SameTitleGroups); Assert.AreEqual(0, audit.RepeatedVersions);
        Assert.AreEqual(1, audit.IdenticalSnapshots.Count); Assert.IsTrue(audit.SharedBytesSaved > 0);
        Assert.AreEqual(2, catalog.List().Count(entry => entry.Selected)); Assert.AreEqual(2, audit.Versions);
        var manifest = await SessionArchiveService.ReadManifestAsync(catalog.Versions(item.Key).Single().ManifestPath);
        var duplicate = manifest with { VersionId = Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow.AddSeconds(1) };
        var duplicatePath = service.ManifestPath(duplicate.VersionId); await File.WriteAllTextAsync(duplicatePath, JsonSerializer.Serialize(duplicate)); catalog.AddVersion(duplicate, duplicatePath);
        audit = await service.InspectAsync(false); Assert.AreEqual(1, audit.RepeatedVersions); Assert.AreEqual(3, audit.Versions);
        Assert.IsTrue(SessionArchiveDiagnostics.Present(audit, true).Contains("同名不等于重复"));
        Assert.AreEqual(3, catalog.List().Sum(entry => catalog.Versions(entry.Key).Count));
    }
    [TestMethod]
    public async Task CloudDuplicateCheck_IsPaginatedScopedAndReadOnly()
    {
        var handler = new InventoryHandler(); using var http = new HttpClient(handler);
        var drive = new GoogleDriveSessionCloud(Catalog(), Path.Combine(_root, "Profile"), http, _ => Task.FromResult("test-token"));
        var inventory = await drive.InspectAsync(default);
        Assert.AreEqual(1, inventory.Roots); Assert.AreEqual(4, inventory.Objects); Assert.AreEqual(1, inventory.Manifests);
        Assert.AreEqual(1, inventory.DuplicateGroups); Assert.AreEqual(1, inventory.ExtraFiles); Assert.AreEqual(10L, inventory.ExtraBytes);
        Assert.AreEqual(1, inventory.ConflictingHashes); Assert.AreEqual(1, inventory.InvalidMetadata); Assert.IsTrue(handler.PageRead);
        Assert.IsTrue(handler.Requests.All(request => request == HttpMethod.Get));
    }
    [TestMethod]
    public void IssueHelp_SeparatesMissingImagesFromTransfersAndExplainsActions()
    {
        var descriptor = Item(Write("source.txt", "hello")); var entry = new SessionEntry(descriptor, true, null, "Cloud backup (missing attachments)", null, DateTimeOffset.UtcNow);
        Assert.IsTrue(SessionIssuePresentation.Matches(entry, SessionIssueFilter.Attachments)); Assert.IsFalse(SessionIssuePresentation.Matches(entry, SessionIssueFilter.Transfer));
        Assert.IsTrue(SessionIssuePresentation.Help(entry, null, true).Contains("重试不能"));
        var partial = entry with { Status = "Cloud backup (missing source)", Descriptor = descriptor with { Availability = "Missing source" } };
        Assert.IsTrue(SessionIssuePresentation.Matches(partial, SessionIssueFilter.Source)); Assert.IsTrue(SessionIssuePresentation.Help(partial, null, true).Contains("不完整"));
        var failure = entry with { Status = "Retry pending", Error = "offline" }; var job = new SessionJobInfo(2, DateTimeOffset.UtcNow.AddMinutes(1), false, SessionFailureKind.Transient);
        Assert.IsTrue(SessionIssuePresentation.Matches(failure, SessionIssueFilter.Transfer)); Assert.IsTrue(SessionIssuePresentation.Help(failure, job, true).Contains("下次自动重试"));
    }
    private sealed class CountingSource(SessionDescriptor item, string? missingImage = null) : ISessionSourceAdapter
    {
        public string Name => "Imported"; public int Snapshots;
        public Task<IReadOnlyList<SessionDescriptor>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SessionDescriptor>>([item]);
        public async Task<IReadOnlyList<SnapshotFile>> SnapshotAsync(SessionDescriptor session, string staging, CancellationToken ct)
        {
            Snapshots++; var files = new List<SnapshotFile> { await SessionSnapshot.CopyAsync(session.Path!, staging, "conversation.txt", ct) };
            if (missingImage is null) return files;
            if (File.Exists(missingImage)) files.Add(await SessionSnapshot.CopyAsync(missingImage, staging, "image.png", ct));
            else { var report = Path.Combine(staging, "pinboard-attachment-report.json"); await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new[] { missingImage }), ct); files.Add(new("pinboard-attachment-report.json", report, true)); }
            return files;
        }
    }
    private sealed class InventoryHandler : HttpMessageHandler
    {
        public bool PageRead; public List<HttpMethod> Requests = [];
        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) };
        private static object Folder(string id, string kind) => new { id, appProperties = new { pinboardKind = kind } };
        private static object File(string id, string hash, int size, string md5) => new { id, size = size.ToString(), md5Checksum = md5, appProperties = new { sha256 = hash, pinboardKind = "ai-sessions-data" } };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.Method); Assert.AreEqual(HttpMethod.Get, request.Method);
            if (request.RequestUri!.AbsolutePath.EndsWith("/about")) return Task.FromResult(Json(new { user = new { permissionId = "test-account", displayName = "Demo" }, storageQuota = new { usage = "0" } }));
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            if (query.Contains("ai-sessions-root")) return Task.FromResult(Json(new { files = new[] { Folder("root-folder", "ai-sessions-root") } }));
            if (query.Contains("'root-folder' in parents")) return Task.FromResult(Json(new { files = new[] { Folder("object-folder", "ai-sessions-objects"), Folder("version-folder", "ai-sessions-versions") } }));
            if (query.Contains("'version-folder' in parents")) return Task.FromResult(Json(new { files = new[] { File("version", new string('b', 64), 11, new string('b', 32)) } }));
            Assert.IsTrue(query.Contains("'object-folder' in parents"));
            if (!query.Contains("pageToken=")) return Task.FromResult(Json(new { files = new[] { File("one", new string('a', 64), 10, new string('a', 32)) }, nextPageToken = "page-two" }));
            PageRead = true;
            return Task.FromResult(Json(new { files = new[] { File("one", new string('a', 64), 10, new string('a', 32)), File("two", new string('a', 64), 10, new string('a', 32)),
                File("conflict", new string('a', 64), 9, new string('b', 32)), File("invalid", "bad", 10, new string('a', 32)) } }));
        }
    }
}
