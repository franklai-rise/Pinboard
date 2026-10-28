using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pinboard.App.Services.AiSessions;

namespace Pinboard.App.Tests;

[TestClass]
public sealed class AiSessionSourceTests
{
    private string _root = null!;
    [TestInitialize] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "PinboardAiSourceTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
    private string Write(string relative, string text)
    { var path = Path.Combine(_root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, new UTF8Encoding(false)); return path; }
    private static void Sql(string path, string sql)
    { using var db = new SqliteConnection($"Data Source={path};Pooling=False"); db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    [TestMethod]
    public async Task Codex_MetadataIncludesArchivedZstTitlesAndParentWithoutDecompression()
    {
        var id = Guid.NewGuid().ToString(); var parent = Guid.NewGuid().ToString();
        var raw = Write(Path.Combine("archived_sessions", "rollout-2026-01-01-" + id + ".jsonl.zst"), "compressed bytes are never parsed for titles");
        var state = Path.Combine(_root, "state_5.sqlite");
        Sql(state, "CREATE TABLE threads(id TEXT,title TEXT,rollout_path TEXT,cwd TEXT,updated_at INTEGER,archived INTEGER,source TEXT);");
        using (var db = new SqliteConnection($"Data Source={state};Pooling=False"))
        {
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO threads VALUES($id,'Original',$path,'Demo project',1234,1,$source)";
            cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$path", raw[..^4]);
            cmd.Parameters.AddWithValue("$source", JsonSerializer.Serialize(new { subagent = new { thread_spawn = new { parent_thread_id = parent } } })); cmd.ExecuteNonQuery();
        }
        Write("session_index.jsonl", JsonSerializer.Serialize(new { id, thread_name = "Renamed title" }) + "\n");
        var items = await new CodexSessionSource(_root).DiscoverAsync(default);
        Assert.AreEqual(1, items.Count); Assert.AreEqual("Renamed title", items[0].Title); Assert.AreEqual(raw, items[0].Path);
        Assert.IsTrue(items[0].Archived); Assert.AreEqual("Codex:" + parent, items[0].ParentKey);
    }
    [TestMethod]
    public async Task Codex_OrphanArchiveUsesTitleIndexAndMissingSourcesRemainVisible()
    {
        var id = Guid.NewGuid().ToString(); Write(Path.Combine("sessions", "rollout-2026-01-01-" + id + ".jsonl"), "{}\n");
        Write("session_index.jsonl", JsonSerializer.Serialize(new { id, thread_name = "Known title" }) + "\n");
        var items = await new CodexSessionSource(_root).DiscoverAsync(default); Assert.AreEqual("Known title", items.Single().Title);
        var state = Path.Combine(_root, "state_5.sqlite"); Sql(state, "CREATE TABLE threads(id TEXT,title TEXT,rollout_path TEXT,cwd TEXT,updated_at INTEGER,archived INTEGER,source TEXT); INSERT INTO threads VALUES('missing','Missing','absent','demo',0,0,'cli');");
        items = await new CodexSessionSource(_root).DiscoverAsync(default); Assert.AreEqual("Missing source", items.Single(x => x.SourceId == "missing").Availability);
    }
    [TestMethod]
    public async Task Codex_SnapshotOnlyExportsChosenThreadRows()
    {
        var raw = Write(Path.Combine("sessions", "chosen.jsonl"), "{\"type\":\"user\"}\n"); var state = Path.Combine(_root, "state_5.sqlite"); var history = Path.Combine(_root, "thread_history_1.sqlite");
        Sql(state, "CREATE TABLE threads(id TEXT,title TEXT); INSERT INTO threads VALUES('chosen','Chosen'),('other','Unselected');");
        Sql(history, "CREATE TABLE thread_items(thread_id TEXT,item_json TEXT); INSERT INTO thread_items VALUES('chosen','{}'),('other','{\"private\":true}'); CREATE TABLE thread_turns(thread_id TEXT,turn_id TEXT); INSERT INTO thread_turns VALUES('chosen','1'),('other','2');");
        var stage = Path.Combine(_root, "snapshot"); Directory.CreateDirectory(stage);
        var descriptor = new SessionDescriptor { Key = "Codex:chosen", Source = "Codex", SourceId = "chosen", Title = "Chosen", Path = raw, MetadataPath = state, HistoryPath = history };
        var files = await new CodexSessionSource(_root).SnapshotAsync(descriptor, stage, default); Assert.AreEqual(3, files.Count);
        using var db = SessionSnapshot.OpenRead(files.Single(x => x.RelativePath == "thread-history.sqlite").Path);
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT GROUP_CONCAT(thread_id) FROM thread_items"; Assert.AreEqual("chosen", cmd.ExecuteScalar());
    }
    [TestMethod]
    public async Task Codex_RelocatedArchiveRetainsMetadataAndResolvesMissingOriginal()
    {
        var id = Guid.NewGuid().ToString(); var raw = Write(Path.Combine("archived_sessions", "rollout-2026-01-01-" + id + ".jsonl"), "{}\n");
        var state = Path.Combine(_root, "state_5.sqlite");
        Sql(state, "CREATE TABLE threads(id TEXT,title TEXT,rollout_path TEXT,cwd TEXT,updated_at INTEGER,archived INTEGER,source TEXT);" +
            $"INSERT INTO threads VALUES('{id}','Preserved title','missing-original-path','Demo project',0,1,'cli');");
        var item = (await new CodexSessionSource(_root).DiscoverAsync(default)).Single();
        Assert.AreEqual(raw, item.Path); Assert.AreEqual("Available", item.Availability);
        Assert.AreEqual("Preserved title", item.Title); Assert.AreEqual(state, item.MetadataPath); Assert.IsTrue(item.Archived);
    }
    [TestMethod]
    public async Task Claude_TitleOverridesPreviewAndSubagentsAreSeparate()
    {
        var id = Guid.NewGuid().ToString(); var records = new[]
        {
            JsonSerializer.Serialize(new { type = "user", message = new { content = "First question\n第二行" }, cwd = "Demo project" }),
            JsonSerializer.Serialize(new { type = "summary", summary = "Summary title" }),
            JsonSerializer.Serialize(new { type = "custom-title", customTitle = "Chosen name" })
        };
        Write(Path.Combine("project", id + ".jsonl"), string.Join('\n', records) + "\n");
        Write(Path.Combine("project", id, "subagents", "agent-demo.jsonl"), records[0] + "\n");
        var items = await new ClaudeSessionSource(_root).DiscoverAsync(default);
        Assert.AreEqual(2, items.Count); var main = items.Single(x => x.SourceId == id); Assert.AreEqual("Chosen name", main.Title); Assert.IsFalse(main.PreviewTitle);
        var child = items.Single(x => x.SourceId == "agent-demo"); Assert.AreEqual("Claude Code:" + id, child.ParentKey); Assert.IsTrue(child.PreviewTitle);
        Assert.AreEqual("First question 第二行", child.Title);
    }
    [TestMethod]
    public async Task Codex_ExtendedLengthPathIsAcceptedOnlyInsideManagedArchive()
    {
        var raw = Write(Path.Combine("sessions", "chosen.jsonl"), "{\"type\":\"user\"}\n");
        var descriptor = new SessionDescriptor { Key = "Codex:chosen", Source = "Codex", SourceId = "chosen", Title = "Chosen", Path = @"\\?\" + raw };
        var stage = Path.Combine(_root, "extended-snapshot"); Directory.CreateDirectory(stage);
        var files = await new CodexSessionSource(_root).SnapshotAsync(descriptor, stage, default);
        Assert.AreEqual("{\"type\":\"user\"}\n", await File.ReadAllTextAsync(files.Single().Path));
        var outside = Write("credentials.txt", "must not be exported");
        Assert.ThrowsException<InvalidDataException>(() => SessionSnapshot.RequireManagedPath(@"\\?\" + outside, Path.Combine(_root, "sessions")));
        Assert.ThrowsException<InvalidDataException>(() => SessionSnapshot.RequireManagedPath(@"\\?\" + Path.Combine(_root, "sessions", "..", "credentials.txt"), Path.Combine(_root, "sessions")));
        Assert.ThrowsException<InvalidDataException>(() => SessionSnapshot.NormalizeManagedPath(@"\\?\GLOBALROOT\Device\HarddiskVolume1\data"));
    }
    [TestMethod]
    public void SelectedThreadExport_DoesNotRequireOrIncludeUnselectedForeignTables()
    {
        var source = Path.Combine(_root, "state_5.sqlite");
        Sql(source, "CREATE TABLE projects(id TEXT PRIMARY KEY,private_notes TEXT); INSERT INTO projects VALUES('p','not part of the selected conversation'); " +
            "CREATE TABLE threads(id TEXT PRIMARY KEY,title TEXT,project_id TEXT REFERENCES projects(id)); INSERT INTO threads VALUES('chosen','Selected','p'),('other','Do not export','p');");
        var stage = Path.Combine(_root, "foreign-keys-snapshot"); Directory.CreateDirectory(stage);
        var file = SessionSnapshot.ExportRows(source, stage, "thread-metadata.sqlite", "id", "chosen", "threads");
        using var db = SessionSnapshot.OpenRead(file.Path); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT GROUP_CONCAT(id) FROM threads"; Assert.AreEqual("chosen", cmd.ExecuteScalar());
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='projects'"; Assert.AreEqual(0L, cmd.ExecuteScalar());
        cmd.CommandText = "PRAGMA quick_check"; Assert.AreEqual("ok", cmd.ExecuteScalar());
    }
    [TestMethod]
    public async Task Antigravity_UsesSummaryTitleAndMakesConsistentSnapshotOfLiveWal()
    {
        var id = Guid.NewGuid().ToString(); var metadata = Path.Combine(_root, "conversation_summaries.db");
        Sql(metadata, "CREATE TABLE conversation_summaries(conversation_id TEXT,title TEXT,last_modified_time TEXT,workspace_uris TEXT,parent_conversation_id TEXT);" +
            $"INSERT INTO conversation_summaries VALUES('{id}','Design idea','2026-01-01T12:00:00Z','Demo project','');");
        var raw = Path.Combine(_root, "conversations", id + ".db"); Directory.CreateDirectory(Path.GetDirectoryName(raw)!);
        using var live = new SqliteConnection($"Data Source={raw};Pooling=False"); live.Open();
        using (var cmd = live.CreateCommand()) { cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE steps(idx INTEGER PRIMARY KEY,payload BLOB); INSERT INTO steps VALUES(1,X'010203');"; cmd.ExecuteNonQuery(); }
        var source = new AntigravitySessionSource(_root); var item = (await source.DiscoverAsync(default)).Single(); Assert.AreEqual("Design idea", item.Title);
        var stage = Path.Combine(_root, "snapshot"); Directory.CreateDirectory(stage); var files = await source.SnapshotAsync(item, stage, default);
        using var db = SessionSnapshot.OpenRead(files.Single(x => x.RelativePath == id + ".db").Path); using var query = db.CreateCommand(); query.CommandText = "SELECT hex(payload) FROM steps";
        Assert.AreEqual("010203", query.ExecuteScalar()); Assert.IsFalse(Directory.EnumerateFiles(stage).Any(x => x.EndsWith("-wal") || x.EndsWith("-shm")));
    }
    [TestMethod]
    public async Task ManualUnknownExport_IsArchivedUnselectedWithoutRewritingOriginal()
    {
        var path = Write("official-export.html", "<html>official export</html>"); var root = Path.Combine(_root, "Imports"); var source = new ImportedSessionSource(root);
        var item = await source.ImportAsync(path, "My exported chat", default); Assert.AreNotEqual(path, item.Path);
        Assert.AreEqual("<html>official export</html>", await File.ReadAllTextAsync(path));
        var catalog = new SessionCatalog(Path.Combine(_root, "Archive")); catalog.Merge(await source.DiscoverAsync(default)); Assert.IsFalse(catalog.List().Single().Selected);
    }
    [TestMethod]
    public async Task ReferencedClipboardPng_IsIncludedAndMissingImagesAreReported()
    {
        var path = Path.Combine(_root, "codex-clipboard-" + Guid.NewGuid() + ".png");
        await File.WriteAllBytesAsync(path, new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3 });
        var missing = Path.Combine(_root, "codex-clipboard-" + Guid.NewGuid() + ".png");
        var stage = Path.Combine(_root, "snapshot"); Directory.CreateDirectory(stage); var files = new List<SnapshotFile>();
        await SessionSnapshot.AddCodexClipboardImagesAsync([path, missing], stage, files, default);
        Assert.IsTrue(files.Any(x => x.RelativePath.EndsWith(Path.GetFileName(path))));
        Assert.IsTrue(files.Any(x => x.IncompleteAttachments));
        Assert.IsTrue((await File.ReadAllTextAsync(files.Single(x => x.IncompleteAttachments).Path)).Contains(Path.GetFileName(missing)));
    }
    [TestMethod]
    public async Task SourceScope_RejectsCredentialFilesOutsideManagedDirectories()
    {
        var path = Write("credentials.txt", "synthetic credential fixture"); var imports = new ImportedSessionSource(Path.Combine(_root, "Imports"));
        var item = new SessionDescriptor { Key = "Imported:bad", Source = "Imported", SourceId = "bad", Title = "Bad source", Path = path };
        var stage = Path.Combine(_root, "snapshot"); Directory.CreateDirectory(stage);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => imports.SnapshotAsync(item, stage, default));
        Assert.AreEqual(0, Directory.EnumerateFiles(stage).Count());
    }
}
