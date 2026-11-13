using DuplicateCleanup;
using System.Text.Json;
using System.Security.Cryptography;

var failures = 0; var passed = 0;
async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failures++; Console.Error.WriteLine($"FAIL {name}: {e}"); }
}
void Unit(string name, Action action) => Test(name, () => { action(); return Task.CompletedTask; }).GetAwaiter().GetResult();
void Assert(bool condition, string reason = "assertion failed") { if (!condition) throw new Exception(reason); }
void Throws(Action action, string reason)
{ try { action(); } catch (AppError) { return; } throw new Exception(reason); }
var fs = new DuplicateCleanup.FileSystemInfo("Btrfs", "btrfs:one", 1, 5, false, true, true, null);
FileStamp Stamp(ulong inode = 1, uint links = 1) => new(1, inode, 0x81a4, 1000, 1000, links, 8192, 16, 1, 0, 1, 0, 1);
ExtentMap Map(params Extent[] extents) => new(true, extents, null);
Unit("Reject traversal, NUL, relative and filesystem root", () =>
{
    foreach (var path in new[] { "/", "relative", "/safe/../etc", "/safe/./a", "/safe/a\0b" }) Throws(() => Paths.Normalize(path), path);
    foreach (var p in new[] { "../a", "/a", "a//b", "a/./b" }) Throws(() => Paths.Parts(p), p);
    Assert(!Paths.Within("/safe", "/safe-neighbor/a")); Assert(Paths.Within("/safe", "/safe/a"));
});
Unit("Existing hardlinks have zero extra data cost", () =>
{
    var result = Sharing.Compare(Stamp(), Stamp(), fs, fs, new(false, [], "unsupported"), new(false, [], "unsupported"));
    Assert(result.State == "hardlink" && result.ExclusiveBytes == 0 && result.Complete);
});
Unit("Shared flag alone does not prove sharing with keeper", () =>
{
    var result = Sharing.Compare(Stamp(), Stamp(2), fs, fs, Map(new Extent(0, 10000, 8192, 0x2001)), Map(new Extent(0, 30000, 8192, 0x2001)));
    Assert(result.State == "independent" && !result.Complete);
    Assert(Sharing.ExclusiveEstimate(Stamp(), Map(new Extent(0, 10000, 8192, 0x2001))) is null);
});
Unit("Full and partial shared extents are distinguished", () =>
{
    var full = Sharing.Compare(Stamp(), Stamp(2), fs, fs, Map(new Extent(0, 10000, 8192, 0x2001)), Map(new Extent(0, 10000, 4096, 0x2000), new Extent(4096, 14096, 4096, 0x2001)));
    Assert(full.State == "fullyShared" && full.SharedBytes == 8192);
    var part = Sharing.Compare(Stamp(), Stamp(2), fs, fs, Map(new Extent(0, 10000, 8192, 0x2001)), Map(new Extent(0, 10000, 4096, 0x2000), new Extent(4096, 40000, 4096, 0x2001)));
    Assert(part.State == "partlyShared" && part.SharedBytes == 4096);
    Assert(!Sharing.RangeShared(Map(new Extent(0, 10000, 8192, 1)), Map(new Extent(0, 40000, 8192, 1)), 0, 8192));
});
Unit("Cross-filesystem physical numbers are never equated", () =>
{
    var result = Sharing.Compare(Stamp(), Stamp(2), fs, fs with { Identity = "btrfs:two" }, Map(new Extent(0, 10000, 8192, 1)), Map(new Extent(0, 10000, 8192, 1)));
    Assert(result.State == "independent" && result.SharedBytes == 0);
});
Unit("Unknown maps and extra hardlink references cannot claim savings", () =>
{
    Assert(Sharing.ExclusiveEstimate(Stamp(), new(false, [], "compressed")) is null);
    Assert(Sharing.ExclusiveEstimate(Stamp(links: 2), Map(new Extent(0, 10000, 8192, 1))) is null);
    Assert(Sharing.ExclusiveEstimate(Stamp(), Map(new Extent(0, 10000, 8192, 1))) == 8192);
    Assert(Sharing.ExclusiveEstimate(Stamp(), Map()) == 0);
});
Unit("JSON source generation includes nested space measurements", () =>
{
    var job = new Job { Id = TaskStore.Id(), Owner = 1000, Kind = "cleanup", Space = [new() { FileSystemId = "fs", Root = "/files", BeforeAvailable = 1000, AfterAvailable = 500, BeforeAt = DateTimeOffset.UtcNow }] };
    var json = JsonSerializer.Serialize(job, AppJson.Default.Job); var copy = JsonSerializer.Deserialize(json, AppJson.Default.Job)!;
    Assert(copy.Space[0].AfterAvailable - copy.Space[0].BeforeAvailable == -500);
    Assert(json.Contains("estimatedSavedBytes") && !JsonSerializer.IsReflectionEnabledByDefault);
});
var temporary = Path.Combine(Path.GetTempPath(), "duplicate-tests-" + TaskStore.Id()); Directory.CreateDirectory(temporary);
try
{
    Unit("SQLite task isolation, idempotency and crash recovery", () =>
    {
        var store = new TaskStore(new(Path.Combine(temporary, "state"), Path.Combine(temporary, "target")));
        var job = new Job { Id = TaskStore.Id(), Owner = 1000, Kind = "scan", State = "running" };
        store.Create(job, "test-request-1", "hash"); Assert(store.Existing(1000, "test-request-1", "hash")?.Id == job.Id);
        Throws(() => store.Get(job.Id, 1001), "Cross-user task read allowed");
        Throws(() => store.Existing(1000, "test-request-1", "different"), "Idempotency conflict allowed");
        Assert(!store.Ids(1001).Any()); store.Recover(); Assert(store.Get(job.Id).State == "interrupted");
        using var db = store.Db(job.Id); db.ResetEnumeration();
        Assert(db.Upsert("/files", "a", 0, Stamp())); Assert(!db.Upsert("/files", "a", 1, Stamp()));
        Assert(db.Upsert("/files", "b", 0, Stamp(2))); Assert(db.Files("id>0 LIMIT 1").Count() == 1);
        var a = db.Files().First(); db.Save(new Operation { Id = "op", Target = a, Method = "trash", State = "trashed", EstimatedSaved = 0, EstimateComplete = true });
        Cleaner.Recount(job, db); Assert(job.TrashBytes == 8192 && job.EstimatedSavedBytes == 0);
        db.Save(new Operation { Id = "partial", Target = a, Method = "dedupe", State = "attention", MergedBytes = 4096, EstimatedSaved = 8192 });
        Cleaner.Recount(job, db); Assert(job.EstimatedSavedBytes == 0 && job.MergedBytes == 4096 && !job.EstimateComplete);
    });
    await Test("Authorization revoke and ACL failures fail closed", async () =>
    {
        var root = "/vol1/authorization-test";
        var authority = new TestAuthority(root); var guard = new AccessGuard(authority, new(Path.Combine(temporary, "private"), Path.Combine(temporary, "target")));
        await guard.Require(1000, root, root + "/a", "read", default);
        authority.Allow = false; var denied = false;
        try { await guard.Require(1000, root, root + "/a", "read", default); } catch (AppError e) { denied = e.Status == 403; }
        Assert(denied); authority.Allow = true; authority.Granted = false; denied = false;
        try { await guard.Require(1000, root, root + "/a", "read", default); } catch (AppError e) { denied = e.Status == 403; }
        Assert(denied);
    });
    if (OperatingSystem.IsLinux())
    {
        await Test("Native scan, mutation detection, hardlink, trash, no-overwrite restore and delete", async () =>
        {
            if (LinuxRuntime.IsRoot()) throw new Exception("Integration tests MUST run as an unprivileged user");
            var basePath = Environment.GetEnvironmentVariable("DUPLICATE_TEST_ROOT") ?? temporary;
            var directory = Path.Combine(basePath, "native-" + TaskStore.Id()); Directory.CreateDirectory(directory);
            try
            {
                var data = new byte[1024 * 1024 + 123]; RandomNumberGenerator.Fill(data);
                File.WriteAllBytes(Path.Combine(directory, "a"), data); File.WriteAllBytes(Path.Combine(directory, "b"), data);
                File.WriteAllBytes(Path.Combine(directory, ".隐藏"), data); File.WriteAllBytes(Path.Combine(directory, "different"), new byte[data.Length]);
                Directory.CreateSymbolicLink(Path.Combine(directory, "escape"), "/etc");
                using (var root = new SecureRoot(directory)) root.Link("a", root, "existing-link");
                var authority = new TestAuthority(directory); var paths = new AppPaths(Path.Combine(temporary, "native-state"), Path.Combine(temporary, "native-target"));
                var guard = new AccessGuard(authority, paths); var scanner = new Scanner(guard); var store = new TaskStore(paths); var cleaner = new Cleaner(guard, scanner, store);
                async Task<Job> Scan()
                {
                    var j = new Job { Id = TaskStore.Id(), Owner = LinuxRuntime.Uid(), Kind = "scan", Roots = [directory] };
                    store.Create(j, j.Id, j.Id); using var db = store.Db(j.Id); await scanner.Run(j, db, default); j.State = "completed"; db.Save(j); return j;
                }
                var scan = await Scan(); Assert(scan.Discovered == 5, $"Discovered {scan.Discovered}");
                using (var db = store.Db(scan.Id)) { Assert(db.Groups().Count() == 1); Assert(db.Count("files", "group_id IS NOT NULL") == 4); }
                async Task<Job> Clean(string method, Job input)
                {
                    var request = new CleanupRequest(input.Id, method, true, [], [], true, TaskStore.Id());
                    var j = new Job { Id = TaskStore.Id(), Owner = LinuxRuntime.Uid(), Kind = "cleanup", Roots = [directory], Method = method };
                    store.Create(j, j.Id, j.Id); using var db = store.Db(j.Id);
                    await foreach (var op in cleaner.Plan(input, request, default)) db.Save(op);
                    await cleaner.Run(j, db, default); return j;
                }
                var rootFs = new SecureRoot(directory); using (rootFs)
                {
                    var kind = LinuxFiles.FileSystem(rootFs.Handle);
                    if (kind.Btrfs)
                    {
                        var dedupe = await Clean("dedupe", scan); Assert(dedupe.Errors == 0, "Btrfs dedupe failed");
                        using var r = new SecureRoot(directory); using var a = r.Open("a"); using var b = r.Open("b");
                        Assert(Sharing.Compare(LinuxFiles.Stamp(b), LinuxFiles.Stamp(a), kind, kind, LinuxFiles.Map(b), LinuxFiles.Map(a)).State == "fullyShared");
                        File.WriteAllText(Path.Combine(directory, "b"), "changed independently"); Assert(File.ReadAllBytes(Path.Combine(directory, "a")).SequenceEqual(data));
                        foreach (var name in new[] { "a", "b", ".隐藏", "existing-link" }) File.Delete(Path.Combine(directory, name));
                        foreach (var name in new[] { "a", "b", ".隐藏" }) File.WriteAllBytes(Path.Combine(directory, name), data);
                        using (var rebuild = new SecureRoot(directory)) rebuild.Link("a", rebuild, "existing-link");
                        scan = await Scan();
                        // Simulate an app-data checkpoint ahead of the filesystem's persisted
                        // extent state. Resume must revisit the prefix without double-counting.
                        using var resumedSource = store.Db(scan.Id);
                        var resumed = new Job { Id = TaskStore.Id(), Owner = LinuxRuntime.Uid(), Kind = "cleanup", Method = "dedupe", Roots = [directory] };
                        store.Create(resumed, resumed.Id, resumed.Id); using var resumedDb = store.Db(resumed.Id);
                        resumedDb.Save(new Operation { Id = "resume", Method = "dedupe", State = "merging", Offset = 65536, MergedBytes = 65536,
                            Keeper = resumedSource.Files().Single(f => f.Relative == "a"), Target = resumedSource.Files().Single(f => f.Relative == "b") });
                        await cleaner.Run(resumed, resumedDb, default);
                        Assert(resumed.Errors == 0 && resumed.MergedBytes == data.Length, "Resume skipped or double-counted a range");
                        using var checkRoot = new SecureRoot(directory); using var ca = checkRoot.Open("a"); using var cb = checkRoot.Open("b");
                        Assert(Sharing.Compare(LinuxFiles.Stamp(cb), LinuxFiles.Stamp(ca), kind, kind, LinuxFiles.Map(cb), LinuxFiles.Map(ca)).State == "fullyShared");
                    }
                }
                var hardlink = await Clean("hardlink", scan); Assert(hardlink.Errors == 0, "Hardlink operations failed");
                using (var root = new SecureRoot(directory)) using (var a = root.Open("a")) using (var b = root.Open("b")) Assert(LinuxFiles.Stamp(a).SameInode(LinuxFiles.Stamp(b)));
                // Independent copies for trash/delete, not aliases to the keeper.
                File.WriteAllBytes(Path.Combine(directory, "copy"), data); scan = await Scan();
                var trash = await Clean("trash", scan); Assert(trash.Errors == 0 && trash.EstimatedSavedBytes == 0 && trash.TrashBytes > 0);
                using var trashDb = store.Db(trash.Id); var saved = trashDb.Operations("state='trashed'").First();
                var restore = new Job { Id = TaskStore.Id(), Owner = LinuxRuntime.Uid(), Kind = "restore", Roots = [directory] };
                store.Create(restore, restore.Id, restore.Id); using var restoreDb = store.Db(restore.Id);
                restoreDb.Save(new Operation { Id = "restore", Target = saved.Target, Method = "restore", SourceTaskId = trash.Id, SourceOperationId = saved.Id });
                File.WriteAllText(saved.Target.Path, "collision"); await cleaner.Run(restore, restoreDb, default);
                Assert(File.ReadAllText(saved.Target.Path) == "collision" && restore.Errors == 1, "Restore overwrote an existing file");
                File.Delete(saved.Target.Path);
                // Retry a new restore task after the collision, preserving the failed task history.
                var retry = new Job { Id = TaskStore.Id(), Owner = LinuxRuntime.Uid(), Kind = "restore", Roots = [directory] }; store.Create(retry, retry.Id, retry.Id);
                using var retryDb = store.Db(retry.Id); retryDb.Save(new Operation { Id = "restore", Target = saved.Target, Method = "restore", SourceTaskId = trash.Id, SourceOperationId = saved.Id });
                await cleaner.Run(retry, retryDb, default); Assert(retry.Errors == 0 && File.ReadAllBytes(saved.Target.Path).SequenceEqual(data));
                File.WriteAllBytes(Path.Combine(directory, "final-copy"), data); scan = await Scan(); var deleted = await Clean("delete", scan); Assert(deleted.Errors == 0);
            }
            finally { Directory.Delete(directory, true); }
        });
        await Test("Native journal recovery at move, unlink and hardlink exchange boundaries", async () =>
        {
            if (LinuxRuntime.IsRoot()) throw new Exception("Integration tests MUST run unprivileged");
            var basePath = Environment.GetEnvironmentVariable("DUPLICATE_TEST_ROOT") ?? temporary;
            foreach (var phase in new[] { "prepared", "afterMove", "staged", "deleting", "afterDelete", "linked", "swapped", "afterOldDelete", "afterTrashMove" })
            {
                var directory = Path.Combine(basePath, "recovery-" + TaskStore.Id()); Directory.CreateDirectory(directory);
                try
                {
                    var data = RandomNumberGenerator.GetBytes(65536);
                    File.WriteAllBytes(Path.Combine(directory, "a"), data); File.WriteAllBytes(Path.Combine(directory, "b"), data);
                    var paths = new AppPaths(Path.Combine(temporary, "recovery-state-" + phase), Path.Combine(temporary, "target"));
                    var authority = new TestAuthority(directory); var guard = new AccessGuard(authority, paths);
                    var scanner = new Scanner(guard); var store = new TaskStore(paths); var cleaner = new Cleaner(guard, scanner, store);
                    var scan = new Job { Id = TaskStore.Id(), Owner = LinuxRuntime.Uid(), Kind = "scan", Roots = [directory] };
                    store.Create(scan, scan.Id, scan.Id); using var scanDb = store.Db(scan.Id); await scanner.Run(scan, scanDb, default);
                    var target = scanDb.Files().Single(f => f.Relative == "b"); var keeper = scanDb.Files().Single(f => f.Relative == "a");
                    var method = phase is "linked" or "swapped" or "afterOldDelete" ? "hardlink" : phase == "afterTrashMove" ? "trash" : "delete";
                    var job = new Job { Id = TaskStore.Id(), Owner = LinuxRuntime.Uid(), Kind = "cleanup", Roots = [directory], Method = method };
                    store.Create(job, job.Id, job.Id); using var db = store.Db(job.Id); using var root = new SecureRoot(directory);
                    var holding = (method == "trash" ? AppInfo.Trash : AppInfo.Staging) + $"/{job.Owner}/{job.Id}";
                    root.EnsurePrivateDirectory(holding, job.Owner);
                    var op = new Operation { Id = "op", Target = target, Keeper = keeper, Method = method, HoldingRelative = holding + "/op.data", LinkRelative = holding + "/op.link", State = phase switch { "afterMove" or "afterTrashMove" => "prepared", "afterDelete" => "deleting", "afterOldDelete" => "deletingOld", _ => phase }, EstimateComplete = false };
                    if (phase is "afterMove" or "staged" or "deleting" or "afterDelete" or "afterTrashMove") root.Rename("b", root, op.HoldingRelative);
                    if (phase == "afterDelete") { root.Unlink(op.HoldingRelative); File.WriteAllText(Path.Combine(directory, "b"), "new unrelated file"); }
                    if (method == "hardlink")
                    {
                        root.Link("a", root, op.LinkRelative);
                        if (phase != "linked") root.Rename("b", root, op.LinkRelative, exchange: true);
                        if (phase == "afterOldDelete") root.Unlink(op.LinkRelative);
                    }
                    db.Save(op); await cleaner.Run(job, db, default);
                    Assert(job.Errors == 0, $"Recovery failed at {phase}");
                    Assert(File.ReadAllBytes(Path.Combine(directory, "a")).SequenceEqual(data), $"Keeper changed at {phase}");
                    if (method == "hardlink")
                    { using var a = root.Open("a"); using var b = root.Open("b"); Assert(LinuxFiles.Stamp(a).SameInode(LinuxFiles.Stamp(b)), phase); }
                    else if (phase == "afterDelete") Assert(File.ReadAllText(Path.Combine(directory, "b")) == "new unrelated file", "Recovery deleted a replacement file");
                    else Assert(!File.Exists(Path.Combine(directory, "b")), phase);
                    Assert(db.Operation("op").State == (method == "trash" ? "trashed" : "done"), phase);
                }
                finally { Directory.Delete(directory, true); }
            }
        });
    }
    else Console.WriteLine("SKIP Linux native integration (host is not Linux)");
}
finally { Directory.Delete(temporary, true); }
Console.WriteLine($"{passed} passed, {failures} failed"); return failures == 0 ? 0 : 1;

sealed class TestAuthority(string root) : IAuthority
{
    public bool Allow { get; set; } = true;
    public bool Granted { get; set; } = true;
    public Task<string[]> Roots(uint uid, CancellationToken ct) => Task.FromResult(Granted ? new[] { root } : []);
    public Task<AclEntry[]> Check(uint uid, string[] paths, CancellationToken ct) => Task.FromResult(paths.Select(p => new AclEntry(p, Allow && Paths.Within(root, p), Allow && Paths.Within(root, p), Allow && Paths.Within(root, p))).ToArray());
    public Task<Dictionary<string, string>> Labels(string[] paths, string language, CancellationToken ct) => Task.FromResult(paths.ToDictionary(p => p, p => "我的文件/" + Path.GetRelativePath(root, p)));
    public Task Revoke(uint uid, string path, CancellationToken ct) { Granted = false; return Task.CompletedTask; }
}
