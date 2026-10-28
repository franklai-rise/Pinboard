using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pinboard.App.Services.AiSessions;

namespace Pinboard.App.Tests;

[TestClass]
public sealed class AiSessionProgressTests
{
    private string _root = null!;
    [TestInitialize] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "PinboardProgressTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
    private SessionCatalog Catalog() => new(Path.Combine(_root, "Archive"));
    private SessionDescriptor Item(string id)
    {
        var path = Path.Combine(_root, id + ".txt"); File.WriteAllText(path, "Non-sensitive progress fixture.");
        return new() { Key = "Imported:" + id, Source = "Imported", SourceId = id, Title = "Demo " + id, Path = path,
            UpdatedAt = File.GetLastWriteTimeUtc(path), Size = new FileInfo(path).Length };
    }
    private static SessionManifest LocalVersion(SessionCatalog catalog, string key, string id)
    {
        var manifest = new SessionManifest(1, id, catalog.Find(key)!.Descriptor, DateTimeOffset.UtcNow, []);
        catalog.AddVersion(manifest, Path.Combine(catalog.Root, id + ".json")); return manifest;
    }
    private static void CloudVersion(SessionCatalog catalog, string key, string id, string account = "account-one")
    { LocalVersion(catalog, key, id); catalog.MarkUploaded(id, account, "remote-" + id); catalog.SetStatus(key, "Cloud backup"); catalog.FinishJob(key); }

    [TestMethod]
    public void EmptyCatalogue_DoesNotClaimOneHundredPercent()
    {
        var progress = Catalog().ReadProgress(true, "account-one");
        Assert.AreEqual(0, progress.Total); Assert.AreEqual(0d, progress.Percentage); Assert.IsFalse(progress.IsComplete);
        Assert.AreEqual("尚未选择备份对话", SessionProgressPresentation.Create(progress, true).Title);
    }
    [TestMethod]
    public void CloudProgress_RequiresLatestVersionAccountAndQueueConfirmation()
    {
        var catalog = Catalog(); catalog.Merge([Item("one"), Item("two")]); catalog.SetSelection(["Imported:one", "Imported:two"], true);
        LocalVersion(catalog, "Imported:one", "v1");
        var progress = catalog.ReadProgress(true, "account-one");
        Assert.AreEqual(2, progress.Total); Assert.AreEqual(1, progress.LocalSaved); Assert.AreEqual(0, progress.Completed);
        catalog.MarkUploaded("v1", "account-one", "remote-v1"); catalog.SetStatus("Imported:one", "Cloud backup");
        Assert.AreEqual(0, catalog.ReadProgress(true, "account-one").Completed, "The job must settle before counting completion.");
        catalog.FinishJob("Imported:one"); progress = catalog.ReadProgress(true, "account-one");
        Assert.AreEqual(1, progress.Completed); Assert.AreEqual(50d, progress.Percentage); Assert.AreEqual(1, progress.Waiting);
        Assert.AreEqual(0, catalog.ReadProgress(true, "another-account").CloudSaved);
        Assert.AreEqual(0, catalog.ReadProgress(true, "another-account").Completed);
        Assert.AreEqual(0, catalog.ReadProgress(true, null).Completed, "An unknown destination must not count a different account's copies.");
        LocalVersion(catalog, "Imported:one", "v2");
        Assert.AreEqual(0, catalog.ReadProgress(true, "account-one").CloudSaved, "An older uploaded version is not the latest version.");
    }
    [TestMethod]
    public void FailedAndPartialItems_AreVisibleAndNeverCountAsCompleted()
    {
        var catalog = Catalog(); catalog.Merge([Item("one"), Item("two"), Item("three")]);
        catalog.SetSelection(catalog.List().Select(x => x.Key), true); CloudVersion(catalog, "Imported:one", "v1");
        catalog.SetStatus("Imported:two", "Retry pending", "Synthetic connection failure"); catalog.RetryJob("Imported:two");
        CloudVersion(catalog, "Imported:three", "v3"); catalog.SetStatus("Imported:three", "Cloud backup (missing attachments)");
        var progress = catalog.ReadProgress(true, "account-one");
        Assert.AreEqual(1, progress.Completed); Assert.AreEqual(1, progress.Retrying); Assert.AreEqual(1, progress.Warnings);
        Assert.AreEqual(0, progress.Waiting); Assert.AreEqual(2, progress.Problems); Assert.IsFalse(progress.IsComplete);
        Assert.AreEqual("备份未完成 · 仍有条目需要处理", SessionProgressPresentation.Create(progress, true).Title);
        Assert.IsTrue(SessionProgressPresentation.Create(progress, false).Summary.Contains("retrying"));
        Assert.AreEqual(2, catalog.List().Count(x => x.Selected && SessionSelection.HasIssue(x)));
        Assert.AreEqual(1, Catalog().ReadProgress(true, "account-one").Completed, "Restart must retain useful progress.");
    }
    [TestMethod]
    public void Completion_IsExplicitAndLocalOnlyDoesNotClaimCloudSuccess()
    {
        var catalog = Catalog(); catalog.Merge([Item("one")]); catalog.Select("Imported:one", true);
        LocalVersion(catalog, "Imported:one", "v1"); catalog.FinishJob("Imported:one");
        var local = catalog.ReadProgress(false, null);
        Assert.IsTrue(local.IsComplete); Assert.AreEqual(100d, local.Percentage);
        Assert.IsTrue(SessionProgressPresentation.Create(local, true).Title.Contains("尚未连接云端"));
        Assert.IsFalse(catalog.ReadProgress(true, "account-one").IsComplete);
        catalog.MarkUploaded("v1", "account-one", "remote-v1"); catalog.SetStatus("Imported:one", "Cloud backup");
        var cloud = catalog.ReadProgress(true, "account-one"); Assert.IsTrue(cloud.IsComplete);
        Assert.AreEqual("云端备份已完成", SessionProgressPresentation.Create(cloud, true).Title);
        Assert.AreEqual("Cloud backup complete", SessionProgressPresentation.Create(cloud, false).Title);
        catalog.Select("Imported:one", false); Assert.AreEqual(0, catalog.ReadProgress(true, "account-one").Total);
    }
    [TestMethod]
    public void InFlightBytes_ProgressWithoutPrematureOneHundredPercent()
    {
        var current = new SessionBackupActivity("demo", "Demonstration", SessionBackupStage.Upload, 512, 1024);
        var progress = new SessionBackupProgress(2, 1, 2, 1, 1, 0, 0, 1, true, false, current);
        Assert.IsTrue(progress.Percentage > 50 && progress.Percentage < 100);
        var text = SessionProgressPresentation.Create(progress, false); Assert.IsTrue(text.Current.Contains("512.0 B / 1.0 KB"));
        Assert.IsTrue(text.Current.Contains("Uploading"));
        var finalizing = progress with { Total = 10000, Completed = 9999, Current = current with { Stage = SessionBackupStage.Confirm } };
        Assert.IsTrue(finalizing.Percentage <= 99.9); Assert.IsFalse(SessionProgressPresentation.Create(finalizing, false).Percentage.StartsWith("100"));
        Assert.AreEqual("备份已暂停", SessionProgressPresentation.Create(progress with { Paused = true }, true).Title);
    }
    [TestMethod]
    public async Task AutomaticScan_DoesNotResetFinishedProgressOrRetryDeadlines()
    {
        var catalog = Catalog(); var one = Item("one"); var two = Item("two"); catalog.Merge([one, two]);
        catalog.SetSelection([one.Key, two.Key], true); CloudVersion(catalog, one.Key, "v1");
        catalog.SetStatus(two.Key, "Retry pending", "Synthetic retry"); catalog.RetryJob(two.Key);
        string? Due()
        { using var db = SessionSnapshot.OpenRead(catalog.DatabasePath); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT due_at FROM jobs WHERE session_key='Imported:two'"; return cmd.ExecuteScalar() as string; }
        var due = Due(); var source = new FixtureSource([one, two]);
        var service = new SessionArchiveService(catalog, [source], new ControlledCloud());
        await service.ScanAsync(); Assert.AreEqual(due, Due()); Assert.AreEqual(1, service.GetProgress().Completed);
        service.Paused = true; service.Paused = false; await Task.Delay(30);
        Assert.AreEqual(1, service.GetProgress().Completed, "Resume must not reset already confirmed progress.");
        Assert.AreEqual(due, Due(), "Resume must retain existing retry state.");
        File.SetLastWriteTimeUtc(one.Path!, DateTime.UtcNow.AddSeconds(3));
        await service.ScanAsync(); Assert.AreEqual(0, service.GetProgress().Completed, "A changed source must be checked again.");
        Assert.IsNotNull(catalog.NextJob()); Assert.AreEqual(due, Due());
    }
    [TestMethod]
    public async Task CurrentTransfer_UpdatesOnAcknowledgementsAndWaitsForManifest()
    {
        var catalog = Catalog(); var item = Item("one"); catalog.Merge([item]); catalog.Select(item.Key, true);
        var cloud = new ControlledCloud { HoldTransfer = true }; var service = new SessionArchiveService(catalog, [new FixtureSource([item])], cloud);
        var backup = service.BackupAsync(item.Key);
        try
        {
            await cloud.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var uploading = service.GetProgress(); Assert.AreEqual(SessionBackupStage.Upload, uploading.Current!.Stage);
            Assert.IsTrue(uploading.Current.CompletedBytes > 0); Assert.IsTrue(uploading.Current.CompletedBytes < uploading.Current.TotalBytes);
            Assert.AreEqual(0, uploading.Completed); Assert.IsTrue(uploading.Percentage > 0 && uploading.Percentage < 100);
            cloud.ReleaseTransfer.TrySetResult(); await cloud.ManifestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(SessionBackupStage.Confirm, service.GetProgress().Current!.Stage);
            Assert.IsFalse(service.GetProgress().IsComplete);
            cloud.ReleaseManifest.TrySetResult(); await backup;
            Assert.IsNull(service.GetProgress().Current); catalog.FinishJob(item.Key); Assert.IsTrue(service.GetProgress().IsComplete);
        }
        finally { cloud.ReleaseTransfer.TrySetResult(); cloud.ReleaseManifest.TrySetResult(); await backup; }
    }
    [TestMethod]
    public void RetryProblems_DoesNotRequeueHealthyBackups()
    {
        var catalog = Catalog(); var one = Item("one"); var two = Item("two"); catalog.Merge([one, two]);
        catalog.SetSelection([one.Key, two.Key], true); CloudVersion(catalog, one.Key, "v1");
        catalog.SetStatus(two.Key, "Retry pending", "Synthetic error"); catalog.RetryJob(two.Key);
        var service = new SessionArchiveService(catalog, [new FixtureSource([one, two])], new ControlledCloud());
        service.QueueProblems(); Assert.AreEqual(two.Key, catalog.NextJob()); Assert.AreEqual(1, service.GetProgress().Completed);
    }

    private sealed class FixtureSource(IReadOnlyList<SessionDescriptor> items) : ISessionSourceAdapter
    {
        public string Name => "Imported";
        public Task<IReadOnlyList<SessionDescriptor>> DiscoverAsync(CancellationToken ct) => Task.FromResult(items);
        public async Task<IReadOnlyList<SnapshotFile>> SnapshotAsync(SessionDescriptor item, string staging, CancellationToken ct)
            => [await SessionSnapshot.CopyAsync(item.Path!, staging, "sample.txt", ct)];
    }
    private sealed class ControlledCloud : ISessionCloud, ISessionTransferProgressSource
    {
        public bool IsConnected => true; public string? ConfirmedAccountId => "account-one"; public bool HoldTransfer;
        public event Action<long, long>? TransferProgress;
        public TaskCompletionSource UploadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseTransfer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ManifestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseManifest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DriveAccount> GetAccountAsync(CancellationToken ct) => Task.FromResult(new DriveAccount("account-one", "Demo", 100000000, 0));
        public async Task<string> UploadObjectAsync(string hash, string path, CancellationToken ct)
        {
            var length = new FileInfo(path).Length; TransferProgress?.Invoke(length / 2, length); UploadStarted.TrySetResult();
            if (HoldTransfer) await ReleaseTransfer.Task.WaitAsync(ct);
            TransferProgress?.Invoke(length, length); return "remote-" + hash;
        }
        public async Task<string> UploadManifestAsync(SessionManifest manifest, string path, CancellationToken ct)
        { ManifestStarted.TrySetResult(); if (HoldTransfer) await ReleaseManifest.Task.WaitAsync(ct); return "remote-manifest"; }
        public Task DownloadObjectAsync(string fileId, string destination, CancellationToken ct) => throw new NotSupportedException();
    }
}
