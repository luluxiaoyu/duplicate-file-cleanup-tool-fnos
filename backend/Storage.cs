using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Security.Cryptography;
using System.Text;

namespace DuplicateCleanup;

public sealed class TaskStore
{
    public string Data { get; }
    readonly string catalog;
    public TaskStore(AppPaths paths)
    {
        Data = paths.Data; Directory.CreateDirectory(Path.Combine(Data, "tasks"));
        catalog = Path.Combine(Data, "catalog.db");
        using var db = Open(catalog);
        Sql(db, "CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY,owner INTEGER NOT NULL,created TEXT NOT NULL,key TEXT NOT NULL,request_hash TEXT NOT NULL,UNIQUE(owner,key)); CREATE INDEX IF NOT EXISTS jobs_owner ON jobs(owner,created DESC);");
    }
    public static SqliteConnection Open(string file)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        db.Open(); Sql(db, "PRAGMA busy_timeout=10000; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;"); return db;
    }
    public static void Sql(SqliteConnection db, string sql, params (string, object?)[] args)
    { using var c = Command(db, sql, args); c.ExecuteNonQuery(); }
    public static SqliteCommand Command(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    public static string Id() => Guid.NewGuid().ToString("N");
    public string DirectoryFor(string id)
    {
        if (id.Length != 32 || !id.All(Uri.IsHexDigit)) throw new AppError("任务不存在", 404);
        return Path.Combine(Data, "tasks", id);
    }
    public TaskDb Db(string id) => new(Path.Combine(DirectoryFor(id), "task.db"));
    public Job Get(string id, uint? uid = null)
    {
        if (!File.Exists(Path.Combine(DirectoryFor(id), "task.db"))) throw new AppError("任务不存在", 404);
        using var db = Db(id); var job = db.Job();
        if (uid is not null && job.Owner != uid) throw new AppError("任务不存在", 404);
        return job;
    }
    public IEnumerable<string> Ids(uint? uid = null, long offset = 0, int limit = 50)
    {
        using var db = Open(catalog);
        using var c = Command(db, "SELECT id FROM jobs WHERE ($uid IS NULL OR owner=$uid) ORDER BY created DESC,id LIMIT $limit OFFSET $offset", ("$uid", uid), ("$limit", limit), ("$offset", offset));
        using var r = c.ExecuteReader(); while (r.Read()) yield return r.GetString(0);
    }
    public Job? Existing(uint uid, string key, string requestHash)
    {
        if (key.Length is < 8 or > 128 || key.Any(char.IsControl)) throw new AppError("幂等标识无效");
        using var db = Open(catalog); using var c = Command(db, "SELECT id,request_hash FROM jobs WHERE owner=$uid AND key=$key", ("$uid", uid), ("$key", key));
        using var r = c.ExecuteReader(); if (!r.Read()) return null;
        if (r.GetString(1) != requestHash) throw new AppError("同一幂等标识不能用于不同请求", 409);
        return Get(r.GetString(0), uid);
    }
    public void Create(Job job, string key, string requestHash, Action<TaskDb>? initialize = null)
    {
        var dir = DirectoryFor(job.Id); Directory.CreateDirectory(dir);
        try
        {
            using (var task = Db(job.Id)) { task.Save(job); initialize?.Invoke(task); }
            using (var descriptor = new FileStream(Path.Combine(dir, "task.json"), FileMode.CreateNew, FileAccess.Write))
            { descriptor.Write(JsonSerializer.SerializeToUtf8Bytes(job, AppJson.Default.Job)); descriptor.Flush(true); }
            if (OperatingSystem.IsLinux())
            {
                LinuxFiles.SyncAppDirectory(dir); LinuxFiles.SyncAppDirectory(Path.Combine(Data, "tasks")); LinuxFiles.SyncAppDirectory(Data);
            }
            using var db = Open(catalog);
            Sql(db, "INSERT INTO jobs VALUES($id,$uid,$created,$key,$hash)", ("$id", job.Id), ("$uid", job.Owner), ("$created", job.CreatedAt.ToString("O")), ("$key", key), ("$hash", requestHash));
        }
        catch { Directory.Delete(dir, true); throw; }
    }
    public static string Hash<T>(T value, JsonTypeInfo<T> info) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, info))));
    public void Recover()
    {
        long offset = 0;
        while (true)
        {
            var ids = Ids(offset: offset).ToArray(); if (ids.Length == 0) break; offset += ids.Length;
            foreach (var id in ids)
            {
                using var db = Db(id); var j = db.Job();
                if (j.State is "running" or "queued" or "cancelling") { j.State = "interrupted"; j.Stage = "已中断，等待手动继续"; db.Save(j); }
            }
        }
    }
}

public sealed class TaskDb : IDisposable
{
    public SqliteConnection Connection { get; }
    public TaskDb(string path)
    {
        Connection = TaskStore.Open(path);
        using (var version = TaskStore.Command(Connection, "PRAGMA user_version"))
            if ((long)version.ExecuteScalar()! > 1) throw new AppError("任务数据由较新版本创建，请使用对应或更新的应用版本", 409);
        TaskStore.Sql(Connection, """
        CREATE TABLE IF NOT EXISTS meta(id INTEGER PRIMARY KEY CHECK(id=1),json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS files(id INTEGER PRIMARY KEY,path TEXT NOT NULL UNIQUE,root TEXT NOT NULL,relative TEXT NOT NULL,root_order INTEGER NOT NULL,size INTEGER NOT NULL,stamp TEXT NOT NULL,sample TEXT,hash TEXT,group_id INTEGER,sharing TEXT,seen INTEGER NOT NULL DEFAULT 1,inode_key TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS files_size ON files(size,id);
        CREATE INDEX IF NOT EXISTS files_candidate ON files(size,sample,hash,id);
        CREATE INDEX IF NOT EXISTS files_group ON files(group_id,id);
        CREATE INDEX IF NOT EXISTS files_inode ON files(inode_key,id);
        CREATE TABLE IF NOT EXISTS groups(id INTEGER PRIMARY KEY,keeper INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS operations(id TEXT PRIMARY KEY,state TEXT NOT NULL,json TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS operations_state ON operations(state,id);
        CREATE TABLE IF NOT EXISTS errors(id INTEGER PRIMARY KEY,message TEXT NOT NULL);
        PRAGMA user_version=1;
        """);
    }
    public void Save(Job j)
    {
        j.UpdatedAt = DateTimeOffset.UtcNow;
        TaskStore.Sql(Connection, "INSERT INTO meta VALUES(1,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json", ("$json", JsonSerializer.Serialize(j, AppJson.Default.Job)));
    }
    public Job Job()
    {
        using var c = TaskStore.Command(Connection, "SELECT json FROM meta WHERE id=1");
        var value = c.ExecuteScalar() as string ?? throw new AppError("任务数据损坏", 500);
        return JsonSerializer.Deserialize(value, AppJson.Default.Job) ?? throw new AppError("任务数据损坏", 500);
    }
    public void ResetEnumeration()
    {
        TaskStore.Sql(Connection, "UPDATE files SET seen=0,group_id=NULL,sharing=NULL; DELETE FROM groups;");
    }
    public bool Upsert(string root, string relative, int order, FileStamp stamp)
    {
        var path = Path.Combine(root, relative);
        using var c = TaskStore.Command(Connection, "SELECT id,stamp,seen FROM files WHERE path=$path", ("$path", path));
        using var r = c.ExecuteReader(); long? id = null; var same = false;
        if (r.Read()) { if (r.GetInt64(2) == 1) return false; id = r.GetInt64(0); same = JsonSerializer.Deserialize(r.GetString(1), AppJson.Default.FileStamp)!.SameContentVersion(stamp); }
        r.Close();
        if (id is null)
            TaskStore.Sql(Connection, "INSERT INTO files(path,root,relative,root_order,size,stamp,inode_key) VALUES($path,$root,$relative,$order,$size,$stamp,$inode)",
                ("$path", path), ("$root", root), ("$relative", relative), ("$order", order), ("$size", stamp.Size), ("$stamp", JsonSerializer.Serialize(stamp, AppJson.Default.FileStamp)), ("$inode", InodeKey(stamp)));
        else
            TaskStore.Sql(Connection, "UPDATE files SET root=$root,relative=$relative,root_order=$order,size=$size,stamp=$stamp,inode_key=$inode,seen=1,sample=CASE WHEN $same THEN sample END,hash=CASE WHEN $same THEN hash END WHERE id=$id",
                ("$root", root), ("$relative", relative), ("$order", order), ("$size", stamp.Size), ("$stamp", JsonSerializer.Serialize(stamp, AppJson.Default.FileStamp)), ("$same", same), ("$id", id), ("$inode", InodeKey(stamp)));
        return true;
    }
    public IEnumerable<FileEntry> Files(string where = "1=1", (string, object?)[]? args = null)
    {
        var clause = where.Split(" LIMIT ", 2, StringSplitOptions.None);
        using var c = TaskStore.Command(Connection, "SELECT id,root,relative,root_order,stamp,sample,hash,group_id,sharing FROM files WHERE " + clause[0] + " ORDER BY id" + (clause.Length == 2 ? " LIMIT " + clause[1] : ""), args ?? []);
        using var r = c.ExecuteReader();
        while (r.Read()) yield return new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3),
            JsonSerializer.Deserialize(r.GetString(4), AppJson.Default.FileStamp)!, r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
            r.IsDBNull(7) ? null : r.GetInt64(7), r.IsDBNull(8) ? null : JsonSerializer.Deserialize(r.GetString(8), AppJson.Default.SharingInfo));
    }
    public FileEntry File(long id) => Files("id=$id", [("$id", id)]).FirstOrDefault() ?? throw new AppError("文件记录不存在", 404);
    public long Count(string table, string where = "1=1", params (string, object?)[] args)
    { using var c = TaskStore.Command(Connection, $"SELECT COUNT(*) FROM {table} WHERE {where}", args); return (long)c.ExecuteScalar()!; }
    public void Fingerprint(long id, string column, string hash)
    {
        if (column is not ("sample" or "hash")) throw new InvalidOperationException();
        TaskStore.Sql(Connection, $"UPDATE files SET {column}=$hash WHERE id=$id", ("$hash", hash), ("$id", id));
    }
    static string InodeKey(FileStamp stamp) => $"{stamp.Device:x}:{stamp.Inode:x}";
    public string? InodeFingerprint(FileEntry file, string column)
    {
        if (column is not ("sample" or "hash")) throw new InvalidOperationException();
        using var c = TaskStore.Command(Connection, $"SELECT {column} FROM files WHERE inode_key=$inode AND stamp=$stamp AND {column} IS NOT NULL LIMIT 1", ("$inode", InodeKey(file.Stamp)), ("$stamp", JsonSerializer.Serialize(file.Stamp, AppJson.Default.FileStamp)));
        return c.ExecuteScalar() as string;
    }
    public void Remove(long id) => TaskStore.Sql(Connection, "DELETE FROM files WHERE id=$id", ("$id", id));
    public void Group(long id, long keeper) => TaskStore.Sql(Connection, "INSERT INTO groups VALUES($id,$keeper)", ("$id", id), ("$keeper", keeper));
    public void Membership(long file, long group, SharingInfo sharing) => TaskStore.Sql(Connection, "UPDATE files SET group_id=$group,sharing=$sharing WHERE id=$id", ("$id", file), ("$group", group), ("$sharing", JsonSerializer.Serialize(sharing, AppJson.Default.SharingInfo)));
    public IEnumerable<(long Id, long Keeper)> Groups(long after = 0, int limit = 20)
    {
        using var c = TaskStore.Command(Connection, "SELECT id,keeper FROM groups WHERE id>$id ORDER BY id LIMIT $limit", ("$id", after), ("$limit", limit));
        using var r = c.ExecuteReader(); while (r.Read()) yield return (r.GetInt64(0), r.GetInt64(1));
    }
    public void Save(Operation op) => TaskStore.Sql(Connection, "INSERT INTO operations VALUES($id,$state,$json) ON CONFLICT(id) DO UPDATE SET state=excluded.state,json=excluded.json", ("$id", op.Id), ("$state", op.State), ("$json", JsonSerializer.Serialize(op, AppJson.Default.Operation)));
    public Operation Operation(string id)
    {
        using var c = TaskStore.Command(Connection, "SELECT json FROM operations WHERE id=$id", ("$id", id));
        return JsonSerializer.Deserialize(c.ExecuteScalar() as string ?? throw new AppError("操作记录不存在", 404), AppJson.Default.Operation)!;
    }
    public IEnumerable<Operation> Operations(string where = "1=1", string after = "", int limit = 100)
    {
        using var c = TaskStore.Command(Connection, "SELECT json FROM operations WHERE id>$after AND (" + where + ") ORDER BY id LIMIT $limit", ("$after", after), ("$limit", limit));
        using var r = c.ExecuteReader(); while (r.Read()) yield return JsonSerializer.Deserialize(r.GetString(0), AppJson.Default.Operation)!;
    }
    public void Error(string message) => TaskStore.Sql(Connection, "INSERT INTO errors(message) VALUES($message)", ("$message", message));
    public void Dispose() => Connection.Dispose();
}
