using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pinboard.App.Models;

namespace Pinboard.App.Services;

public sealed class PinboardDocument
{
    private const int SchemaVersion = 2;
    private const string BrotliEncoding = "br-v1";
    private const double DefaultInboxX = 80;
    private const double DefaultInboxY = 80;
    private const double InboxImageMaxWidth = 1120;
    private const double InboxGap = 64;
    private const double TextInboxX = 80;
    private const double TextInboxY = 80;
    private const double TextInboxGap = 28;
    private const int CheckpointLimit = 20;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _displayTitle;
    private bool _initialized;

    public PinboardDocument(string path)
    {
        FilePath = Path.GetFullPath(path);
        _displayTitle = Path.GetFileNameWithoutExtension(FilePath);
    }

    public string FilePath { get; }
    public string Title => _displayTitle;
    public string FileName => Path.GetFileNameWithoutExtension(FilePath);
    public bool IsReadOnly { get; private set; }

    public static string ReadDisplayTitle(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var fallback = Path.GetFileNameWithoutExtension(fullPath);
        if (!File.Exists(fullPath))
        {
            return fallback;
        }

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = fullPath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 1
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key='title'";
            return command.ExecuteScalar() is string title && !string.IsNullOrWhiteSpace(title) ? title : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            if (File.Exists(FilePath) && File.GetAttributes(FilePath).HasFlag(FileAttributes.ReadOnly))
            {
                IsReadOnly = true;
                await using var readOnlyConnection = await OpenConnectionAsync(cancellationToken);
                var readOnlyVersion = await ReadAndValidateSchemaVersionAsync(readOnlyConnection, cancellationToken);
                await VerifySceneSchemaAsync(readOnlyConnection, readOnlyVersion, cancellationToken);
                await ValidateSceneExistsAsync(readOnlyConnection, cancellationToken);
                _displayTitle = await GetMetaStringAsync(readOnlyConnection, "title", FileName, cancellationToken);
                _initialized = true;
                return;
            }

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await ConfigureDatabaseAsync(connection, cancellationToken);

            if (!await TableExistsAsync(connection, "meta", cancellationToken))
            {
                if (await TableExistsAsync(connection, "scene_current", cancellationToken))
                {
                    throw new InvalidDataException("The Pinboard database has scene data but no schema_version metadata.");
                }

                await CreateVersionTwoDatabaseAsync(connection, cancellationToken);
            }
            else
            {
                var version = await ReadAndValidateSchemaVersionAsync(connection, cancellationToken);
                if (version == 1)
                {
                    await MigrateVersionOneToTwoAsync(connection, cancellationToken);
                }
                else if (version != SchemaVersion)
                {
                    throw new InvalidDataException($"Unsupported Pinboard schema version {version}. This application supports up to version {SchemaVersion}.");
                }

                await EnsureVersionTwoSchemaAsync(connection, rebuildSearchIndex: version == 1, cancellationToken);
            }

            await VerifySceneSchemaAsync(connection, SchemaVersion, cancellationToken);
            await EnsureDocumentDefaultsAsync(connection, cancellationToken);
            await ValidateSceneExistsAsync(connection, cancellationToken);
            _displayTitle = await GetMetaStringAsync(connection, "title", FileName, cancellationToken);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DocumentSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var version = await ReadAndValidateSchemaVersionAsync(connection, cancellationToken);
            var (scene, revision) = await ReadCurrentSceneAsync(connection, version, cancellationToken);

            var files = new List<AssetPayload>();
            await using var fileCommand = connection.CreateCommand();
            fileCommand.CommandText = """
                SELECT fm.file_id, a.hash, a.mime_type, a.width, a.height, a.bytes, fm.created_utc
                FROM file_map fm
                JOIN assets a ON a.hash = fm.asset_hash
                ORDER BY fm.created_utc;
                """;
            await using var fileReader = await fileCommand.ExecuteReaderAsync(cancellationToken);
            while (await fileReader.ReadAsync(cancellationToken))
            {
                var mime = fileReader.GetString(2);
                var bytes = (byte[])fileReader[5];
                var created = DateTimeOffset.TryParse(fileReader.GetString(6), out var createdAt)
                    ? createdAt.ToUnixTimeMilliseconds()
                    : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                files.Add(new AssetPayload(
                    fileReader.GetString(0),
                    fileReader.GetString(1),
                    mime,
                    fileReader.GetInt32(3),
                    fileReader.GetInt32(4),
                    ImageProcessor.ToDataUrl(mime, bytes),
                    created));
            }

            return new DocumentSnapshot(FilePath, Title, scene, files, revision);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<long> SaveSceneAsync(
        string sceneJson,
        IReadOnlyList<(string FileId, AssetRecord Asset)> newAssets,
        bool checkpoint = false,
        string checkpointReason = "autosave",
        CancellationToken cancellationToken = default) =>
        SaveSceneCoreAsync(sceneJson, newAssets, null, checkpoint, checkpointReason, cancellationToken);

    public Task<long> SaveSceneAsync(
        string sceneJson,
        IReadOnlyList<(string FileId, AssetRecord Asset)> newAssets,
        long expectedRevision,
        bool checkpoint = false,
        string checkpointReason = "autosave",
        CancellationToken cancellationToken = default) =>
        SaveSceneCoreAsync(sceneJson, newAssets, expectedRevision, checkpoint, checkpointReason, cancellationToken);

    public async Task<long> GetCurrentRevisionAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT revision FROM scene_current WHERE id=1";
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0, CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<DocumentCheckpointInfo>> ListCheckpointsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var version = await ReadAndValidateSchemaVersionAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = version == 1
                ? "SELECT revision, saved_utc, reason, length(CAST(scene_json AS BLOB)) FROM scene_revisions ORDER BY revision DESC"
                : "SELECT revision, saved_utc, reason, length(scene_data) FROM scene_revisions ORDER BY revision DESC";
            var checkpoints = new List<DocumentCheckpointInfo>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var savedAt = DateTimeOffset.TryParse(reader.GetString(1), out var parsed)
                    ? parsed
                    : DateTimeOffset.MinValue;
                checkpoints.Add(new DocumentCheckpointInfo(
                    reader.GetInt64(0),
                    savedAt,
                    reader.GetString(2),
                    reader.IsDBNull(3) ? 0 : reader.GetInt64(3)));
            }
            return checkpoints;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> LoadCheckpointSceneAsync(long revision, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var version = await ReadAndValidateSchemaVersionAsync(connection, cancellationToken);
            return await ReadCheckpointSceneAsync(connection, null, version, revision, cancellationToken)
                ?? throw new KeyNotFoundException($"Checkpoint revision {revision} does not exist.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> RestoreCheckpointAsync(
        long checkpointRevision,
        string reason = "restore",
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        EnsureWritable();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction();
            var scene = await ReadCheckpointSceneAsync(connection, transaction, SchemaVersion, checkpointRevision, cancellationToken)
                ?? throw new KeyNotFoundException($"Checkpoint revision {checkpointRevision} does not exist.");
            var revision = await GetRevisionAsync(connection, transaction, cancellationToken) + 1;
            await UpdateSceneAsync(connection, transaction, scene, revision, cancellationToken);
            await ReindexTextIncrementallyAsync(connection, transaction, scene, cancellationToken);
            await InsertCheckpointAsync(connection, transaction, revision, scene, $"{reason}:{checkpointRevision}", cancellationToken);
            await TrimCheckpointsAsync(connection, transaction, cancellationToken);
            var now = DateTimeOffset.UtcNow.ToString("O");
            await SetMetaAsync(connection, transaction, "modified_utc", now, cancellationToken);
            await SetMetaAsync(connection, transaction, "last_checkpoint_utc", now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return revision;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DocumentOptimizeResult> OptimizeAsync(bool vacuum = true, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        EnsureWritable();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var beforeBytes = new FileInfo(FilePath).Length;
            var removedMappings = 0;
            var removedAssets = 0;

            await using (var connection = await OpenConnectionAsync(cancellationToken))
            {
                await using var transaction = connection.BeginTransaction();
                var protectedFileIds = new HashSet<string>(StringComparer.Ordinal);
                var currentScene = await GetSceneAsync(connection, transaction, cancellationToken);
                CollectReferencedFileIds(currentScene, protectedFileIds);

                await using (var revisionCommand = connection.CreateCommand())
                {
                    revisionCommand.Transaction = transaction;
                    revisionCommand.CommandText = "SELECT scene_data, scene_encoding FROM scene_revisions";
                    await using var reader = await revisionCommand.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        CollectReferencedFileIds(DecodeScene((byte[])reader[0], reader.GetString(1)), protectedFileIds);
                    }
                }

                var mappingsToRemove = new List<string>();
                await using (var mapCommand = connection.CreateCommand())
                {
                    mapCommand.Transaction = transaction;
                    mapCommand.CommandText = "SELECT file_id FROM file_map";
                    await using var reader = await mapCommand.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var fileId = reader.GetString(0);
                        if (!protectedFileIds.Contains(fileId))
                        {
                            mappingsToRemove.Add(fileId);
                        }
                    }
                }

                foreach (var fileId in mappingsToRemove)
                {
                    await using var delete = connection.CreateCommand();
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM file_map WHERE file_id=$fileId";
                    delete.Parameters.AddWithValue("$fileId", fileId);
                    removedMappings += await delete.ExecuteNonQueryAsync(cancellationToken);
                }

                var orphanHashes = new List<string>();
                await using (var orphanCommand = connection.CreateCommand())
                {
                    orphanCommand.Transaction = transaction;
                    orphanCommand.CommandText = "SELECT hash FROM assets WHERE NOT EXISTS (SELECT 1 FROM file_map WHERE asset_hash=assets.hash)";
                    await using var reader = await orphanCommand.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        orphanHashes.Add(reader.GetString(0));
                    }
                }

                foreach (var hash in orphanHashes)
                {
                    await using (var deleteSearch = connection.CreateCommand())
                    {
                        deleteSearch.Transaction = transaction;
                        deleteSearch.CommandText = """
                            DELETE FROM search_content WHERE kind='ocr' AND substr(ref_id, 1, $length)=$hash;
                            DELETE FROM search_fts WHERE kind='ocr' AND substr(ref_id, 1, $length)=$hash;
                            """;
                        deleteSearch.Parameters.AddWithValue("$length", hash.Length);
                        deleteSearch.Parameters.AddWithValue("$hash", hash);
                        await deleteSearch.ExecuteNonQueryAsync(cancellationToken);
                    }
                    await using (var deleteOcr = connection.CreateCommand())
                    {
                        deleteOcr.Transaction = transaction;
                        deleteOcr.CommandText = "DELETE FROM ocr WHERE asset_hash=$hash";
                        deleteOcr.Parameters.AddWithValue("$hash", hash);
                        await deleteOcr.ExecuteNonQueryAsync(cancellationToken);
                    }
                    await using (var deleteAsset = connection.CreateCommand())
                    {
                        deleteAsset.Transaction = transaction;
                        deleteAsset.CommandText = "DELETE FROM assets WHERE hash=$hash";
                        deleteAsset.Parameters.AddWithValue("$hash", hash);
                        removedAssets += await deleteAsset.ExecuteNonQueryAsync(cancellationToken);
                    }
                }

                await transaction.CommitAsync(cancellationToken);
                await using var optimize = connection.CreateCommand();
                optimize.CommandText = "PRAGMA optimize;";
                await optimize.ExecuteNonQueryAsync(cancellationToken);
                if (vacuum)
                {
                    await using var vacuumCommand = connection.CreateCommand();
                    vacuumCommand.CommandText = "VACUUM;";
                    await vacuumCommand.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            var fileInfo = new FileInfo(FilePath);
            fileInfo.Refresh();
            return new DocumentOptimizeResult(removedMappings, removedAssets, beforeBytes, fileInfo.Length, vacuum);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<InsertResult> InsertCapturedImageAsync(
        AssetRecord asset,
        string source,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        EnsureWritable();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction();

            var inboxX = await GetMetaDoubleAsync(connection, transaction, "inbox_x", DefaultInboxX, cancellationToken);
            var legacyCursorY = await GetMetaDoubleAsync(connection, transaction, "inbox_cursor_y", DefaultInboxY, cancellationToken);
            var firstColumnY = await GetMetaDoubleAsync(connection, transaction, "inbox_column_0_y", legacyCursorY, cancellationToken);
            var secondColumnY = await GetMetaDoubleAsync(connection, transaction, "inbox_column_1_y", legacyCursorY, cancellationToken);
            var useFirstColumn = firstColumnY <= secondColumnY;
            var x = inboxX + (useFirstColumn ? 0 : InboxImageMaxWidth + InboxGap);
            var y = useFirstColumn ? firstColumnY : secondColumnY;
            var scene = await GetSceneAsync(connection, transaction, cancellationToken);
            var fileId = asset.Hash[..Math.Min(40, asset.Hash.Length)];
            var result = SceneBuilder.AppendImage(scene, fileId, asset, x, y, source);

            await UpsertAssetAsync(connection, transaction, asset, cancellationToken);
            await UpsertFileMapAsync(connection, transaction, fileId, asset.Hash, cancellationToken);
            var revision = await GetRevisionAsync(connection, transaction, cancellationToken) + 1;
            await UpdateSceneAsync(connection, transaction, result.SceneJson, revision, cancellationToken);
            var updatedColumnY = y + result.Height + InboxGap;
            if (useFirstColumn)
            {
                firstColumnY = updatedColumnY;
            }
            else
            {
                secondColumnY = updatedColumnY;
            }
            await SetMetaAsync(connection, transaction, "inbox_column_0_y", firstColumnY.ToString(CultureInfo.InvariantCulture), cancellationToken);
            await SetMetaAsync(connection, transaction, "inbox_column_1_y", secondColumnY.ToString(CultureInfo.InvariantCulture), cancellationToken);
            await SetMetaAsync(connection, transaction, "inbox_cursor_y", Math.Max(firstColumnY, secondColumnY).ToString(CultureInfo.InvariantCulture), cancellationToken);
            await SetMetaAsync(connection, transaction, "last_inserted_element", result.ElementId, cancellationToken);
            await SetMetaAsync(connection, transaction, "modified_utc", DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
            if (await IsCheckpointDueAsync(connection, transaction, cancellationToken))
            {
                await InsertCheckpointAsync(connection, transaction, revision, result.SceneJson, "capture", cancellationToken);
                await TrimCheckpointsAsync(connection, transaction, cancellationToken);
                await SetMetaAsync(connection, transaction, "last_checkpoint_utc", DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TextClipInsertResult> InsertCapturedTextAsync(
        string text,
        string source,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Copied text cannot be empty.", nameof(text));
        }

        await InitializeAsync(cancellationToken);
        EnsureWritable();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction();
            var x = await GetMetaDoubleAsync(connection, transaction, "text_inbox_x", TextInboxX, cancellationToken);
            var y = await GetMetaDoubleAsync(connection, transaction, "text_inbox_cursor_y", TextInboxY, cancellationToken);
            var scene = await GetSceneAsync(connection, transaction, cancellationToken);
            var result = SceneBuilder.AppendTextClip(scene, text, x, y, source);
            var revision = await GetRevisionAsync(connection, transaction, cancellationToken) + 1;

            await UpdateSceneAsync(connection, transaction, result.SceneJson, revision, cancellationToken);
            await ReindexTextIncrementallyAsync(connection, transaction, result.SceneJson, cancellationToken);
            await SetMetaAsync(connection, transaction, "text_inbox_cursor_y", (y + result.Height + TextInboxGap).ToString(CultureInfo.InvariantCulture), cancellationToken);
            await SetMetaAsync(connection, transaction, "last_inserted_element", result.ElementId, cancellationToken);
            await SetMetaAsync(connection, transaction, "modified_utc", DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
            if (await IsCheckpointDueAsync(connection, transaction, cancellationToken))
            {
                await InsertCheckpointAsync(connection, transaction, revision, result.SceneJson, "text-capture", cancellationToken);
                await TrimCheckpointsAsync(connection, transaction, cancellationToken);
                await SetMetaAsync(connection, transaction, "last_checkpoint_utc", DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveOcrAsync(
        string assetHash,
        string language,
        string text,
        string status,
        string? error,
        double x,
        double y,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        EnsureWritable();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO ocr(asset_hash, language, text, status, error, updated_utc)
                VALUES($hash, $language, $text, $status, $error, $now)
                ON CONFLICT(asset_hash, language) DO UPDATE SET
                    text=excluded.text,
                    status=excluded.status,
                    error=excluded.error,
                    updated_utc=excluded.updated_utc;
                """;
            command.Parameters.AddWithValue("$hash", assetHash);
            command.Parameters.AddWithValue("$language", language);
            command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);

            var refId = $"{assetHash}:{language}";
            if (string.IsNullOrWhiteSpace(text))
            {
                await DeleteSearchAsync(connection, transaction, "ocr", refId, cancellationToken);
            }
            else
            {
                await UpsertSearchAsync(connection, transaction, "ocr", refId, text, x, y, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return [];
        }

        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            if (query.EnumerateRunes().Count() <= 2)
            {
                command.CommandText = """
                    SELECT kind, ref_id, text, x, y
                    FROM search_content
                    WHERE text LIKE $query ESCAPE '\'
                    ORDER BY id DESC
                    LIMIT 100;
                    """;
                var escaped = query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
                command.Parameters.AddWithValue("$query", $"%{escaped}%");
            }
            else
            {
                command.CommandText = """
                    SELECT kind, ref_id, text, CAST(x AS REAL), CAST(y AS REAL)
                    FROM search_fts
                    WHERE search_fts MATCH $query
                    ORDER BY bm25(search_fts), rowid DESC
                    LIMIT 100;
                    """;
                command.Parameters.AddWithValue("$query", $"\"{query.Replace("\"", "\"\"")}\"");
            }

            var results = new List<SearchHit>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(new SearchHit(
                    FilePath,
                    Title,
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetDouble(3),
                    reader.GetDouble(4)));
            }
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveCopyAsync(string destination, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var source = await OpenConnectionAsync(cancellationToken);
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = destination,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            };
            await using var target = new SqliteConnection(builder.ToString());
            await target.OpenAsync(cancellationToken);
            source.BackupDatabase(target);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RenameDisplayTitleAsync(string displayTitle, CancellationToken cancellationToken = default)
    {
        displayTitle = displayTitle.Trim();
        if (string.IsNullOrWhiteSpace(displayTitle))
        {
            throw new InvalidDataException(LocalizationService.T("RenameBoardEmpty"));
        }
        if (displayTitle.Length > 120)
        {
            throw new InvalidDataException(LocalizationService.T("RenameBoardTooLong"));
        }

        await InitializeAsync(cancellationToken);
        EnsureWritable();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction();
            await SetMetaAsync(connection, transaction, "title", displayTitle, cancellationToken);
            await SetMetaAsync(connection, transaction, "modified_utc", DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _displayTitle = displayTitle;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetInboxAsync(double x = DefaultInboxX, double y = DefaultInboxY, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        EnsureWritable();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction();
            await SetMetaAsync(connection, transaction, "inbox_x", x.ToString(CultureInfo.InvariantCulture), cancellationToken);
            await SetMetaAsync(connection, transaction, "inbox_cursor_y", y.ToString(CultureInfo.InvariantCulture), cancellationToken);
            await SetMetaAsync(connection, transaction, "inbox_column_0_y", y.ToString(CultureInfo.InvariantCulture), cancellationToken);
            await SetMetaAsync(connection, transaction, "inbox_column_1_y", y.ToString(CultureInfo.InvariantCulture), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<long> SaveSceneCoreAsync(
        string sceneJson,
        IReadOnlyList<(string FileId, AssetRecord Asset)> newAssets,
        long? expectedRevision,
        bool checkpoint,
        string checkpointReason,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        EnsureWritable();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction();
            var currentRevision = await GetRevisionAsync(connection, transaction, cancellationToken);
            if (expectedRevision.HasValue && currentRevision != expectedRevision.Value)
            {
                throw new RevisionConflictException(expectedRevision.Value, currentRevision);
            }

            foreach (var item in newAssets)
            {
                await UpsertAssetAsync(connection, transaction, item.Asset, cancellationToken);
                await UpsertFileMapAsync(connection, transaction, item.FileId, item.Asset.Hash, cancellationToken);
            }

            var revision = currentRevision + 1;
            await UpdateSceneAsync(connection, transaction, sceneJson, revision, cancellationToken);
            await ReindexTextIncrementallyAsync(connection, transaction, sceneJson, cancellationToken);
            await SetMetaAsync(connection, transaction, "modified_utc", DateTimeOffset.UtcNow.ToString("O"), cancellationToken);

            if (checkpoint || await IsCheckpointDueAsync(connection, transaction, cancellationToken))
            {
                await InsertCheckpointAsync(connection, transaction, revision, sceneJson, checkpointReason, cancellationToken);
                await TrimCheckpointsAsync(connection, transaction, cancellationToken);
                await SetMetaAsync(connection, transaction, "last_checkpoint_utc", DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return revision;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = IsReadOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task ConfigureDatabaseAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task CreateVersionTwoDatabaseAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await EnsureVersionTwoSchemaAsync(connection, rebuildSearchIndex: false, cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await SetMetaAsync(connection, transaction, "schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await SetMetaAsync(connection, transaction, "scene_compression", BrotliEncoding, cancellationToken);
        await using var userVersion = connection.CreateCommand();
        userVersion.Transaction = transaction;
        userVersion.CommandText = $"PRAGMA user_version={SchemaVersion};";
        await userVersion.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task EnsureVersionTwoSchemaAsync(
        SqliteConnection connection,
        bool rebuildSearchIndex,
        CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS meta (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS scene_current (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    scene_data BLOB NOT NULL,
                    scene_encoding TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS scene_revisions (
                    revision INTEGER PRIMARY KEY,
                    scene_data BLOB NOT NULL,
                    scene_encoding TEXT NOT NULL,
                    saved_utc TEXT NOT NULL,
                    reason TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS assets (
                    hash TEXT PRIMARY KEY,
                    mime_type TEXT NOT NULL,
                    width INTEGER NOT NULL,
                    height INTEGER NOT NULL,
                    bytes BLOB NOT NULL,
                    created_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS file_map (
                    file_id TEXT PRIMARY KEY,
                    asset_hash TEXT NOT NULL REFERENCES assets(hash),
                    created_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS ocr (
                    asset_hash TEXT NOT NULL REFERENCES assets(hash),
                    language TEXT NOT NULL,
                    text TEXT NOT NULL,
                    status TEXT NOT NULL,
                    error TEXT,
                    updated_utc TEXT NOT NULL,
                    PRIMARY KEY(asset_hash, language)
                );
                CREATE TABLE IF NOT EXISTS search_content (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    kind TEXT NOT NULL,
                    ref_id TEXT NOT NULL,
                    text TEXT NOT NULL,
                    x REAL NOT NULL,
                    y REAL NOT NULL
                );
                CREATE VIRTUAL TABLE IF NOT EXISTS search_fts USING fts5(
                    kind,
                    ref_id UNINDEXED,
                    text,
                    x UNINDEXED,
                    y UNINDEXED,
                    tokenize='trigram'
                );
                DELETE FROM search_content
                WHERE id NOT IN (SELECT MAX(id) FROM search_content GROUP BY kind, ref_id);
                CREATE UNIQUE INDEX IF NOT EXISTS ix_search_content_kind_ref ON search_content(kind, ref_id);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (rebuildSearchIndex)
        {
            await using var rebuild = connection.CreateCommand();
            rebuild.Transaction = transaction;
            rebuild.CommandText = """
                DELETE FROM search_fts;
                INSERT INTO search_fts(kind, ref_id, text, x, y)
                SELECT kind, ref_id, text, x, y FROM search_content;
                """;
            await rebuild.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MigrateVersionOneToTwoAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await VerifySceneSchemaAsync(connection, 1, cancellationToken);
        string currentScene;
        long currentRevision;
        string currentUpdated;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT scene_json, revision, updated_utc FROM scene_current WHERE id=1";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException("The version 1 Pinboard file has no current scene.");
            }
            currentScene = reader.GetString(0);
            currentRevision = reader.GetInt64(1);
            currentUpdated = reader.GetString(2);
        }

        var revisions = new List<(long Revision, string Scene, string SavedUtc, string Reason)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT revision, scene_json, saved_utc, reason FROM scene_revisions ORDER BY revision";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                revisions.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        await using var transaction = connection.BeginTransaction();
        await using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = """
                CREATE TABLE scene_current_v2 (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    scene_data BLOB NOT NULL,
                    scene_encoding TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE scene_revisions_v2 (
                    revision INTEGER PRIMARY KEY,
                    scene_data BLOB NOT NULL,
                    scene_encoding TEXT NOT NULL,
                    saved_utc TEXT NOT NULL,
                    reason TEXT NOT NULL
                );
                """;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var insertCurrent = connection.CreateCommand())
        {
            insertCurrent.Transaction = transaction;
            insertCurrent.CommandText = "INSERT INTO scene_current_v2 VALUES(1, $data, $encoding, $revision, $updated)";
            insertCurrent.Parameters.AddWithValue("$data", EncodeScene(currentScene));
            insertCurrent.Parameters.AddWithValue("$encoding", BrotliEncoding);
            insertCurrent.Parameters.AddWithValue("$revision", currentRevision);
            insertCurrent.Parameters.AddWithValue("$updated", currentUpdated);
            await insertCurrent.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var revision in revisions)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO scene_revisions_v2 VALUES($revision, $data, $encoding, $saved, $reason)";
            insert.Parameters.AddWithValue("$revision", revision.Revision);
            insert.Parameters.AddWithValue("$data", EncodeScene(revision.Scene));
            insert.Parameters.AddWithValue("$encoding", BrotliEncoding);
            insert.Parameters.AddWithValue("$saved", revision.SavedUtc);
            insert.Parameters.AddWithValue("$reason", revision.Reason);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var swap = connection.CreateCommand())
        {
            swap.Transaction = transaction;
            swap.CommandText = """
                DROP TABLE scene_current;
                DROP TABLE scene_revisions;
                ALTER TABLE scene_current_v2 RENAME TO scene_current;
                ALTER TABLE scene_revisions_v2 RENAME TO scene_revisions;
                """;
            await swap.ExecuteNonQueryAsync(cancellationToken);
        }
        await SetMetaAsync(connection, transaction, "schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await SetMetaAsync(connection, transaction, "scene_compression", BrotliEncoding, cancellationToken);
        await using (var userVersion = connection.CreateCommand())
        {
            userVersion.Transaction = transaction;
            userVersion.CommandText = $"PRAGMA user_version={SchemaVersion};";
            await userVersion.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task EnsureDocumentDefaultsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await SetMetaDefaultAsync(connection, "schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture), cancellationToken);
        var storedVersion = await ReadAndValidateSchemaVersionAsync(connection, cancellationToken);
        if (storedVersion != SchemaVersion)
        {
            throw new InvalidDataException($"Pinboard schema_version is {storedVersion}, expected {SchemaVersion}.");
        }
        await SetMetaDefaultAsync(connection, "scene_compression", BrotliEncoding, cancellationToken);
        await SetMetaDefaultAsync(connection, "title", Title, cancellationToken);
        await SetMetaDefaultAsync(connection, "created_utc", DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
        await SetMetaDefaultAsync(connection, "inbox_x", DefaultInboxX.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await SetMetaDefaultAsync(connection, "inbox_cursor_y", DefaultInboxY.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await SetMetaDefaultAsync(connection, "text_inbox_x", TextInboxX.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await SetMetaDefaultAsync(connection, "text_inbox_cursor_y", TextInboxY.ToString(CultureInfo.InvariantCulture), cancellationToken);

        await using var sceneCommand = connection.CreateCommand();
        sceneCommand.CommandText = """
            INSERT OR IGNORE INTO scene_current(id, scene_data, scene_encoding, revision, updated_utc)
            VALUES(1, $scene, $encoding, 1, $now);
            """;
        var scene = IsTextClipsDocumentPath(FilePath)
            ? SceneBuilder.CreateTextClipsScene()
            : SceneBuilder.CreateDefaultScene();
        sceneCommand.Parameters.AddWithValue("$scene", EncodeScene(scene));
        sceneCommand.Parameters.AddWithValue("$encoding", BrotliEncoding);
        sceneCommand.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await sceneCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool IsTextClipsDocumentPath(string path)
    {
        if (Path.GetFileName(path).Equals(AppSettings.TextClipsBoardFileName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var parent = Path.GetDirectoryName(path);
        return parent is not null
            && Path.GetFileName(parent).Equals(AppSettings.TextClipsDirectoryName, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<int> ReadAndValidateSchemaVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "meta", cancellationToken))
        {
            throw new InvalidDataException("This file is not a valid Pinboard database: meta table is missing.");
        }
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key='schema_version'";
        var raw = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1)
        {
            throw new InvalidDataException("This Pinboard file has a missing or invalid schema_version.");
        }
        if (version > SchemaVersion)
        {
            throw new InvalidDataException($"This Pinboard file uses schema version {version}, but this application supports up to version {SchemaVersion}.");
        }
        return version;
    }

    private static async Task VerifySceneSchemaAsync(SqliteConnection connection, int version, CancellationToken cancellationToken)
    {
        var currentColumn = version == 1 ? "scene_json" : "scene_data";
        var encodingRequired = version >= 2;
        if (!await ColumnExistsAsync(connection, "scene_current", currentColumn, cancellationToken) ||
            !await ColumnExistsAsync(connection, "scene_revisions", currentColumn, cancellationToken) ||
            encodingRequired && (!await ColumnExistsAsync(connection, "scene_current", "scene_encoding", cancellationToken) ||
                                 !await ColumnExistsAsync(connection, "scene_revisions", "scene_encoding", cancellationToken)))
        {
            throw new InvalidDataException($"Pinboard schema version {version} does not match its scene table layout.");
        }
    }

    private static async Task ValidateSceneExistsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var validation = connection.CreateCommand();
        validation.CommandText = "SELECT COUNT(*) FROM scene_current WHERE id=1";
        if (Convert.ToInt64(await validation.ExecuteScalarAsync(cancellationToken) ?? 0, CultureInfo.InvariantCulture) != 1)
        {
            throw new InvalidDataException(LocalizationService.T("PinboardSceneMissing"));
        }
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name)";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0, CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string table, string column, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\")";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private void EnsureWritable()
    {
        if (IsReadOnly)
        {
            throw new UnauthorizedAccessException(LocalizationService.T("PinboardReadOnlyFormat", FilePath));
        }
    }

    private static async Task SetMetaDefaultAsync(SqliteConnection connection, string key, string value, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO meta(key, value) VALUES($key, $value)";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SetMetaAsync(SqliteConnection connection, SqliteTransaction transaction, string key, string value, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO meta(key, value) VALUES($key, $value)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<double> GetMetaDoubleAsync(SqliteConnection connection, SqliteTransaction transaction, string key, double fallback, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM meta WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : fallback;
    }

    private static async Task<string> GetMetaStringAsync(SqliteConnection connection, string key, string fallback, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string text && !string.IsNullOrWhiteSpace(text) ? text : fallback;
    }

    private static async Task<(string Scene, long Revision)> ReadCurrentSceneAsync(
        SqliteConnection connection,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = version == 1
            ? "SELECT scene_json, revision FROM scene_current WHERE id=1"
            : "SELECT scene_data, scene_encoding, revision FROM scene_current WHERE id=1";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(LocalizationService.T("PinboardSceneMissing"));
        }
        return version == 1
            ? (reader.GetString(0), reader.GetInt64(1))
            : (DecodeScene((byte[])reader[0], reader.GetString(1)), reader.GetInt64(2));
    }

    private static async Task<string> GetSceneAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT scene_data, scene_encoding FROM scene_current WHERE id=1";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? DecodeScene((byte[])reader[0], reader.GetString(1))
            : SceneBuilder.CreateDefaultScene();
    }

    private static async Task<string?> ReadCheckpointSceneAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        int version,
        long revision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = version == 1
            ? "SELECT scene_json FROM scene_revisions WHERE revision=$revision"
            : "SELECT scene_data, scene_encoding FROM scene_revisions WHERE revision=$revision";
        command.Parameters.AddWithValue("$revision", revision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return version == 1 ? reader.GetString(0) : DecodeScene((byte[])reader[0], reader.GetString(1));
    }

    private static async Task<long> GetRevisionAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision FROM scene_current WHERE id=1";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0, CultureInfo.InvariantCulture);
    }

    private static async Task UpdateSceneAsync(SqliteConnection connection, SqliteTransaction transaction, string sceneJson, long revision, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE scene_current
            SET scene_data=$scene, scene_encoding=$encoding, revision=$revision, updated_utc=$now
            WHERE id=1;
            """;
        command.Parameters.AddWithValue("$scene", EncodeScene(sceneJson));
        command.Parameters.AddWithValue("$encoding", BrotliEncoding);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static byte[] EncodeScene(string sceneJson)
    {
        var utf8 = Encoding.UTF8.GetBytes(sceneJson);
        using var output = new MemoryStream();
        using (var compressor = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            compressor.Write(utf8, 0, utf8.Length);
        }
        return output.ToArray();
    }

    private static string DecodeScene(byte[] data, string encoding)
    {
        try
        {
            if (string.Equals(encoding, BrotliEncoding, StringComparison.Ordinal))
            {
                using var input = new MemoryStream(data, writable: false);
                using var decompressor = new BrotliStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                decompressor.CopyTo(output);
                return Encoding.UTF8.GetString(output.ToArray());
            }
            if (string.Equals(encoding, "utf8", StringComparison.Ordinal))
            {
                return Encoding.UTF8.GetString(data);
            }
            throw new InvalidDataException($"Unsupported scene encoding '{encoding}'.");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidDataException("The compressed Pinboard scene is damaged.", exception);
        }
    }

    private static async Task UpsertAssetAsync(SqliteConnection connection, SqliteTransaction transaction, AssetRecord asset, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO assets(hash, mime_type, width, height, bytes, created_utc)
            VALUES($hash, $mime, $width, $height, $bytes, $created);
            """;
        command.Parameters.AddWithValue("$hash", asset.Hash);
        command.Parameters.AddWithValue("$mime", asset.MimeType);
        command.Parameters.AddWithValue("$width", asset.Width);
        command.Parameters.AddWithValue("$height", asset.Height);
        command.Parameters.AddWithValue("$bytes", asset.Bytes);
        command.Parameters.AddWithValue("$created", asset.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertFileMapAsync(SqliteConnection connection, SqliteTransaction transaction, string fileId, string assetHash, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO file_map(file_id, asset_hash, created_utc)
            VALUES($fileId, $hash, $created)
            ON CONFLICT(file_id) DO UPDATE SET asset_hash=excluded.asset_hash;
            """;
        command.Parameters.AddWithValue("$fileId", fileId);
        command.Parameters.AddWithValue("$hash", assetHash);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReindexTextIncrementallyAsync(SqliteConnection connection, SqliteTransaction transaction, string sceneJson, CancellationToken cancellationToken)
    {
        var desired = SceneBuilder.ExtractTextElements(sceneJson)
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var existing = new Dictionary<string, SearchEntry>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT ref_id, text, x, y FROM search_content WHERE kind='text'";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existing[reader.GetString(0)] = new SearchEntry(reader.GetString(1), reader.GetDouble(2), reader.GetDouble(3));
            }
        }

        foreach (var staleId in existing.Keys.Except(desired.Keys, StringComparer.Ordinal))
        {
            await DeleteSearchAsync(connection, transaction, "text", staleId, cancellationToken);
        }
        foreach (var (id, item) in desired)
        {
            if (!existing.TryGetValue(id, out var old) || old.Text != item.Text || old.X != item.X || old.Y != item.Y)
            {
                await UpsertSearchAsync(connection, transaction, "text", id, item.Text, item.X, item.Y, cancellationToken);
            }
        }
    }

    private static async Task DeleteSearchAsync(SqliteConnection connection, SqliteTransaction transaction, string kind, string refId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM search_content WHERE kind=$kind AND ref_id=$ref;
            DELETE FROM search_fts WHERE kind=$kind AND ref_id=$ref;
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$ref", refId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertSearchAsync(SqliteConnection connection, SqliteTransaction transaction, string kind, string refId, string text, double x, double y, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO search_content(kind, ref_id, text, x, y) VALUES($kind, $ref, $text, $x, $y)
            ON CONFLICT(kind, ref_id) DO UPDATE SET text=excluded.text, x=excluded.x, y=excluded.y;
            DELETE FROM search_fts WHERE kind=$kind AND ref_id=$ref;
            INSERT INTO search_fts(kind, ref_id, text, x, y) VALUES($kind, $ref, $text, $x, $y);
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$ref", refId);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$x", x);
        command.Parameters.AddWithValue("$y", y);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertCheckpointAsync(SqliteConnection connection, SqliteTransaction transaction, long revision, string sceneJson, string reason, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO scene_revisions(revision, scene_data, scene_encoding, saved_utc, reason)
            VALUES($revision, $scene, $encoding, $now, $reason);
            """;
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$scene", EncodeScene(sceneJson));
        command.Parameters.AddWithValue("$encoding", BrotliEncoding);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$reason", reason);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task TrimCheckpointsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            DELETE FROM scene_revisions
            WHERE revision NOT IN (
                SELECT revision FROM scene_revisions ORDER BY revision DESC LIMIT {CheckpointLimit}
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> IsCheckpointDueAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM meta WHERE key='last_checkpoint_utc'";
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        return !DateTimeOffset.TryParse(value, out var last) || DateTimeOffset.UtcNow - last >= TimeSpan.FromMinutes(10);
    }

    private static void CollectReferencedFileIds(string sceneJson, ISet<string> destination)
    {
        using var document = JsonDocument.Parse(sceneJson);
        Visit(document.RootElement);
        return;

        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("fileId") && property.Value.ValueKind == JsonValueKind.String)
                    {
                        var fileId = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(fileId))
                        {
                            destination.Add(fileId);
                        }
                    }
                    else if (property.NameEquals("files") && property.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var file in property.Value.EnumerateObject())
                        {
                            destination.Add(file.Name);
                        }
                    }
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    Visit(item);
                }
            }
        }
    }

    private sealed record SearchEntry(string Text, double X, double Y);
}
