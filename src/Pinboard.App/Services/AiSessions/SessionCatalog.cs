using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Pinboard.App.Services.AiSessions;

// Connections are short-lived. All discovery/backup work runs away from the UI thread.
public sealed class SessionCatalog
{
    public string Root { get; }
    public string DatabasePath => System.IO.Path.Combine(Root, "catalog.sqlite");
    public SessionCatalog(string root)
    {
        Root = System.IO.Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            INSERT OR IGNORE INTO meta VALUES('schema_version','1');
            CREATE TABLE IF NOT EXISTS sessions(key TEXT PRIMARY KEY,descriptor TEXT NOT NULL,
                selected INTEGER NOT NULL DEFAULT 0,display_name TEXT,status TEXT NOT NULL DEFAULT 'Not selected',
                error TEXT,last_backup TEXT);
            CREATE TABLE IF NOT EXISTS versions(id TEXT PRIMARY KEY,session_key TEXT NOT NULL,created_at TEXT NOT NULL,
                manifest_path TEXT NOT NULL,size INTEGER NOT NULL,cloud_account TEXT,cloud_file_id TEXT);
            CREATE INDEX IF NOT EXISTS versions_session ON versions(session_key,created_at);
            CREATE TABLE IF NOT EXISTS objects(account TEXT NOT NULL,hash TEXT NOT NULL,file_id TEXT NOT NULL DEFAULT '',
                upload_uri TEXT,offset INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(account,hash));
            CREATE TABLE IF NOT EXISTS jobs(session_key TEXT PRIMARY KEY,due_at TEXT NOT NULL,attempts INTEGER NOT NULL DEFAULT 0);
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT name FROM pragma_table_info('jobs');";
        var columns = new HashSet<string>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(0));
        using var migration = db.BeginTransaction(); command.Transaction = migration;
        if (!columns.Contains("blocked")) { command.CommandText = "ALTER TABLE jobs ADD COLUMN blocked INTEGER NOT NULL DEFAULT 0;"; command.ExecuteNonQuery(); }
        if (!columns.Contains("failure_kind")) { command.CommandText = "ALTER TABLE jobs ADD COLUMN failure_kind INTEGER NOT NULL DEFAULT 0;"; command.ExecuteNonQuery(); }
        command.CommandText = "UPDATE meta SET value='2' WHERE key='schema_version';"; command.ExecuteNonQuery(); migration.Commit();
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, DefaultTimeout = 5, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL;";
        cmd.ExecuteNonQuery();
        return db;
    }
    private void Execute(string sql, params (string Name, object? Value)[] values)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var value in values) cmd.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
    public void Merge(IEnumerable<SessionDescriptor> sessions)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        foreach (var item in sessions)
        {
            using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO sessions(key,descriptor) VALUES($key,$data) ON CONFLICT(key) DO UPDATE SET descriptor=excluded.descriptor;";
            cmd.Parameters.AddWithValue("$key", item.Key); cmd.Parameters.AddWithValue("$data", JsonSerializer.Serialize(item)); cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
    public IReadOnlyList<SessionEntry> List()
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT descriptor,selected,display_name,status,error,last_backup FROM sessions;";
        using var reader = cmd.ExecuteReader(); var result = new List<SessionEntry>();
        while (reader.Read()) result.Add(ReadEntry(reader));
        return result.OrderByDescending(x => x.Descriptor.UpdatedAt).ToArray();
    }
    private static SessionEntry ReadEntry(SqliteDataReader reader) => new(JsonSerializer.Deserialize<SessionDescriptor>(reader.GetString(0))!,
        reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)));
    public SessionEntry? Find(string key)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT descriptor,selected,display_name,status,error,last_backup FROM sessions WHERE key=$key;";
        cmd.Parameters.AddWithValue("$key", key); using var reader = cmd.ExecuteReader(); return reader.Read() ? ReadEntry(reader) : null;
    }
    public void Select(string key, bool selected) => SetSelection([key], selected);
    public int SetSelection(IEnumerable<string> keys, bool selected)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        using var update = db.CreateCommand(); update.Transaction = tx;
        update.CommandText = "UPDATE sessions SET selected=$v,status=CASE WHEN $v=1 THEN 'Selected' ELSE 'Not selected' END,error=NULL WHERE key=$key AND selected<>$v;";
        update.Parameters.AddWithValue("$v", selected); var updateKey = update.Parameters.AddWithValue("$key", "");
        using var job = db.CreateCommand(); job.Transaction = tx;
        job.CommandText = selected
            ? "INSERT INTO jobs(session_key,due_at) VALUES($key,$now) ON CONFLICT(session_key) DO UPDATE SET due_at=$now;"
            : "DELETE FROM jobs WHERE session_key=$key;";
        var jobKey = job.Parameters.AddWithValue("$key", "");
        if (selected) job.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        var changed = 0;
        // One transaction keeps large selections responsive and selection/queue state consistent.
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            updateKey.Value = key;
            if (update.ExecuteNonQuery() == 0) continue;
            jobKey.Value = key; job.ExecuteNonQuery(); changed++;
        }
        tx.Commit(); return changed;
    }
    public void Rename(string key, string? name) => Execute("UPDATE sessions SET display_name=$name WHERE key=$key;", ("$name", name), ("$key", key));
    public void SetStatus(string key, string status, string? error = null) => Execute(
        "UPDATE sessions SET status=$status,error=$error WHERE key=$key;", ("$key", key), ("$status", status), ("$error", error));
    public void Queue(string key) => QueueMany([key]);
    public void QueueMany(IEnumerable<string> keys, bool restartExisting = true)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO jobs(session_key,due_at) SELECT key,$now FROM sessions WHERE key=$key AND selected=1 ON CONFLICT(session_key) "
            + (restartExisting ? "DO UPDATE SET due_at=$now,blocked=0;" : "DO NOTHING;");
        var keyParameter = cmd.Parameters.AddWithValue("$key", ""); cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        foreach (var key in keys.Distinct(StringComparer.Ordinal)) { keyParameter.Value = key; cmd.ExecuteNonQuery(); }
        tx.Commit();
    }
    private const string ProgressRows = """
        WITH progress AS (
            SELECT s.key,s.status,s.error,s.descriptor,j.session_key AS queued,j.blocked,
                v.id AS version_id,v.cloud_file_id,v.cloud_account,
                CASE WHEN s.key=$active THEN 1 ELSE 0 END AS active,
                CASE WHEN s.error IS NOT NULL OR s.status='Retry pending' THEN 1 ELSE 0 END AS failed,
                CASE WHEN instr(s.status,'missing ')>0 OR json_extract(s.descriptor,'$.Availability')='Missing source' THEN 1 ELSE 0 END AS warning,
                CASE WHEN v.id IS NOT NULL AND ($cloud=0 OR (v.cloud_file_id IS NOT NULL AND v.cloud_account=$account))
                    AND s.status IN ('Cloud backup','Local backup') AND ($cloud=0 OR s.status='Cloud backup')
                    THEN 1 ELSE 0 END AS target_saved
            FROM sessions s LEFT JOIN jobs j ON j.session_key=s.key
            LEFT JOIN versions v ON v.id=(SELECT id FROM versions WHERE session_key=s.key ORDER BY created_at DESC,id DESC LIMIT 1)
            WHERE s.selected=1
        )
        """;
    public SessionBackupProgress ReadProgress(bool cloud, string? account, bool paused = false, SessionBackupActivity? current = null)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = ProgressRows + """
            SELECT COUNT(*),
                COALESCE(SUM(CASE WHEN target_saved=1 AND queued IS NULL AND failed=0 AND warning=0 AND active=0 THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN version_id IS NOT NULL THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN cloud_file_id IS NOT NULL AND cloud_account=$account THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN queued IS NOT NULL THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN failed=1 AND active=0 THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN warning=1 AND failed=0 AND active=0 THEN 1 ELSE 0 END),0),
                COALESCE(SUM(active),0),
                COALESCE(SUM(CASE WHEN failed=1 AND active=0 AND blocked=1 THEN 1 ELSE 0 END),0) FROM progress;
            """;
        AddProgressParameters(cmd, cloud, account, current?.Key);
        using var reader = cmd.ExecuteReader(); reader.Read();
        return new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
            reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), cloud, paused, current, reader.GetInt32(8));
    }
    public HashSet<string> SettledSessionKeys(bool cloud, string? account)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = ProgressRows + "SELECT key FROM progress WHERE target_saved=1 AND queued IS NULL AND failed=0 AND warning=0;";
        AddProgressParameters(cmd, cloud, account, null);
        using var reader = cmd.ExecuteReader(); var keys = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) keys.Add(reader.GetString(0)); return keys;
    }
    private static void AddProgressParameters(SqliteCommand cmd, bool cloud, string? account, string? active)
    {
        cmd.Parameters.AddWithValue("$cloud", cloud); cmd.Parameters.AddWithValue("$account", account ?? ""); cmd.Parameters.AddWithValue("$active", active ?? "");
    }
    public string? NextJob()
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT j.session_key FROM jobs j JOIN sessions s ON s.key=j.session_key WHERE s.selected=1 AND j.blocked=0 AND j.due_at <= $now ORDER BY j.due_at LIMIT 1;";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); return cmd.ExecuteScalar() as string;
    }
    public void FinishJob(string key) => Execute("DELETE FROM jobs WHERE session_key=$key;", ("$key", key));
    public SessionJobInfo? GetJob(string key)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT attempts,due_at,blocked,failure_kind FROM jobs WHERE session_key=$key;"; cmd.Parameters.AddWithValue("$key", key);
        using var r = cmd.ExecuteReader(); return r.Read() ? new(r.GetInt32(0), DateTimeOffset.Parse(r.GetString(1)), r.GetBoolean(2), (SessionFailureKind)r.GetInt32(3)) : null;
    }
    public void RetryJob(string key, SessionFailureKind kind = SessionFailureKind.Transient)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT attempts FROM jobs WHERE session_key=$key;"; cmd.Parameters.AddWithValue("$key", key);
        if (cmd.ExecuteScalar() is not long attempts) return;
        cmd.CommandText = "UPDATE jobs SET attempts=attempts+1,due_at=$due,blocked=$blocked,failure_kind=$kind WHERE session_key=$key;";
        cmd.Parameters.AddWithValue("$due", DateTimeOffset.UtcNow.Add(SessionRetryPolicy.Delay((int)Math.Min(int.MaxValue, attempts + 1))).ToString("O"));
        cmd.Parameters.AddWithValue("$blocked", kind != SessionFailureKind.Transient); cmd.Parameters.AddWithValue("$kind", (int)kind);
        cmd.ExecuteNonQuery(); tx.Commit();
    }
    public IReadOnlySet<string> StoredSessionKeys(bool cloud, string? account)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT s.key FROM sessions s JOIN versions v ON v.id=(SELECT id FROM versions WHERE session_key=s.key ORDER BY created_at DESC,id DESC LIMIT 1)
            WHERE s.selected=1 AND s.error IS NULL AND (s.status LIKE 'Cloud backup%' OR s.status LIKE 'Local backup%')
                AND ($cloud=0 OR (v.cloud_file_id IS NOT NULL AND v.cloud_account=$account));
            """;
        cmd.Parameters.AddWithValue("$cloud", cloud); cmd.Parameters.AddWithValue("$account", account ?? "");
        using var r = cmd.ExecuteReader(); var result = new HashSet<string>(StringComparer.Ordinal);
        while (r.Read()) result.Add(r.GetString(0)); return result;
    }
    public IReadOnlyList<SessionVersion> Versions(string key)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT id,created_at,manifest_path,size,cloud_account,cloud_file_id FROM versions WHERE session_key=$key ORDER BY created_at DESC;";
        cmd.Parameters.AddWithValue("$key", key); using var r = cmd.ExecuteReader(); var result = new List<SessionVersion>();
        while (r.Read()) result.Add(new(r.GetString(0), key, DateTimeOffset.Parse(r.GetString(1)), r.GetString(2), r.GetInt64(3),
            r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5))); return result;
    }
    public void AddVersion(SessionManifest manifest, string path)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO versions(id,session_key,created_at,manifest_path,size) VALUES($id,$key,$time,$path,$size); UPDATE sessions SET last_backup=MAX(COALESCE(last_backup,''),$time),status='Local backup',error=NULL WHERE key=$key;";
        cmd.Parameters.AddWithValue("$id", manifest.VersionId); cmd.Parameters.AddWithValue("$key", manifest.Session.Key);
        cmd.Parameters.AddWithValue("$time", manifest.CreatedAt.ToString("O")); cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$size", manifest.Files.Sum(x => x.Length)); cmd.ExecuteNonQuery(); tx.Commit();
    }
    public void MarkUploaded(string id, string account, string fileId) => Execute(
        "UPDATE versions SET cloud_account=$a,cloud_file_id=$f WHERE id=$id;", ("$id", id), ("$a", account), ("$f", fileId));
    public RemoteObject? GetObject(string account, string hash)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT file_id,upload_uri,offset FROM objects WHERE account=$a AND hash=$h;";
        cmd.Parameters.AddWithValue("$a", account); cmd.Parameters.AddWithValue("$h", hash); using var r = cmd.ExecuteReader();
        return r.Read() ? new(hash, r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt64(2)) : null;
    }
    public void PutObject(string account, RemoteObject item) => Execute("INSERT INTO objects(account,hash,file_id,upload_uri,offset) VALUES($a,$h,$f,$u,$o) ON CONFLICT(account,hash) DO UPDATE SET file_id=$f,upload_uri=$u,offset=$o;",
        ("$a", account), ("$h", item.Hash), ("$f", item.FileId), ("$u", item.UploadUri), ("$o", item.Offset));
}
