using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Pinboard.App.Services.AiSessions;

public static class SessionSnapshot
{
    public static string NormalizeManagedPath(string path)
    {
        // Codex records may use Windows extended-length paths. Normalize only filesystem forms,
        // never device namespaces, before applying the same narrow archive boundary checks.
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length > 6
            && char.IsAsciiLetter(path[4]) && path[5] == ':' && path[6] == '\\') path = path[4..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new InvalidDataException("Device paths are not eligible conversation sources.");
        return System.IO.Path.GetFullPath(path);
    }
    public static void RequireManagedPath(string path, params string[] roots)
    {
        var full = NormalizeManagedPath(path);
        foreach (var candidate in roots)
        {
            var root = NormalizeManagedPath(candidate).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            for (var check = full; check is not null && check.Length >= root.TrimEnd(System.IO.Path.DirectorySeparatorChar).Length; check = System.IO.Path.GetDirectoryName(check))
                if ((File.GetAttributes(check) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked files outside the managed conversation archive are not eligible.");
            return;
        }
        throw new InvalidDataException("Source is outside its managed conversation directory. Import an official export instead.");
    }
    public static SqliteConnection OpenRead(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 3, Pooling = false }.ToString());
        db.Open(); return db;
    }
    public static long Size(string? path) => path is not null && File.Exists(path) ? new FileInfo(path).Length : 0;
    public static DateTimeOffset Modified(string path) => File.GetLastWriteTimeUtc(path);
    public static async Task<SnapshotFile> CopyAsync(string source, string staging, string name, CancellationToken ct)
    {
        var target = System.IO.Path.Combine(staging, name); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        var startedModified = File.GetLastWriteTimeUtc(source);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 128 * 1024, true);
        var length = input.Length;
        if (length > 0 && source.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            // A captured prefix must end at a complete JSONL record, including when the writer is active.
            var tailSize = (int)Math.Min(length, 4 * 1024 * 1024);
            var tail = new byte[tailSize]; input.Position = length - tailSize;
            await input.ReadExactlyAsync(tail, ct);
            var end = Array.LastIndexOf(tail, (byte)'\n');
            if (end < 0) throw new IOException("No complete JSONL boundary found; waiting for the source writer.");
            var previous = Array.LastIndexOf(tail, (byte)'\n', Math.Max(0, end - 1));
            var start = previous >= 0 ? previous + 1 : 0;
            if (start == 0 && length > tailSize) throw new IOException("The last JSONL record exceeds the validation limit; source format needs review.");
            if (end > start)
            {
                using var json = JsonDocument.Parse(tail.AsMemory(start, end - start));
                if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Unexpected JSONL record format.");
            }
            length = length - tailSize + end + 1;
        }
        input.Position = 0;
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
        var buffer = new byte[128 * 1024]; long remaining = length;
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (read == 0) throw new IOException("The source changed while creating its snapshot.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct); remaining -= read;
        }
        if (!source.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            && (input.Length != length || File.GetLastWriteTimeUtc(source) != startedModified))
            throw new IOException("The source is still changing; waiting for a stable snapshot.");
        await output.FlushAsync(ct); output.Flush(true); return new(name.Replace('\\', '/'), target);
    }
    public static SnapshotFile CopyDatabase(string source, string staging, string name)
    {
        var target = System.IO.Path.Combine(staging, name);
        using var input = OpenRead(source); using var output = new SqliteConnection($"Data Source={target};Pooling=False");
        output.Open(); input.BackupDatabase(output);
        using (var journal = output.CreateCommand()) { journal.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL;"; journal.ExecuteNonQuery(); }
        VerifyDatabase(output); return new(name, target);
    }
    public static SnapshotFile ExportRows(string source, string staging, string name, string idColumn, string id, params string[] tables)
    {
        var target = System.IO.Path.Combine(staging, name);
        using var input = OpenRead(source); using var readTx = input.BeginTransaction(deferred: true);
        // This is deliberately a selected-thread export, not a copy of unselected project tables.
        // Keep original column values/schema, but do not enforce references to excluded tables.
        using var output = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = target, Pooling = false, ForeignKeys = false }.ToString()); output.Open();
        using var writeTx = output.BeginTransaction();
        foreach (var table in tables)
        {
            // Table/column names only come from adapter constants, never imported JSON.
            using var schema = input.CreateCommand(); schema.Transaction = readTx;
            schema.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$table";
            schema.Parameters.AddWithValue("$table", table);
            if (schema.ExecuteScalar() is not string sql) continue;
            using var create = output.CreateCommand(); create.Transaction = writeTx; create.CommandText = sql; create.ExecuteNonQuery();
            using var select = input.CreateCommand(); select.Transaction = readTx;
            select.CommandText = $"SELECT * FROM \"{table}\" WHERE \"{idColumn}\"=$id"; select.Parameters.AddWithValue("$id", id);
            using var reader = select.ExecuteReader();
            using var insert = output.CreateCommand(); insert.Transaction = writeTx;
            insert.CommandText = $"INSERT INTO \"{table}\" VALUES({string.Join(',', Enumerable.Range(0, reader.FieldCount).Select(i => "$v" + i))})";
            for (var i = 0; i < reader.FieldCount; i++) insert.Parameters.Add(new SqliteParameter("$v" + i, DBNull.Value));
            while (reader.Read())
            {
                for (var i = 0; i < reader.FieldCount; i++) insert.Parameters[i].Value = reader.GetValue(i);
                insert.ExecuteNonQuery();
            }
        }
        writeTx.Commit(); readTx.Commit(); VerifyDatabase(output); return new(name, target);
    }
    private static void VerifyDatabase(SqliteConnection db)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA quick_check";
        if (!string.Equals(cmd.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Snapshot database integrity check failed.");
    }
    public static async Task AddReferencedImagesAsync(IEnumerable<string> records, string allowedRoot, string staging,
        List<SnapshotFile> files, CancellationToken ct)
    {
        IEnumerable<string> Values()
        {
            foreach (var record in records)
            {
                using var reader = new StringReader(record);
                foreach (var value in JsonStringValues(reader, ct)) yield return value;
            }
        }
        await AddReferencedPathsAsync(Values(), allowedRoot, staging, files, ct);
    }
    public static async Task AddReferencedImagesFromFileAsync(string path, string allowedRoot, string staging, List<SnapshotFile> files, CancellationToken ct)
    {
        using var reader = new StreamReader(path);
        await AddReferencedPathsAsync(JsonStringValues(reader, ct), allowedRoot, staging, files, ct);
    }
    private static async Task AddReferencedPathsAsync(IEnumerable<string> values, string allowedRoot, string staging, List<SnapshotFile> files, CancellationToken ct)
    {
        var root = NormalizeManagedPath(allowedRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var value in values)
        {
            ct.ThrowIfCancellationRequested();
            if (!System.IO.Path.IsPathFullyQualified(value)) continue;
            try
            {
                var path = NormalizeManagedPath(value);
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    || !new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" }.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant())) continue;
                var linked = false;
                for (var check = path; check is not null && check.Length >= root.TrimEnd(System.IO.Path.DirectorySeparatorChar).Length; check = System.IO.Path.GetDirectoryName(check))
                    if ((File.GetAttributes(check) & FileAttributes.ReparsePoint) != 0) { linked = true; break; }
                if (!linked) found.Add(path);
            }
            catch (ArgumentException) { }
            catch (FileNotFoundException) { missing.Add(value); }
            catch (DirectoryNotFoundException) { missing.Add(value); }
        }
        foreach (var path in found)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("A referenced conversation image is missing.", path);
            files.Add(await CopyAsync(path, staging, "attachments/" + System.IO.Path.GetRelativePath(root, path), ct));
        }
        await WriteMissingReportAsync(missing, staging, files, ct);
    }
    private static async Task WriteMissingReportAsync(IReadOnlyList<string> missing, string staging, List<SnapshotFile> files, CancellationToken ct)
    {
        if (missing.Count == 0) return;
        const string name = "pinboard-attachment-report.json"; var path = System.IO.Path.Combine(staging, name);
        var previous = File.Exists(path) ? JsonSerializer.Deserialize<List<string>>(await File.ReadAllTextAsync(path, ct)) ?? [] : [];
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(previous.Concat(missing).Distinct(), SessionJson.Options), ct);
        if (!files.Any(x => x.RelativePath == name)) files.Add(new(name, path, true));
    }
    public static async Task AddCodexClipboardImagesAsync(IEnumerable<string> values, string staging, List<SnapshotFile> files, CancellationToken ct)
    {
        var missing = new List<string>(); var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            ct.ThrowIfCancellationRequested();
            if (!System.IO.Path.IsPathFullyQualified(value)) continue;
            var name = System.IO.Path.GetFileName(value);
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^codex-clipboard-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.png$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
            if (!File.Exists(value)) { missing.Add(value); continue; }
            if (!found.Add(value) || files.Any(x => x.RelativePath == "attachments/clipboard/" + name)) continue;
            RequireManagedPath(value, System.IO.Path.GetDirectoryName(value)!);
            await using var input = new FileStream(value, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var signature = new byte[8]; var read = await input.ReadAsync(signature, ct);
            if (read != 8 || !signature.AsSpan().SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new InvalidDataException("A referenced clipboard image has an unexpected format.");
            files.Add(await CopyAsync(value, staging, "attachments/clipboard/" + name, ct));
        }
        await WriteMissingReportAsync(missing, staging, files, ct);
    }
    public static IEnumerable<string> JsonStringValuesFromFile(string path, CancellationToken ct)
    {
        using var input = new StreamReader(path);
        foreach (var value in JsonStringValues(input, ct)) yield return value;
    }
    private static IEnumerable<string> JsonStringValues(TextReader input, CancellationToken ct)
    {
        // Large inline images/tool output are scanned without retaining their strings.
        var buffer = new char[32 * 1024]; var token = new StringBuilder();
        var inString = false; var escaped = false; var oversized = false; int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (var i = 0; i < read; i++)
            {
                var ch = buffer[i];
                if (!inString) { if (ch == '"') { inString = true; escaped = false; oversized = false; token.Clear(); } continue; }
                if (ch == '"' && !escaped)
                {
                    inString = false;
                    if (!oversized)
                    {
                        string? value = null;
                        try { value = JsonSerializer.Deserialize<string>("\"" + token + "\""); } catch (JsonException) { }
                        if (value is not null) yield return value;
                    }
                    continue;
                }
                if (!oversized) { token.Append(ch); if (token.Length > 32 * 1024) { token.Clear(); oversized = true; } }
                if (ch == '\\' && !escaped) escaped = true; else escaped = false;
            }
        }
    }
}

public sealed class CodexSessionSource(string root) : ISessionSourceAdapter
{
    public string Name => "Codex";
    public Task<IReadOnlyList<SessionDescriptor>> DiscoverAsync(CancellationToken ct)
    {
        var sessions = new Dictionary<string, SessionDescriptor>(StringComparer.Ordinal);
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        var state = System.IO.Path.Combine(root, "state_5.sqlite");
        if (File.Exists(state))
        {
            using var db = SessionSnapshot.OpenRead(state); using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id,title,rollout_path,cwd,updated_at,archived,source FROM threads";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                ct.ThrowIfCancellationRequested(); var id = r.GetString(0); var path = r.GetString(2);
                if (!File.Exists(path) && File.Exists(path + ".zst")) path += ".zst";
                var parent = ParentFromSource(r.GetString(6));
                sessions[id] = new() { Key = "Codex:" + id, Source = Name, SourceId = id, Title = r.GetString(1),
                    Project = r.GetString(3), Path = path, MetadataPath = state, HistoryPath = System.IO.Path.Combine(root, "thread_history_1.sqlite"),
                    UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(4)), Archived = r.GetBoolean(5), Size = SessionSnapshot.Size(path),
                    ParentKey = parent is null ? null : "Codex:" + parent, Availability = File.Exists(path) ? "Available" : "Missing source" };
            }
        }
        var index = System.IO.Path.Combine(root, "session_index.jsonl");
        if (File.Exists(index)) foreach (var line in File.ReadLines(index))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var json = JsonDocument.Parse(line); var e = json.RootElement;
                var id = SessionJson.String(e, "id"); var title = SessionJson.String(e, "thread_name");
                if (id.Length > 0 && title.Length > 0) titles[id] = title;
                if (sessions.TryGetValue(id, out var old) && title.Length > 0) sessions[id] = old with { Title = title };
            }
            catch (JsonException) { }
        }
        // Older/archived files can outlive the metadata database. Preserve them in the catalogue.
        foreach (var directory in new[] { "sessions", "archived_sessions" })
        {
            var path = System.IO.Path.Combine(root, directory); if (!Directory.Exists(path)) continue;
            foreach (var file in Directory.EnumerateFiles(path, "rollout-*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested(); var fileName = System.IO.Path.GetFileName(file);
                if (!(fileName.EndsWith(".jsonl") || fileName.EndsWith(".zst"))) continue;
                var match = System.Text.RegularExpressions.Regex.Match(fileName, @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}");
                if (!match.Success) continue;
                if (sessions.TryGetValue(match.Value, out var known))
                {
                    // An archived rollout can move while the source index still points at its old location.
                    if (!File.Exists(known.Path)) sessions[match.Value] = known with
                    { Path = file, Size = SessionSnapshot.Size(file), Availability = "Available", Archived = directory == "archived_sessions" };
                    continue;
                }
                sessions[match.Value] = new() { Key = "Codex:" + match.Value, Source = Name, SourceId = match.Value,
                    Title = titles.GetValueOrDefault(match.Value, fileName), Path = file, UpdatedAt = SessionSnapshot.Modified(file), Size = SessionSnapshot.Size(file),
                    Archived = directory == "archived_sessions" };
            }
        }
        return Task.FromResult<IReadOnlyList<SessionDescriptor>>(sessions.Values.ToArray());
    }
    private static string? ParentFromSource(string value)
    {
        try
        {
            using var json = JsonDocument.Parse(value);
            return Find(json.RootElement);
        }
        catch (JsonException) { return null; }
        static string? Find(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            if (e.TryGetProperty("parent_thread_id", out var id) && id.ValueKind == JsonValueKind.String) return id.GetString();
            foreach (var p in e.EnumerateObject()) if (Find(p.Value) is { } parent) return parent; return null;
        }
    }
    public async Task<IReadOnlyList<SnapshotFile>> SnapshotAsync(SessionDescriptor session, string staging, CancellationToken ct)
    {
        var missingSource = !File.Exists(session.Path);
        var full = SessionSnapshot.NormalizeManagedPath(session.Path!);
        if (!new[] { "sessions", "archived_sessions" }.Any(folder => full.StartsWith(
            SessionSnapshot.NormalizeManagedPath(System.IO.Path.Combine(root, folder)).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Source is outside its managed conversation directory. Import an official export instead.");
        var files = new List<SnapshotFile>();
        if (!missingSource)
        {
            SessionSnapshot.RequireManagedPath(session.Path!, System.IO.Path.Combine(root, "sessions"), System.IO.Path.Combine(root, "archived_sessions"));
            files.Add(await SessionSnapshot.CopyAsync(session.Path!, staging, System.IO.Path.GetFileName(session.Path!), ct));
        }
        var historyRows = 0L;
        if (session.MetadataPath is { } metadata && File.Exists(metadata))
        {
            if (SessionSnapshot.NormalizeManagedPath(metadata) != SessionSnapshot.NormalizeManagedPath(System.IO.Path.Combine(root, "state_5.sqlite"))) throw new InvalidDataException("Source metadata does not match this provider.");
            SessionSnapshot.RequireManagedPath(metadata, root);
            files.Add(SessionSnapshot.ExportRows(metadata, staging, "thread-metadata.sqlite", "id", session.SourceId, "threads"));
        }
        if (session.HistoryPath is { } history && File.Exists(history))
        {
            if (SessionSnapshot.NormalizeManagedPath(history) != SessionSnapshot.NormalizeManagedPath(System.IO.Path.Combine(root, "thread_history_1.sqlite"))) throw new InvalidDataException("Source history does not match this provider.");
            SessionSnapshot.RequireManagedPath(history, root);
            files.Add(SessionSnapshot.ExportRows(history, staging, "thread-history.sqlite", "thread_id", session.SourceId,
                "thread_turns", "thread_items", "thread_history_projection_state", "thread_realtime_items"));
            using var db = SessionSnapshot.OpenRead(history); using var cmd = db.CreateCommand();
            foreach (var table in new[] { "thread_items", "thread_turns", "thread_realtime_items" })
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$table"; cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$table", table);
                if (Convert.ToInt64(cmd.ExecuteScalar()) == 0) continue;
                cmd.CommandText = $"SELECT COUNT(*) FROM \"{table}\" WHERE thread_id=$id"; cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$id", session.SourceId);
                historyRows += Convert.ToInt64(cmd.ExecuteScalar());
            }
            if (missingSource && historyRows == 0) throw new FileNotFoundException("Conversation source is missing and no selected history records are available. Existing backups are retained.");
            cmd.Parameters.Clear();
            cmd.CommandText = "SELECT item_json FROM thread_items WHERE thread_id=$id"; cmd.Parameters.AddWithValue("$id", session.SourceId);
            using var r = cmd.ExecuteReader();
            IEnumerable<string> Records() { while (r.Read()) { ct.ThrowIfCancellationRequested(); yield return r.GetString(0); } }
            await SessionSnapshot.AddReferencedImagesAsync(Records(), System.IO.Path.Combine(root, "codex-remote-attachments"), staging, files, ct);
            r.Close();
            using var clipboard = db.CreateCommand();
            clipboard.CommandText = "SELECT j.value FROM thread_items i,json_tree(i.item_json) j WHERE i.thread_id=$id AND j.type='text' AND j.value LIKE '%codex-clipboard-%'";
            clipboard.Parameters.AddWithValue("$id", session.SourceId);
            using var imageRows = clipboard.ExecuteReader();
            IEnumerable<string> ClipboardPaths() { while (imageRows.Read()) { ct.ThrowIfCancellationRequested(); yield return imageRows.GetString(0); } }
            await SessionSnapshot.AddCodexClipboardImagesAsync(ClipboardPaths(), staging, files, ct);
        }
        if (missingSource)
        {
            if (historyRows == 0) throw new FileNotFoundException("Conversation source is missing and no selected history records are available. Existing backups are retained.");
            const string reportName = "pinboard-source-report.json";
            var report = System.IO.Path.Combine(staging, reportName);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { Reason = "Original conversation file is missing", SelectedHistoryRows = historyRows,
                Scope = "Only selected thread history and metadata were recovered. This is NOT a complete conversation backup." }, SessionJson.Options), ct);
            files.Add(new(reportName, report, IncompleteSource: true));
        }
        else if (session.Path!.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            await SessionSnapshot.AddCodexClipboardImagesAsync(SessionSnapshot.JsonStringValuesFromFile(files[0].Path, ct), staging, files, ct);
        return files;
    }
}

public sealed class ClaudeSessionSource(string root) : ISessionSourceAdapter
{
    public string Name => "Claude Code";
    public async Task<IReadOnlyList<SessionDescriptor>> DiscoverAsync(CancellationToken ct)
    {
        var sessions = new List<SessionDescriptor>(); if (!Directory.Exists(root)) return sessions;
        foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested(); var id = System.IO.Path.GetFileNameWithoutExtension(file);
            string title = "", preview = "", project = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file)) ?? "", parent = "";
            var folder = System.IO.Path.GetDirectoryName(file)!;
            if (System.IO.Path.GetFileName(folder).Equals("subagents", StringComparison.OrdinalIgnoreCase))
                parent = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(folder)) ?? "";
            // Only bounded head/tail metadata is examined. Large tool outputs are not retained.
            foreach (var line in await MetadataLines(file, ct))
            {
                try
                {
                    using var json = JsonDocument.Parse(line); var e = json.RootElement; var type = SessionJson.String(e, "type");
                    if (type == "custom-title") title = SessionJson.String(e, "customTitle");
                    else if (type == "summary" && title.Length == 0) title = SessionJson.String(e, "summary");
                    if (SessionJson.String(e, "cwd") is { Length: > 0 } cwd) project = cwd;
                    if (SessionJson.String(e, "parentSessionId") is { Length: > 0 } pid) parent = pid;
                    if (type == "user" && preview.Length == 0 && e.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content))
                    {
                        preview = content.ValueKind == JsonValueKind.String ? content.GetString() ?? "" : content.ValueKind == JsonValueKind.Array
                            ? string.Join(" ", content.EnumerateArray().Where(x => SessionJson.String(x, "type") == "text").Select(x => SessionJson.String(x, "text"))) : "";
                        preview = SessionJson.ShortTitle(preview);
                    }
                }
                catch (JsonException) { }
            }
            var auto = title.Length == 0;
            sessions.Add(new() { Key = "Claude Code:" + id, Source = Name, SourceId = id, Title = auto ? (preview.Length > 0 ? preview : id) : title,
                PreviewTitle = auto, Path = file, Project = project, ParentKey = parent.Length == 0 ? null : "Claude Code:" + parent,
                UpdatedAt = SessionSnapshot.Modified(file), Size = SessionSnapshot.Size(file) });
        }
        return sessions;
    }
    private static async Task<IReadOnlyList<string>> MetadataLines(string file, CancellationToken ct)
    {
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var size = (int)Math.Min(input.Length, 1024 * 1024); var head = new byte[size]; await input.ReadExactlyAsync(head, ct);
        var lines = Encoding.UTF8.GetString(head).Split('\n').Where(x => x.Length < 256 * 1024).ToList();
        if (input.Length > size)
        {
            input.Position = input.Length - size; var tail = new byte[size]; await input.ReadExactlyAsync(tail, ct);
            lines.AddRange(Encoding.UTF8.GetString(tail).Split('\n').Skip(1).Where(x => x.Length < 256 * 1024));
        }
        return lines;
    }
    public async Task<IReadOnlyList<SnapshotFile>> SnapshotAsync(SessionDescriptor session, string staging, CancellationToken ct)
    {
        SessionSnapshot.RequireManagedPath(session.Path!, root);
        var files = new List<SnapshotFile> { await SessionSnapshot.CopyAsync(session.Path!, staging, System.IO.Path.GetFileName(session.Path!), ct) };
        // Claude images stored inline are already included. Only managed project attachments are eligible.
        await SessionSnapshot.AddReferencedImagesFromFileAsync(files[0].Path, System.IO.Path.GetDirectoryName(session.Path!)!, staging, files, ct);
        return files;
    }
}

public sealed class AntigravitySessionSource(string root) : ISessionSourceAdapter
{
    public string Name => "Antigravity";
    public Task<IReadOnlyList<SessionDescriptor>> DiscoverAsync(CancellationToken ct)
    {
        var result = new List<SessionDescriptor>(); var metadata = System.IO.Path.Combine(root, "conversation_summaries.db");
        if (!File.Exists(metadata)) return Task.FromResult<IReadOnlyList<SessionDescriptor>>(result);
        using var db = SessionSnapshot.OpenRead(metadata); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT conversation_id,title,last_modified_time,workspace_uris,parent_conversation_id FROM conversation_summaries";
        using var r = cmd.ExecuteReader(); while (r.Read())
        {
            ct.ThrowIfCancellationRequested(); var id = r.GetString(0); var path = System.IO.Path.Combine(root, "conversations", id + ".db");
            var parent = r.IsDBNull(4) ? "" : r.GetString(4);
            result.Add(new() { Key = Name + ":" + id, Source = Name, SourceId = id, Title = r.GetString(1), Project = r.GetString(3),
                Path = path, MetadataPath = metadata, ParentKey = parent.Length > 0 ? Name + ":" + parent : null,
                UpdatedAt = DateTimeOffset.TryParse(r.GetString(2), out var updated) ? updated : SessionSnapshot.Modified(path),
                Size = SessionSnapshot.Size(path) + SessionSnapshot.Size(path + "-wal"), Availability = File.Exists(path) ? "Available" : "Missing source" });
        }
        return Task.FromResult<IReadOnlyList<SessionDescriptor>>(result);
    }
    public Task<IReadOnlyList<SnapshotFile>> SnapshotAsync(SessionDescriptor session, string staging, CancellationToken ct)
    {
        SessionSnapshot.RequireManagedPath(session.Path!, System.IO.Path.Combine(root, "conversations"));
        ct.ThrowIfCancellationRequested(); var files = new List<SnapshotFile> { SessionSnapshot.CopyDatabase(session.Path!, staging, session.SourceId + ".db") };
        if (session.MetadataPath is { } metadata && File.Exists(metadata))
        {
            if (System.IO.Path.GetFullPath(metadata) != System.IO.Path.GetFullPath(System.IO.Path.Combine(root, "conversation_summaries.db"))) throw new InvalidDataException("Source metadata does not match this provider.");
            files.Add(SessionSnapshot.ExportRows(metadata, staging, "conversation-metadata.sqlite", "conversation_id", session.SourceId, "conversation_summaries"));
        }
        return Task.FromResult<IReadOnlyList<SnapshotFile>>(files);
    }
}

public sealed class ImportedSessionSource(string root) : ISessionSourceAdapter
{
    public string Name => "Imported";
    public Task<IReadOnlyList<SessionDescriptor>> DiscoverAsync(CancellationToken ct)
    {
        var result = new List<SessionDescriptor>(); if (Directory.Exists(root)) foreach (var metadata in Directory.EnumerateFiles(root, "session.json", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested(); var session = JsonSerializer.Deserialize<SessionDescriptor>(File.ReadAllText(metadata));
            if (session is not null) result.Add(session);
        }
        return Task.FromResult<IReadOnlyList<SessionDescriptor>>(result);
    }
    public async Task<SessionDescriptor> ImportAsync(string path, string title, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N"); var directory = System.IO.Path.Combine(root, id); Directory.CreateDirectory(directory);
        var file = await SessionSnapshot.CopyAsync(path, directory, System.IO.Path.GetFileName(path), ct);
        var session = new SessionDescriptor { Key = Name + ":" + id, Source = Name, SourceId = id, Title = title,
            Path = file.Path, UpdatedAt = DateTimeOffset.UtcNow, Size = SessionSnapshot.Size(file.Path), Project = "Manual export" };
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "session.json"), JsonSerializer.Serialize(session), ct); return session;
    }
    public async Task<IReadOnlyList<SnapshotFile>> SnapshotAsync(SessionDescriptor session, string staging, CancellationToken ct)
    {
        SessionSnapshot.RequireManagedPath(session.Path!, root);
        return [await SessionSnapshot.CopyAsync(session.Path!, staging, System.IO.Path.GetFileName(session.Path!), ct)];
    }
}
