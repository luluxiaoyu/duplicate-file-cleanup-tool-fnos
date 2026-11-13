using DuplicateCleanup;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;

var paths = AppPaths.FromEnvironment();
if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("本应用服务仅运行于 Linux / fnOS；通用测试请运行 tests 项目。");
if (LinuxRuntime.IsRoot()) throw new InvalidOperationException("请以飞牛应用包用户运行");
Directory.CreateDirectory(paths.Data);
File.SetUnixFileMode(paths.Data, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
using var instance = new FileStream(Path.Combine(paths.Data, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
var socket = Path.Combine(paths.Target, "app.sock");
if (File.Exists(socket)) File.Delete(socket);
var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders(); builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(o => { o.ListenUnixSocket(socket); o.Limits.MaxRequestBodySize = 1024 * 1024; });
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJson.Default));
builder.Services.AddSingleton(paths); builder.Services.AddSingleton<IAuthority, FnOs>();
builder.Services.AddSingleton<AccessGuard>(); builder.Services.AddSingleton<TaskStore>();
builder.Services.AddSingleton<Scanner>(); builder.Services.AddSingleton<Cleaner>();
builder.Services.AddSingleton<TaskRunner>(); builder.Services.AddHostedService(s => s.GetRequiredService<TaskRunner>());
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(25));
var app = builder.Build(); var store = app.Services.GetRequiredService<TaskStore>(); store.Recover();

app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'";
    try
    {
        if (!context.Request.Path.StartsWithSegments(AppInfo.Prefix)) { context.Response.StatusCode = 404; return; }
        if (!uint.TryParse(context.Request.Headers["X-Trim-Userid"], out var uid)) throw new AppError("请从飞牛登录后打开应用", 401);
        context.Items["user"] = new GatewayUser(uid, context.Request.Headers["X-Trim-Username"].ToString());
        if (context.Request.Path.StartsWithSegments(AppInfo.Prefix + "/api"))
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Method is not ("GET" or "HEAD"))
            {
                if (context.Request.Headers["X-Requested-With"] != AppInfo.Name) throw new AppError("请求来源无效", 403);
                if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site") throw new AppError("拒绝跨站请求", 403);
                if (context.Request.Headers.Origin is { Count: > 0 } origins &&
                    (!Uri.TryCreate(origins[0], UriKind.Absolute, out var origin) || origin.Authority != context.Request.Host.Value))
                    throw new AppError("请求来源无效", 403);
            }
        }
        await next(context);
    }
    catch (Exception e) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = e is AppError error ? error.Status : e is JsonException or BadHttpRequestException ? 400 : 500;
        await context.Response.WriteAsJsonAsync(new ErrorResponse(e is AppError ? e.Message : "请求失败，请检查应用状态后重试"), AppJson.Default.ErrorResponse);
    }
});
static GatewayUser User(HttpContext c) => (GatewayUser)c.Items["user"]!;
static string Language(HttpContext c) => c.Request.Headers["X-App-Language"].ToString() is { Length: > 0 and < 40 } value ? value : "zh-CN";

var api = app.MapGroup(AppInfo.Prefix + "/api");
api.MapGet("/me", (HttpContext c) => User(c));
api.MapGet("/health", () => new HealthResponse("ok", "0.1.0"));
api.MapGet("/folders", async (HttpContext c, IAuthority authority, AccessGuard guard, CancellationToken ct) =>
{
    var roots = await authority.Roots(User(c).Uid, ct); var result = new List<FolderInfo>();
    foreach (var path in roots)
    {
        try
        {
            await guard.Require(User(c).Uid, path, path, "read", ct);
            using var root = new SecureRoot(path);
            var labels = await authority.Labels([path], Language(c), ct);
            result.Add(new(path, labels.GetValueOrDefault(path, "已授权目录"), LinuxFiles.FileSystem(root.Handle), null));
        }
        catch (Exception e) when (Scanner.Recoverable(e)) { result.Add(new(path, "不可访问的授权目录", null, Cleaner.SafeError(e))); }
    }
    return result.ToArray();
});
api.MapPost("/folders/revoke", async (PathRequest r, HttpContext c, IAuthority authority, CancellationToken ct) =>
{
    var roots = await authority.Roots(User(c).Uid, ct); var path = Paths.Normalize(r.Path);
    if (!roots.Contains(path)) throw new AppError("目录不在当前用户授权中", 403);
    await authority.Revoke(User(c).Uid, path, ct); return new HealthResponse("ok", "0.1.0");
});
api.MapPost("/scans", async (ScanRequest r, HttpContext c, TaskRunner runner, CancellationToken ct) => await runner.Scan(User(c).Uid, r, ct));
api.MapPost("/cleanups/preview", async (CleanupRequest r, HttpContext c, TaskRunner runner, Cleaner cleaner, CancellationToken ct) => await cleaner.Preview(runner.RequireScan(User(c).Uid, r.ScanId), r, ct));
api.MapPost("/cleanups", async (CleanupRequest r, HttpContext c, TaskRunner runner, CancellationToken ct) => await runner.Cleanup(User(c).Uid, r, ct));
api.MapGet("/scans/{id}/draft", async (string id, HttpContext c, TaskRunner runner, AccessGuard guard, CancellationToken ct) =>
{
    var scan = runner.RequireScan(User(c).Uid, id); await guard.CheckRoots(scan.Owner, scan.Roots, ct);
    using var db = store.Db(id); TaskStore.Sql(db.Connection, "CREATE TABLE IF NOT EXISTS draft(id INTEGER PRIMARY KEY,json TEXT NOT NULL)");
    using var cmd = TaskStore.Command(db.Connection, "SELECT json FROM draft WHERE id=1");
    var json = cmd.ExecuteScalar() as string;
    return Results.Json(json is null ? null : JsonSerializer.Deserialize(json, AppJson.Default.CleanupRequest), AppJson.Default.CleanupRequest);
});
api.MapPost("/scans/{id}/draft", async (string id, CleanupRequest r, HttpContext c, TaskRunner runner, AccessGuard guard, CancellationToken ct) =>
{
    var scan = runner.RequireScan(User(c).Uid, id); await guard.CheckRoots(scan.Owner, scan.Roots, ct);
    if (r.ScanId != id || r.Choices.Length > 1000) throw new AppError("选择记录无效");
    using var db = store.Db(id); TaskStore.Sql(db.Connection, "CREATE TABLE IF NOT EXISTS draft(id INTEGER PRIMARY KEY,json TEXT NOT NULL)");
    TaskStore.Sql(db.Connection, "INSERT INTO draft VALUES(1,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json", ("$json", JsonSerializer.Serialize(r, AppJson.Default.CleanupRequest)));
    return new HealthResponse("ok", "0.1.0");
});
api.MapPost("/trash/tasks", async (TrashRequest r, HttpContext c, TaskRunner runner, CancellationToken ct) => await runner.Trash(User(c).Uid, r, ct));
api.MapGet("/tasks", async (HttpContext c, AccessGuard guard, CancellationToken ct) =>
{
    var offset = Offset(c); var list = new List<Job>();
    foreach (var id in store.Ids(User(c).Uid, offset, 20)) list.Add(await PublicJob(store.Get(id, User(c).Uid), c, guard, ct));
    return new TaskPage(list.ToArray(), offset + list.Count, store.Ids(User(c).Uid, offset + list.Count, 1).Any());
});
api.MapGet("/tasks/{id}", async (string id, HttpContext c, AccessGuard guard, CancellationToken ct) => await PublicJob(store.Get(id, User(c).Uid), c, guard, ct));
api.MapGet("/tasks/{id}/issues", async (string id, HttpContext c, AccessGuard guard, IAuthority authority, CancellationToken ct) =>
{
    var job = store.Get(id, User(c).Uid); await guard.CheckRoots(job.Owner, job.Roots, ct);
    using var db = store.Db(id); var issues = new List<TaskIssue>(); var after = c.Request.Query["after"].ToString();
    if (job.Kind == "scan")
    {
        using var cmd = TaskStore.Command(db.Connection, "SELECT id,message FROM errors ORDER BY id LIMIT 50");
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) issues.Add(new(reader.GetInt64(0).ToString(), "扫描跳过的文件", reader.GetString(1), null));
    }
    else
    {
        foreach (var op in db.Operations("state IN ('error','attention')", after, 50).ToArray())
        {
            await guard.Require(job.Owner, op.Target.Root, Path.GetDirectoryName(op.Target.Path)!, "read", ct);
            var label = (await authority.Labels([op.Target.Root], Language(c), ct)).GetValueOrDefault(op.Target.Root, "已授权目录");
            var held = op.Method == "hardlink" ? op.LinkRelative : op.HoldingRelative;
            string? recovery = null;
            if (held is not null)
            {
                try { await guard.Require(job.Owner, op.Target.Root, Path.Combine(op.Target.Root, held), "read", ct); recovery = label + "/" + held; }
                catch (AppError e) when (e.Status == 403) { }
            }
            issues.Add(new(op.Id, label + "/" + op.Target.Relative, op.Error ?? "文件已保留，需要检查", recovery));
        }
    }
    return new IssuesPage(issues.ToArray(), issues.LastOrDefault()?.Id ?? after);
});
api.MapPost("/tasks/{id}/cancel", async (string id, HttpContext c, TaskRunner runner, CancellationToken ct) => await runner.Control(User(c).Uid, id, false, ct));
api.MapPost("/tasks/{id}/resume", async (string id, HttpContext c, TaskRunner runner, CancellationToken ct) => await runner.Control(User(c).Uid, id, true, ct));
api.MapGet("/scans/{id}/groups", async (string id, HttpContext c, TaskRunner runner, AccessGuard guard, IAuthority authority, CancellationToken ct) =>
{
    var scan = runner.RequireScan(User(c).Uid, id); await guard.CheckRoots(scan.Owner, scan.Roots, ct);
    using var db = store.Db(id); var groups = new List<DuplicateGroup>(); var after = Offset(c);
    foreach (var g in db.Groups(after, 20).ToArray())
    {
        var files = db.Files("group_id=$g AND id>$after LIMIT 20", [("$g", g.Id), ("$after", 0)]).ToArray();
        var views = await Views(files, scan.Owner, g.Keeper, db, guard, authority, Language(c), ct);
        var count = db.Count("files", "group_id=$g", ("$g", g.Id));
        groups.Add(new(g.Id, g.Keeper, count, files.FirstOrDefault()?.Stamp.Size ?? 0, views, files.LastOrDefault()?.Id ?? 0, count > files.Length));
    }
    var next = groups.LastOrDefault()?.Id ?? after;
    return new GroupsPage(groups.ToArray(), next, db.Groups(next, 1).Any());
});
api.MapGet("/scans/{id}/groups/{group:long}/files", async (string id, long group, HttpContext c, TaskRunner runner, AccessGuard guard, IAuthority authority, CancellationToken ct) =>
{
    var scan = runner.RequireScan(User(c).Uid, id); using var db = store.Db(id);
    var g = db.Groups(group - 1, 1).FirstOrDefault(); if (g.Id != group) throw new AppError("重复组不存在", 404);
    var rows = db.Files("group_id=$group AND id>$after LIMIT 50", [("$group", group), ("$after", Offset(c))]).ToArray();
    var next = rows.LastOrDefault()?.Id ?? Offset(c);
    return new FilePage(await Views(rows, scan.Owner, g.Keeper, db, guard, authority, Language(c), ct), next,
        db.Files("group_id=$group AND id>$after LIMIT 1", [("$group", group), ("$after", next)]).Any());
});
api.MapGet("/trash", async (HttpContext c, AccessGuard guard, IAuthority authority, CancellationToken ct) =>
{
    // Each page advances over operations in the catalog, including already restored/purged rows.
    var offset = Offset(c); long visited = 0; var list = new List<TrashView>(); var stop = false;
    for (long page = 0; !stop; page += 50)
    {
        var ids = store.Ids(User(c).Uid, page).ToArray(); if (ids.Length == 0) break;
        foreach (var id in ids)
        {
            using var db = store.Db(id); string after = "";
            while (!stop)
            {
                var rows = db.Operations("state='trashed'", after).ToArray(); if (rows.Length == 0) break;
                foreach (var op in rows)
                {
                    after = op.Id; if (visited++ < offset) continue;
                    var held = Path.Combine(op.Target.Root, op.HoldingRelative!);
                    try
                    {
                        await guard.Require(User(c).Uid, op.Target.Root, held, "read", ct);
                        var labels = await authority.Labels([op.Target.Root], Language(c), ct);
                        list.Add(new(id, op.Id, labels.GetValueOrDefault(op.Target.Root, "已授权目录") + "/" + op.Target.Relative, op.Target.Stamp.Size, op.State, null));
                    }
                    catch (AppError e) when (e.Status == 403) { }
                    if (visited - offset >= 20) { stop = true; break; }
                }
            }
            if (stop) break;
        }
    }
    return new TrashPage(list.ToArray(), Math.Max(offset, visited));
});

static long Offset(HttpContext c) => long.TryParse(c.Request.Query["after"], out var value) && value >= 0 ? value : 0;
static async Task<Job> PublicJob(Job job, HttpContext c, AccessGuard guard, CancellationToken ct)
{
    // Do not leak absolute paths through task metadata. Historical counters remain visible to the
    // owner, but file details and new operations always require current authorization/ACL.
    foreach (var s in job.Space) s.Root = "";
    job.Roots = []; job.PriorityRoots = [];
    await Task.CompletedTask; return job;
}
static async Task<FileView[]> Views(FileEntry[] rows, uint uid, long keeperId, TaskDb db, AccessGuard guard, IAuthority authority, string language, CancellationToken ct)
{
    var output = new List<FileView>(); var keeper = db.File(keeperId);
    using var keeperRoot = new SecureRoot(keeper.Root);
    var keeperExists = keeperRoot.TryStamp(keeper.Relative) is not null;
    await guard.Require(uid, keeper.Root, keeperExists ? keeper.Path : Path.GetDirectoryName(keeper.Path)!, "read", ct);
    using var keeperHandle = keeperExists ? keeperRoot.Open(keeper.Relative) : null;
    foreach (var row in rows)
    {
        using var root = new SecureRoot(row.Root);
        var stamp = root.TryStamp(row.Relative);
        await guard.Require(uid, row.Root, stamp is null ? Path.GetDirectoryName(row.Path)! : row.Path, "read", ct);
        var labels = await authority.Labels([row.Root], language, ct);
        var displayPath = labels.GetValueOrDefault(row.Root, "已授权目录") + "/" + row.Relative;
        if (stamp is null || keeperHandle is null || !stamp.SameContentVersion(row.Stamp))
        {
            output.Add(new(row.Id, displayPath, row.Stamp.Size, stamp?.Links ?? row.Stamp.Links,
                stamp is null ? "missing" : "changed", 0, null, LinuxFiles.FileSystem(root.Handle), "路径已清理、移动或文件已变化；此处保留扫描时的记录，请重新扫描后清理"));
            continue;
        }
        using var f = root.Open(row.Relative);
        var sharing = row.Id == keeperId ? new SharingInfo("reference", 0, null, true, null) :
            Sharing.Compare(LinuxFiles.Stamp(f), LinuxFiles.Stamp(keeperHandle), LinuxFiles.FileSystem(f), LinuxFiles.FileSystem(keeperHandle), LinuxFiles.Map(f), LinuxFiles.Map(keeperHandle));
        output.Add(new(row.Id, displayPath, row.Stamp.Size, row.Stamp.Links, sharing.State, sharing.SharedBytes, sharing.ExclusiveBytes, LinuxFiles.FileSystem(f), sharing.Reason));
    }
    return output.ToArray();
}

var www = Path.Combine(paths.Target, "www");
if (Directory.Exists(www))
{
    app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(www), RequestPath = AppInfo.Prefix });
    app.MapFallback(AppInfo.Prefix + "/{*path:nonfile}", async c =>
    { c.Response.ContentType = "text/html; charset=utf-8"; await c.Response.SendFileAsync(Path.Combine(www, "index.html")); });
}
app.Lifetime.ApplicationStarted.Register(() =>
{
    if (OperatingSystem.IsLinux()) File.SetUnixFileMode(socket, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
    File.WriteAllText(Path.Combine(paths.Data, "server.ready"), Environment.ProcessId.ToString());
});
app.Lifetime.ApplicationStopped.Register(() => { File.Delete(Path.Combine(paths.Data, "server.ready")); File.Delete(socket); });
await app.RunAsync();
