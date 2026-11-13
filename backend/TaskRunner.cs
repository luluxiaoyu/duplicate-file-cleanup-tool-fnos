using System.Text.Json;
using System.Threading.Channels;

namespace DuplicateCleanup;

public sealed class TaskRunner(TaskStore store, Scanner scanner, Cleaner cleaner, AccessGuard access) : BackgroundService
{
    readonly Channel<string> queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    readonly Dictionary<string, CancellationTokenSource> running = [];
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public void Enqueue(string id) => queue.Writer.TryWrite(id);
    public async Task<Job> Scan(uint uid, ScanRequest request, CancellationToken ct)
    {
        if (request.Roots.Length is < 1 or > 64) throw new AppError("请选择 1 至 64 个目录");
        await Gate.WaitAsync(ct);
        try
        {
            var hash = TaskStore.Hash(request, AppJson.Default.ScanRequest);
            if (store.Existing(uid, request.IdempotencyKey, hash) is { } existing) return existing;
            var roots = request.Roots.Select(Paths.Normalize).Distinct().ToArray();
            await access.CheckRoots(uid, roots, ct);
            // Keep explicit nested roots: a separately mounted child may need its own scan.
            var job = new Job { Id = TaskStore.Id(), Owner = uid, Kind = "scan", Roots = roots, PriorityRoots = (request.PriorityRoots ?? []).Select(Paths.Normalize).ToArray() };
            store.Create(job, request.IdempotencyKey, hash); Enqueue(job.Id); return job;
        }
        finally { Gate.Release(); }
    }
    public async Task<Job> Cleanup(uint uid, CleanupRequest request, CancellationToken ct)
    {
        Cleaner.Validate(request);
        if (!request.Confirmed) throw new AppError("请先确认清理预览");
        await Gate.WaitAsync(ct);
        try
        {
            var hash = TaskStore.Hash(request, AppJson.Default.CleanupRequest);
            if (store.Existing(uid, request.IdempotencyKey, hash) is { } existing) return existing;
            var scan = RequireScan(uid, request.ScanId);
            await access.CheckRoots(uid, scan.Roots, ct);
            var job = new Job { Id = TaskStore.Id(), Owner = uid, Kind = "cleanup", Method = request.Method, ScanId = scan.Id, Roots = scan.Roots, PriorityRoots = scan.PriorityRoots };
            store.Create(job, request.IdempotencyKey, hash, db => SaveRequest(db, JsonSerializer.Serialize(request, AppJson.Default.CleanupRequest)));
            Enqueue(job.Id); return job;
        }
        finally { Gate.Release(); }
    }
    public Job RequireScan(uint uid, string id)
    {
        var scan = store.Get(id, uid);
        if (scan.Kind != "scan" || scan.State is not ("completed" or "completedWithErrors")) throw new AppError("请等待扫描完成");
        return scan;
    }
    public async Task<Job> Trash(uint uid, TrashRequest request, CancellationToken ct)
    {
        if (request.Kind is not ("restore" or "purge") || request.Items.Length is < 1 or > 1000 || !request.Confirmed)
            throw new AppError("回收站操作无效或尚未确认");
        if (request.Items.Distinct().Count() != request.Items.Length) throw new AppError("不能重复选择同一文件");
        await Gate.WaitAsync(ct);
        try
        {
            var hash = TaskStore.Hash(request, AppJson.Default.TrashRequest);
            if (store.Existing(uid, request.IdempotencyKey, hash) is { } existing) return existing;
            var roots = new HashSet<string>();
            foreach (var item in request.Items)
            {
                store.Get(item.TaskId, uid); using var db = store.Db(item.TaskId); var op = db.Operation(item.OperationId);
                if (op.State != "trashed") throw new AppError("选择的文件已不在回收站"); roots.Add(op.Target.Root);
            }
            await access.CheckRoots(uid, roots.ToArray(), ct);
            var job = new Job { Id = TaskStore.Id(), Owner = uid, Kind = request.Kind, Method = request.Kind, Roots = roots.ToArray() };
            store.Create(job, request.IdempotencyKey, hash, db =>
            {
                foreach (var item in request.Items)
                {
                    using var sourceDb = store.Db(item.TaskId); var source = sourceDb.Operation(item.OperationId);
                    db.Save(new Operation { Id = TaskStore.Id(), Method = request.Kind, Target = source.Target, SourceTaskId = item.TaskId, SourceOperationId = item.OperationId });
                }
                TaskStore.Sql(db.Connection, "CREATE TABLE IF NOT EXISTS prepared(id INTEGER PRIMARY KEY)");
                TaskStore.Sql(db.Connection, "INSERT INTO prepared VALUES(1)");
            });
            Enqueue(job.Id); return job;
        }
        finally { Gate.Release(); }
    }
    public async Task<Job> Control(uint uid, string id, bool resume, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var job = store.Get(id, uid); using var db = store.Db(id);
            if (resume)
            {
                if (job.State != "interrupted") throw new AppError("只有中断的任务可以继续", 409);
                await access.CheckRoots(uid, job.Roots, ct);
                job.State = "queued"; job.Error = null; job.Stage = "等待继续"; db.Save(job); Enqueue(id);
            }
            else
            {
                if (job.State is not ("queued" or "running" or "cancelling")) throw new AppError("任务已停止", 409);
                if (running.TryGetValue(id, out var source)) { source.Cancel(); job.State = "cancelling"; }
                else job.State = "cancelled";
                job.Stage = "正在安全停止"; db.Save(job);
            }
            return job;
        }
        finally { Gate.Release(); }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in queue.Reader.ReadAllAsync(stoppingToken))
        {
            Job job; CancellationTokenSource cancellation;
            await Gate.WaitAsync(stoppingToken);
            try
            {
                job = store.Get(id); if (job.State != "queued") continue;
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); running[id] = cancellation;
                using var task = store.Db(id); job.State = "running"; task.Save(job);
            }
            finally { Gate.Release(); }
            using var db = store.Db(id);
            try
            {
                var ct = cancellation.Token;
                if (job.Kind == "scan") await scanner.Run(job, db, ct);
                else
                {
                    TaskStore.Sql(db.Connection, "CREATE TABLE IF NOT EXISTS prepared(id INTEGER PRIMARY KEY)");
                    if (db.Count("prepared") == 0)
                    {
                        job.Stage = "准备清理计划"; db.Save(job);
                        var request = JsonSerializer.Deserialize(Request(db), AppJson.Default.CleanupRequest)!;
                        var scan = RequireScan(job.Owner, request.ScanId);
                        await foreach (var op in cleaner.Plan(scan, request, ct))
                        {
                            // Stable file IDs make interrupted plan construction idempotent.
                            if (db.Count("operations", "id=$id", ("$id", op.Id)) == 0) db.Save(op);
                        }
                        TaskStore.Sql(db.Connection, "INSERT INTO prepared VALUES(1)");
                    }
                    job.Total = db.Count("operations"); db.Save(job); await cleaner.Run(job, db, ct);
                }
                job.State = job.Errors > 0 ? "completedWithErrors" : "completed"; job.Stage = job.Errors > 0 ? "已完成，部分文件需要检查" : "已完成";
            }
            catch (OperationCanceledException)
            { job.State = stoppingToken.IsCancellationRequested ? "interrupted" : "cancelled"; job.Stage = stoppingToken.IsCancellationRequested ? "已中断，等待手动继续" : "已取消，已完成操作保留"; }
            catch (Exception e)
            {
                job.State = "interrupted"; job.Stage = "已停止，修复问题后可手动继续";
                job.Error = e is AppError or LinuxException ? e.Message : "任务遇到存储或运行错误，请检查应用日志和磁盘空间";
            }
            finally
            {
                try { TaskStore.Sql(db.Connection, "PRAGMA synchronous=FULL"); if (job.Kind != "scan") Cleaner.Recount(job, db); db.Save(job); }
                finally
                {
                    await Gate.WaitAsync(CancellationToken.None);
                    try { running.Remove(id); cancellation.Dispose(); } finally { Gate.Release(); }
                }
            }
        }
    }
    static void SaveRequest(TaskDb db, string json)
    {
        TaskStore.Sql(db.Connection, "CREATE TABLE request(id INTEGER PRIMARY KEY,json TEXT NOT NULL)");
        TaskStore.Sql(db.Connection, "INSERT INTO request VALUES(1,$json)", ("$json", json));
    }
    static string Request(TaskDb db)
    { using var c = TaskStore.Command(db.Connection, "SELECT json FROM request WHERE id=1"); return (string)c.ExecuteScalar()!; }
}
