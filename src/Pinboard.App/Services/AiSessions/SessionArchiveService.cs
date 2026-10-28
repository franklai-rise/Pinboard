using System.Security.Cryptography;
using System.Net.Http;
using System.Text.Json;

namespace Pinboard.App.Services.AiSessions;

public sealed class SessionArchiveService
{
    public const int ChunkSize = 4 * 1024 * 1024;
    public SessionCatalog Catalog { get; }
    private readonly IReadOnlyList<ISessionSourceAdapter> _sources;
    private readonly ISessionCloud? _cloud;
    private readonly SemaphoreSlim _workGate = new(1, 1);
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private volatile bool _paused;
    private SessionBackupActivity? _current;
    private long _uploadBase;
    private long _nextScanTicks;
    public bool Paused
    {
        get => _paused;
        set
        {
            var resume = _paused && !value; _paused = value; Changed?.Invoke(this, EventArgs.Empty);
            if (resume) Interlocked.Exchange(ref _nextScanTicks, 0);
        }
    }
    public string Activity { get; private set; } = "Ready";
    public event EventHandler? Changed;
    public ImportedSessionSource Imports { get; }
    public SessionArchiveService(SessionCatalog catalog, IReadOnlyList<ISessionSourceAdapter> sources, ISessionCloud? cloud = null)
    {
        Catalog = catalog; _sources = sources; _cloud = cloud;
        Imports = sources.OfType<ImportedSessionSource>().FirstOrDefault() ?? new ImportedSessionSource(Path.Combine(catalog.Root, "Imports"));
        if (cloud is ISessionTransferProgressSource progress) progress.TransferProgress += UpdateUploadProgress;
    }
    public SessionBackupProgress GetProgress() => Catalog.ReadProgress(_cloud?.IsConnected == true,
        _cloud?.ConfirmedAccountId, Paused, Volatile.Read(ref _current));
    public Task<SessionArchiveAudit> InspectAsync(bool includeCloud = true, CancellationToken ct = default)
        => SessionArchiveDiagnostics.InspectAsync(this, _cloud, includeCloud, ct);
    private void UpdateUploadProgress(long acknowledged, long total)
    {
        var current = Volatile.Read(ref _current);
        if (current is not { Stage: SessionBackupStage.Upload }) return;
        var completed = Interlocked.Read(ref _uploadBase) + Math.Clamp(acknowledged, 0, Math.Max(0, total));
        Volatile.Write(ref _current, current with { CompletedBytes = Math.Min(completed, current.TotalBytes) });
    }
    public static IReadOnlyList<ISessionSourceAdapter> LocalSources(string archiveRoot, string? userProfile = null)
    {
        var user = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return [new CodexSessionSource(Path.Combine(user, ".codex")), new ClaudeSessionSource(Path.Combine(user, ".claude", "projects")),
            new AntigravitySessionSource(Path.Combine(user, ".gemini", "antigravity")), new ImportedSessionSource(Path.Combine(archiveRoot, "Imports"))];
    }
    public void Start() => _worker ??= Task.Run(RunAsync);
    public async Task StopAsync()
    {
        await _stop.CancelAsync();
        if (_worker is not null) try { await _worker; } catch (OperationCanceledException) { }
    }
    private void Notify(string activity) { Activity = activity; Changed?.Invoke(this, EventArgs.Empty); }
    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var processed = false;
            try
            {
                if (!Paused && DateTimeOffset.UtcNow.UtcTicks >= Interlocked.Read(ref _nextScanTicks))
                {
                    await ScanAsync(_stop.Token); Interlocked.Exchange(ref _nextScanTicks, DateTimeOffset.UtcNow.AddMinutes(5).UtcTicks);
                }
                if (!Paused && Catalog.NextJob() is { } key)
                {
                    processed = true;
                    try { await BackupAsync(key, _stop.Token); Catalog.FinishJob(key); Changed?.Invoke(this, EventArgs.Empty); }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        var kind = SessionRetryPolicy.Classify(ex);
                        Catalog.SetStatus(key, SessionRetryPolicy.Status(kind), FriendlyFailure(ex)); Catalog.RetryJob(key, kind);
                        Notify("Backup needs attention");
                    }
                }
                await Task.Delay(processed ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(2), _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) { Notify("Library unavailable: " + FriendlyFailure(ex)); await Task.Delay(5000, _stop.Token); }
        }
    }
    public static string FriendlyFailure(Exception ex) => ex switch
    {
        DriveQuotaException => "Google Drive storage is full. Local backup is retained.",
        DriveAuthorizationException => "Google Drive needs sign-in again. Local backup is retained.",
        HttpRequestException => "Google Drive is unavailable. Upload will retry.",
        _ => ex.Message
    };
    public async Task ScanAsync(CancellationToken ct = default)
    {
        await _scanGate.WaitAsync(ct);
        try
        {
            Notify("Scanning titles…"); var failures = new List<string>();
            var previous = Catalog.List().ToDictionary(x => x.Key, x => x.Descriptor, StringComparer.Ordinal);
            foreach (var source in _sources)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var items = await source.DiscoverAsync(ct); Catalog.Merge(items);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failures.Add(source.Name + ": " + FriendlyFailure(ex)); }
            }
            var stored = Catalog.StoredSessionKeys(_cloud?.IsConnected == true, _cloud?.ConfirmedAccountId);
            var unfinished = new List<string>(); var changed = new List<string>();
            foreach (var entry in Catalog.List().Where(x => x.Selected))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var sourceChanged = !previous.TryGetValue(entry.Key, out var old) || old != entry.Descriptor
                        || (entry.LastBackup is not null && SourceChangedSinceBackup(entry));
                    if (!sourceChanged && entry.Error is not null && File.Exists(entry.Descriptor.Path)
                        && Catalog.GetJob(entry.Key) is { Blocked: true, FailureKind: SessionFailureKind.MissingSource }) sourceChanged = true;
                    if (!sourceChanged && entry.Status.Contains("missing attachments", StringComparison.Ordinal)
                        && Catalog.Versions(entry.Key).FirstOrDefault() is { } version)
                        sourceChanged = await AttachmentsRecoveredAsync(await ReadManifestAsync(version.ManifestPath, ct), ct);
                    if (sourceChanged) changed.Add(entry.Key); else if (!stored.Contains(entry.Key)) unfinished.Add(entry.Key);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
                {
                    Catalog.SetStatus(entry.Key, "Needs review", "Local backup metadata needs review. Existing versions are retained.");
                    Catalog.QueueMany([entry.Key], restartExisting: false); Catalog.RetryJob(entry.Key, SessionFailureKind.Review);
                }
            }
            // Partial backups need attention, not repeated multi-GB snapshots of unchanged files.
            // Only actual source changes unblock a source-dependent failure automatically.
            Catalog.QueueMany(unfinished, restartExisting: false); Catalog.QueueMany(changed);
            Notify(failures.Count == 0 ? "Ready" : string.Join(" · ", failures));
        }
        finally { _scanGate.Release(); }
    }
    public void QueueSelected()
    {
        var stored = Catalog.StoredSessionKeys(_cloud?.IsConnected == true, _cloud?.ConfirmedAccountId);
        Catalog.QueueMany(Catalog.List().Where(x => x.Selected && (!stored.Contains(x.Key) || SourceChangedSinceBackup(x))).Select(x => x.Key));
        Notify(Paused ? "Paused" : "Selected conversations queued");
    }
    public void QueueProblems()
    {
        Catalog.QueueMany(Catalog.List().Where(entry => entry.Selected && SessionSelection.HasIssue(entry)).Select(entry => entry.Key));
        Notify(Paused ? "Paused" : "Selected conversations queued");
    }
    private static bool SourceChangedSinceBackup(SessionEntry entry)
    {
        if (entry.LastBackup is not { } last || entry.Descriptor.Path is not { } path) return true;
        try
        {
            return (File.Exists(path) && File.GetLastWriteTimeUtc(path) > last.UtcDateTime)
                || (File.Exists(path + "-wal") && File.GetLastWriteTimeUtc(path + "-wal") > last.UtcDateTime);
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
    public async Task BackupAsync(string key, CancellationToken ct = default)
    {
        await _workGate.WaitAsync(ct);
        try
        {
            var entry = Catalog.Find(key); if (entry is null || !entry.Selected || Paused) return;
            Volatile.Write(ref _current, new(key, entry.Title, SessionBackupStage.Snapshot));
            var source = _sources.FirstOrDefault(x => x.Name == entry.Descriptor.Source) ?? Imports;
            if (!File.Exists(entry.Descriptor.Path) && source is not CodexSessionSource)
                throw new FileNotFoundException("Conversation source is missing. Existing backups are retained.");
            var versions = Catalog.Versions(key); var latest = versions.FirstOrDefault(); SessionManifest? manifest = null;
            // Do not reread gigabytes of unchanged recordings every five minutes.
            if (latest is not null)
            {
                var previous = await ReadManifestAsync(latest.ManifestPath, ct);
                if (previous.Session.UpdatedAt == entry.Descriptor.UpdatedAt && previous.Session.Size == entry.Descriptor.Size
                    && previous.Session.Title == entry.Descriptor.Title
                    && previous.Session.Availability == entry.Descriptor.Availability
                    && (!File.Exists(entry.Descriptor.Path) || File.GetLastWriteTimeUtc(entry.Descriptor.Path!) <= previous.CreatedAt.UtcDateTime)
                    && (!File.Exists(entry.Descriptor.Path + "-wal") || File.GetLastWriteTimeUtc(entry.Descriptor.Path + "-wal") <= previous.CreatedAt.UtcDateTime)
                    && !await AttachmentsRecoveredAsync(previous, ct)) manifest = previous;
            }
            if (manifest is null)
            {
                Catalog.SetStatus(key, "Backing up"); Notify("Creating local snapshot…");
                var staging = Path.Combine(Catalog.Root, ".staging", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
                try
                {
                    var snapshot = await source.SnapshotAsync(entry.Descriptor, staging, ct);
                    var files = new List<ArchiveFile>();
                    var snapshotSize = snapshot.Sum(file => new FileInfo(file.Path).Length); long stored = 0;
                    Volatile.Write(ref _current, new(key, entry.Title, SessionBackupStage.LocalStore, 0, snapshotSize));
                    foreach (var file in snapshot)
                    {
                        ct.ThrowIfCancellationRequested();
                        var saved = await StoreFileAsync(file, bytes => Volatile.Write(ref _current,
                            new(key, entry.Title, SessionBackupStage.LocalStore, stored + bytes, snapshotSize)), ct);
                        files.Add(saved); stored += saved.Length;
                    }
                    if (Catalog.Find(key)?.Selected != true || Paused) return;
                    var previous = latest is null ? null : await ReadManifestAsync(latest.ManifestPath, ct);
                    if (previous is not null && FilesEqual(previous.Files, files)) manifest = previous;
                    else
                    {
                        manifest = new(1, Guid.NewGuid().ToString("N"), entry.Descriptor, DateTimeOffset.UtcNow, files,
                            SnapshotWarnings(snapshot));
                        var path = ManifestPath(manifest.VersionId); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        await WriteDurablyAsync(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(manifest, SessionJson.Options), ct);
                        File.Move(path + ".tmp", path, true); Catalog.AddVersion(manifest, path);
                    }
                }
                finally { Directory.Delete(staging, recursive: true); }
            }
            if (_cloud is { IsConnected: true } && Catalog.Find(key)?.Selected == true && !Paused)
            {
                var account = await _cloud.GetAccountAsync(ct);
                // Also upload historical local versions, oldest first, when a connection becomes available.
                foreach (var version in Catalog.Versions(key).Reverse())
                {
                    if (version.CloudAccount == account.Id && !string.IsNullOrEmpty(version.CloudFileId)) continue;
                    if (Catalog.Find(key)?.Selected != true || Paused) return;
                    var saved = await ReadManifestAsync(version.ManifestPath, ct);
                    var missing = saved.Files.SelectMany(x => x.Chunks).DistinctBy(x => x.Hash)
                        .Where(x => string.IsNullOrEmpty(Catalog.GetObject(account.Id, x.Hash)?.FileId)).ToArray();
                    if (account.Available is { } available && missing.Sum(x => (long)x.Length) + new FileInfo(version.ManifestPath).Length > available)
                        throw new DriveQuotaException();
                    Catalog.SetStatus(key, "Uploading"); Notify("Uploading selected conversation…");
                    var totalBytes = missing.Sum(x => (long)x.Length); long uploadedBytes = 0;
                    Volatile.Write(ref _current, new(key, entry.Title, SessionBackupStage.Upload, 0, totalBytes));
                    foreach (var chunk in missing)
                    {
                        if (Catalog.Find(key)?.Selected != true || Paused) return;
                        Interlocked.Exchange(ref _uploadBase, uploadedBytes);
                        await _cloud.UploadObjectAsync(chunk.Hash, ObjectPath(chunk.Hash), ct);
                        uploadedBytes += chunk.Length;
                        Volatile.Write(ref _current, new(key, entry.Title, SessionBackupStage.Upload, uploadedBytes, totalBytes));
                        var percentage = totalBytes == 0 ? 100 : (int)(uploadedBytes * 100 / totalBytes);
                        Catalog.SetStatus(key, $"Uploading {percentage}%"); Notify($"Uploading selected conversation… {percentage}%");
                    }
                    if (Catalog.Find(key)?.Selected != true || Paused) return;
                    Volatile.Write(ref _current, new(key, entry.Title, SessionBackupStage.Confirm, totalBytes, totalBytes));
                    var remoteId = await _cloud.UploadManifestAsync(saved, version.ManifestPath, ct);
                    Catalog.MarkUploaded(version.Id, account.Id, remoteId);
                }
                Catalog.SetStatus(key, BackupStatus(manifest, cloud: true));
            }
            else Catalog.SetStatus(key, BackupStatus(manifest, cloud: false));
            Notify("Ready");
        }
        finally { Volatile.Write(ref _current, null); _workGate.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }
    public string ObjectPath(string hash)
    {
        if (hash.Length != 64 || hash.Any(x => !Uri.IsHexDigit(x))) throw new InvalidDataException("Invalid archive object hash.");
        return Path.Combine(Catalog.Root, "Objects", hash[..2], hash + ".bin");
    }
    public string ManifestPath(string id) => Path.Combine(Catalog.Root, "Versions", id + ".json");
    private async Task<ArchiveFile> StoreFileAsync(SnapshotFile file, Action<long> progress, CancellationToken ct)
    {
        await using var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var chunks = new List<ArchiveChunk>();
        var buffer = new byte[ChunkSize]; long size = 0;
        while (true)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await input.ReadAsync(buffer.AsMemory(read), ct); if (n == 0) break; read += n;
            }
            if (read == 0) break;
            digest.AppendData(buffer, 0, read); var hash = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read))).ToLowerInvariant();
            var target = ObjectPath(hash);
            if (!File.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await WriteDurablyAsync(target + ".tmp", buffer.AsMemory(0, read), ct);
                File.Move(target + ".tmp", target, true);
            }
            chunks.Add(new(hash, read)); size += read;
            progress(size);
        }
        return new(file.RelativePath, size, Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant(), chunks);
    }
    private static async Task WriteDurablyAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await output.WriteAsync(bytes, ct); await output.FlushAsync(ct); output.Flush(true);
    }
    private static bool FilesEqual(IReadOnlyList<ArchiveFile> a, IReadOnlyList<ArchiveFile> b) => a.Count == b.Count
        && a.OrderBy(x => x.RelativePath, StringComparer.Ordinal).Zip(b.OrderBy(x => x.RelativePath, StringComparer.Ordinal))
            .All(pair => pair.First.RelativePath == pair.Second.RelativePath && pair.First.Length == pair.Second.Length && pair.First.Sha256 == pair.Second.Sha256);
    private static IReadOnlyList<string>? SnapshotWarnings(IReadOnlyList<SnapshotFile> files)
    {
        var warnings = new List<string>();
        if (files.Any(x => x.IncompleteSource)) warnings.Add("Original conversation source is missing. Available selected history was saved; this is not a complete backup. See pinboard-source-report.json.");
        if (files.Any(x => x.IncompleteAttachments)) warnings.Add("Some referenced images are missing. See pinboard-attachment-report.json.");
        return warnings.Count > 0 ? warnings : null;
    }
    public static string BackupStatus(SessionManifest manifest, bool cloud)
    {
        var prefix = cloud ? "Cloud backup" : "Local backup";
        var source = manifest.Files.Any(x => x.RelativePath == "pinboard-source-report.json");
        var attachments = manifest.Files.Any(x => x.RelativePath == "pinboard-attachment-report.json");
        return source && attachments ? prefix + " (missing source, missing attachments)"
            : source ? prefix + " (missing source)" : attachments || manifest.Warnings is { Count: > 0 } ? prefix + " (missing attachments)" : prefix;
    }
    public async Task<IReadOnlyList<string>> MissingAttachmentsAsync(SessionManifest manifest, CancellationToken ct = default)
    {
        var report = manifest.Files.FirstOrDefault(x => x.RelativePath == "pinboard-attachment-report.json");
        if (report is null) return [];
        if (report.Length > 4 * 1024 * 1024) throw new InvalidDataException("Attachment report is too large to inspect safely.");
        using var output = new MemoryStream();
        foreach (var chunk in report.Chunks)
        {
            var bytes = await File.ReadAllBytesAsync(ObjectPath(chunk.Hash), ct);
            if (bytes.Length != chunk.Length || Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != chunk.Hash)
                throw new InvalidDataException("Attachment report checksum did not match.");
            await output.WriteAsync(bytes, ct);
        }
        if (output.Length != report.Length || Convert.ToHexString(SHA256.HashData(output.ToArray())).ToLowerInvariant() != report.Sha256)
            throw new InvalidDataException("Attachment report checksum did not match.");
        return JsonSerializer.Deserialize<string[]>(output.ToArray()) ?? [];
    }
    private async Task<bool> AttachmentsRecoveredAsync(SessionManifest manifest, CancellationToken ct)
        => (await MissingAttachmentsAsync(manifest, ct)).Any(File.Exists);
    public static async Task<SessionManifest> ReadManifestAsync(string path, CancellationToken ct = default)
    {
        try
        {
            await using var input = File.OpenRead(path);
            var manifest = await JsonSerializer.DeserializeAsync<SessionManifest>(input, cancellationToken: ct) ?? throw new InvalidDataException("Missing backup manifest.");
            if (manifest.SchemaVersion != 1) throw new InvalidDataException("Unsupported backup format."); return manifest;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { throw new InvalidDataException("Local backup manifest is missing. Existing versions are retained; restore its metadata from a backup.", ex); }
    }
    public async Task<string> RestoreAsync(SessionVersion version, string destination, bool fromCloud, CancellationToken ct = default)
    {
        var manifest = await ReadManifestAsync(version.ManifestPath, ct);
        DriveAccount? account = null;
        if (fromCloud)
        {
            if (_cloud is not { IsConnected: true }) throw new DriveAuthorizationException();
            account = await _cloud.GetAccountAsync(ct);
            if (account.Id != version.CloudAccount) throw new InvalidOperationException("Connect the Google account used for this backup.");
        }
        var root = Path.Combine(Path.GetFullPath(destination), "Pinboard-Restore-" + version.Id);
        if (Directory.Exists(root)) throw new IOException("Choose another folder; this restored version already exists.");
        // Validate every destination before creating any output files.
        foreach (var file in manifest.Files) _ = SafeRestorePath(root, file.RelativePath);
        Directory.CreateDirectory(root);
        if (fromCloud)
        {
            if (version.CloudFileId is null) throw new InvalidDataException("No cloud manifest is available for this version.");
            var cloudManifest = Path.Combine(root, ".cloud-manifest.json");
            await _cloud!.DownloadObjectAsync(version.CloudFileId, cloudManifest, ct);
            await using (var local = File.OpenRead(version.ManifestPath))
            await using (var remote = File.OpenRead(cloudManifest))
            {
                var localHash = await SHA256.HashDataAsync(local, ct); var remoteHash = await SHA256.HashDataAsync(remote, ct);
                if (!CryptographicOperations.FixedTimeEquals(localHash, remoteHash))
                    throw new InvalidDataException("The cloud backup manifest does not match the local version.");
            }
            File.Delete(cloudManifest);
        }
        foreach (var file in manifest.Files)
        {
            var target = SafeRestorePath(root, file.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var output = new FileStream(target + ".partial", FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long size = 0;
            foreach (var chunk in file.Chunks)
            {
                ct.ThrowIfCancellationRequested(); var path = ObjectPath(chunk.Hash); string? download = null;
                if (fromCloud)
                {
                    var remote = Catalog.GetObject(account!.Id, chunk.Hash);
                    if (remote is null || string.IsNullOrEmpty(remote.FileId))
                    {
                        var id = await _cloud!.FindObjectAsync(chunk.Hash, ct) ?? throw new InvalidDataException("Cloud object is missing.");
                        remote = new(chunk.Hash, id, null, chunk.Length); Catalog.PutObject(account.Id, remote);
                    }
                    download = Path.Combine(root, ".download-" + chunk.Hash);
                    await _cloud!.DownloadObjectAsync(remote.FileId, download, ct); path = download;
                }
                if (chunk.Length < 0 || chunk.Length > ChunkSize || new FileInfo(path).Length != chunk.Length)
                    throw new InvalidDataException("Backup object size verification failed.");
                var bytes = await File.ReadAllBytesAsync(path, ct);
                if (bytes.Length != chunk.Length || Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != chunk.Hash)
                    throw new InvalidDataException("Backup hash verification failed. The incomplete restore is retained for inspection.");
                await output.WriteAsync(bytes, ct); digest.AppendData(bytes); size += bytes.Length;
                if (download is not null) File.Delete(download);
            }
            if (size != file.Length || Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant() != file.Sha256)
                throw new InvalidDataException("Restored file hash verification failed.");
            await output.FlushAsync(ct); output.Flush(true); await output.DisposeAsync(); File.Move(target + ".partial", target);
        }
        await File.WriteAllTextAsync(Path.Combine(root, "pinboard-backup-manifest.json"), JsonSerializer.Serialize(manifest, SessionJson.Options), ct);
        return root;
    }
    public async Task<int> RecoverCloudIndexAsync(CancellationToken ct = default)
    {
        if (_cloud is not { IsConnected: true }) throw new DriveAuthorizationException();
        var account = await _cloud.GetAccountAsync(ct); var files = await _cloud.ListManifestsAsync(ct); var count = 0;
        Directory.CreateDirectory(Path.Combine(Catalog.Root, "Versions"));
        foreach (var file in files)
        {
            var temporary = Path.Combine(Catalog.Root, "Versions", Guid.NewGuid().ToString("N") + ".download");
            try
            {
                await _cloud.DownloadObjectAsync(file.FileId, temporary, ct);
                await using (var input = File.OpenRead(temporary))
                    if (Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant() != file.Hash)
                        throw new InvalidDataException("Cloud manifest checksum verification failed.");
                var manifest = await ReadManifestAsync(temporary, ct);
                if (!Guid.TryParseExact(manifest.VersionId, "N", out _)) throw new InvalidDataException("Invalid backup version identifier.");
                foreach (var item in manifest.Files) _ = SafeRestorePath(Path.Combine(Catalog.Root, "RestoreValidation"), item.RelativePath);
                var target = ManifestPath(manifest.VersionId); File.Move(temporary, target, true);
                if (Catalog.Find(manifest.Session.Key) is null) Catalog.Merge([manifest.Session with { Availability = "Missing source" }]);
                Catalog.AddVersion(manifest, target); Catalog.MarkUploaded(manifest.VersionId, account.Id, file.FileId); count++;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        Changed?.Invoke(this, EventArgs.Empty); return count;
    }
    public static string SafeRestorePath(string root, string relative)
    {
        if (Path.IsPathFullyQualified(relative) || relative.Contains(':')) throw new InvalidDataException("Unsafe restore path.");
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe restore path.");
        return path;
    }
}
