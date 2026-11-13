using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DuplicateCleanup;

public sealed class Cleaner(AccessGuard access, Scanner scanner, TaskStore store)
{
    public static void Validate(CleanupRequest request)
    {
        if (request.Method is not ("dedupe" or "hardlink" or "trash" or "delete")) throw new AppError("清理方式无效");
        if (request.Choices.Length > 1000 || request.Choices.Select(x => x.GroupId).Distinct().Count() != request.Choices.Length)
            throw new AppError("重复组选择无效");
        if (!request.AllGroups && !request.Choices.Any(x => !x.Exclude)) throw new AppError("请选择重复组");
    }
    public async IAsyncEnumerable<Operation> Plan(Job scan, CleanupRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        Validate(request); using var db = store.Db(scan.Id);
        var overrides = request.Choices.ToDictionary(c => c.GroupId);
        var fsCache = new Dictionary<string, FileSystemInfo>(); long after = 0;
        while (true)
        {
            var groups = db.Groups(after, 32).ToArray(); if (groups.Length == 0) break;
            foreach (var (id, suggested) in groups)
            {
                after = id; overrides.TryGetValue(id, out var choice);
                if (choice?.Exclude == true || (!request.AllGroups && choice is null)) continue;
                var keeper = db.File(choice?.KeeperId ?? suggested);
                if (keeper.GroupId != id) throw new AppError("保留文件不属于该重复组");
                if (request.PriorityRoots is { Length: > 0 } && choice is null)
                {
                    foreach (var file in GroupFiles(db, id))
                        if (Scanner.ComparePriority(file, keeper, request.PriorityRoots) < 0) keeper = file;
                }
                var references = new Dictionary<string, FileEntry>();
                if (request.Method is "dedupe" or "hardlink")
                {
                    foreach (var file in GroupFiles(db, id))
                    {
                        if (!fsCache.TryGetValue(file.Root, out var fs))
                        {
                            await access.Require(scan.Owner, file.Root, file.Path, "read", ct);
                            using var root = new SecureRoot(file.Root); using var f = root.Open(file.Relative);
                            fsCache[file.Root] = fs = LinuxFiles.FileSystem(f);
                        }
                        var key = Domain(fs, request.Method);
                        if (!references.TryGetValue(key, out var best) || file.Id == keeper.Id ||
                            (best.Id != keeper.Id && Scanner.ComparePriority(file, best, request.PriorityRoots ?? scan.PriorityRoots) < 0))
                            references[key] = file;
                    }
                }
                var chosenIds = choice?.FileIds?.ToHashSet();
                var excludedIds = choice?.ExcludedFileIds?.ToHashSet();
                if (chosenIds is not null)
                    foreach (var chosen in chosenIds) if (db.File(chosen).GroupId != id) throw new AppError("选择文件不属于该重复组");
                foreach (var file in GroupFiles(db, id))
                {
                    if (file.Id == keeper.Id || chosenIds is not null && !chosenIds.Contains(file.Id) || excludedIds?.Contains(file.Id) == true) continue;
                    var source = keeper;
                    if (request.Method is "dedupe" or "hardlink") source = references[Domain(fsCache[file.Root], request.Method)];
                    if (source.Id == file.Id) continue; // Keep one reference on each compatible filesystem.
                    yield return new Operation { Id = file.Id.ToString("x16"), Target = file, Keeper = source, Method = request.Method };
                }
            }
        }
        foreach (var requested in overrides.Keys)
            if (!db.Groups(requested - 1, 1).Any(x => x.Id == requested)) throw new AppError("重复组不存在");
    }
    static string Domain(FileSystemInfo fs, string method) => method == "dedupe" ? fs.Identity : $"{fs.MountId}:{fs.SubvolumeId}";
    static IEnumerable<FileEntry> GroupFiles(TaskDb db, long group)
    {
        long after = 0;
        while (true)
        {
            var batch = db.Files("group_id=$g AND id>$after LIMIT 64", [("$g", group), ("$after", after)]).ToArray();
            if (batch.Length == 0) yield break;
            foreach (var file in batch) { after = file.Id; yield return file; }
        }
    }
    public async Task<Preview> Preview(Job scan, CleanupRequest request, CancellationToken ct)
    {
        long files = 0, bytes = 0, shared = 0, estimated = 0, unavailable = 0; var complete = true;
        var reasons = new HashSet<string>();
        await foreach (var op in Plan(scan, request, ct))
        {
            try
            {
                var result = await Inspect(scan.Owner, op, ct);
                if (request.Method is "dedupe" or "hardlink" && result.Sharing.State is "hardlink" or "fullyShared" or "noAllocatedData") { shared += op.Target.Stamp.Size; continue; }
                files++; bytes += op.Target.Stamp.Size; shared += result.Sharing.SharedBytes;
                if (request.Method != "trash") { estimated += result.Estimate ?? 0; complete &= result.Complete; }
            }
            catch (Exception e) when (Scanner.Recoverable(e)) { unavailable++; complete = false; if (reasons.Count < 8) reasons.Add(SafeError(e)); }
        }
        return new(files, bytes, shared, estimated, complete, unavailable, reasons.ToArray());
    }
    public async Task<(SharingInfo Sharing, long? Estimate, bool Complete)> Inspect(uint uid, Operation op, CancellationToken ct)
    {
        await Permission(uid, op, ct);
        using var root = new SecureRoot(op.Target.Root); using var target = root.Open(op.Target.Relative, op.Method == "dedupe");
        RequireIdentity(target, op.Target.Stamp);
        using var sourceRoot = new SecureRoot(op.Keeper!.Root); using var source = sourceRoot.Open(op.Keeper.Relative);
        RequireIdentity(source, op.Keeper.Stamp);
        var a = LinuxFiles.FileSystem(target); var b = LinuxFiles.FileSystem(source);
        CheckCapability(op.Method, a, b, target, source);
        var map = LinuxFiles.Map(target); var sharing = Sharing.Compare(LinuxFiles.Stamp(target), LinuxFiles.Stamp(source), a, b, map, LinuxFiles.Map(source));
        var noExtra = sharing.State is "hardlink" or "fullyShared" or "noAllocatedData";
        return (sharing, noExtra ? 0 : Sharing.KnownExclusiveEstimate(LinuxFiles.Stamp(target), map),
            noExtra || Sharing.ExclusiveEstimate(LinuxFiles.Stamp(target), map) is not null);
    }
    async Task Permission(uint uid, Operation op, CancellationToken ct)
    {
        await access.Require(uid, op.Keeper!.Root, op.Keeper.Path, op.Method == "hardlink" ? "write" : "read", ct);
        await access.Require(uid, op.Target.Root, op.Target.Path, op.Method switch { "dedupe" => "write", "hardlink" => "replace", _ => "delete" }, ct);
        if (op.Method != "dedupe")
            await access.Require(uid, op.Target.Root, Path.GetDirectoryName(op.Target.Path)!, "write", ct);
    }
    static void CheckCapability(string method, FileSystemInfo a, FileSystemInfo b, SafeFileHandle target, SafeFileHandle source)
    {
        if (a.ReadOnly) throw new AppError("文件系统只读");
        if (method == "dedupe" && (!a.Btrfs || !b.Btrfs || a.Identity != b.Identity)) throw new AppError("Btrfs 合并需要同一兼容的 Btrfs 文件系统");
        if (method == "hardlink")
        {
            if (!a.HardlinkCandidate || a.MountId != b.MountId || LinuxFiles.Stamp(target).Device != LinuxFiles.Stamp(source).Device)
                throw new AppError("硬链接不能跨挂载或子卷，或当前文件系统不支持");
            if (!LinuxFiles.SameMetadata(target, source)) throw new AppError("所有者、权限或扩展属性不同，不能安全替换为硬链接");
        }
    }
    public async Task Run(Job job, TaskDb db, CancellationToken ct)
    {
        Recount(job, db);
        await MeasureBefore(job, db, ct); job.Stage = "清理文件";
        await ProcessPages(job, db, ct);
        Recount(job, db); await MeasureAfter(job, db, false, ct);
        job.Stage = "复测存储池可用空间"; db.Save(job);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        await MeasureAfter(job, db, true, ct);
    }
    async Task ProcessPages(Job job, TaskDb db, CancellationToken ct)
    {
        string after = "";
        while (true)
        {
            var rows = db.Operations(after: after, limit: 32).ToArray(); if (rows.Length == 0) break;
            foreach (var op in rows)
            {
                after = op.Id; ct.ThrowIfCancellationRequested();
                if (op.State is "done" or "trashed" or "skipped" or "restored" or "purged" or "error" or "attention") continue;
                var previousMerged = op.MergedBytes; var previousShared = op.SharedBytes;
                try
                {
                    if (op.Method is "restore" or "purge") await TrashOperation(job, db, op, ct);
                    else await Execute(job, db, op, ct);
                }
                catch (AppError e) when (e.Status is 403 or 503) { throw; }
                catch (Exception e) when (Scanner.Recoverable(e))
                {
                    op.Error = SafeError(e);
                    op.State = op.State is "pending" ? "error" : "attention";
                    db.Save(op);
                }
                // O(1) progress updates; authoritative totals are rebuilt once at the end/restart.
                if (op.State is "done" or "trashed" or "skipped") job.Processed++;
                if (op.State is "done" or "trashed")
                {
                    job.LogicalBytes += op.Target.Stamp.Size; job.EstimatedSavedBytes += op.EstimatedSaved;
                    job.EstimateComplete &= op.EstimateComplete;
                }
                job.MergedBytes += op.MergedBytes - previousMerged; job.AlreadySharedBytes += op.SharedBytes - previousShared;
                job.CurrentBytes = 0; job.CurrentTotal = 0;
                if (op.State == "trashed") job.TrashBytes += op.Target.Stamp.Size;
                if (op.State is "error" or "attention") { job.Errors++; job.EstimateComplete = false; }
                db.Save(job);
            }
        }
    }
    async Task Execute(Job job, TaskDb db, Operation op, CancellationToken ct)
    {
        using var root = new SecureRoot(op.Target.Root);
        using var sourceRoot = new SecureRoot(op.Keeper!.Root);
        if (op.State == "pending")
        {
            var inspect = await Inspect(job.Owner, op, ct);
            op.SharedBytes = inspect.Sharing.SharedBytes;
            if (op.Method is "dedupe" or "hardlink" && inspect.Sharing.State is "hardlink" or "fullyShared" or "noAllocatedData")
            { op.State = "skipped"; op.SharedBytes = op.Target.Stamp.Size; op.EstimateComplete = true; db.Save(op); return; }
            using var target = root.Open(op.Target.Relative, op.Method == "dedupe");
            using var source = sourceRoot.Open(op.Keeper.Relative);
            await Confirm(job.Owner, source, op.Keeper, target, op.Target, ct);
            op.EstimatedSaved = op.Method == "trash" ? 0 : inspect.Estimate ?? 0;
            op.EstimateComplete = op.Method == "trash" || inspect.Complete;
            if (op.Method == "dedupe") { op.State = "merging"; db.Save(op); }
            else
            {
                var dir = (op.Method == "trash" ? AppInfo.Trash : AppInfo.Staging) + $"/{job.Owner}/{job.Id}";
                await access.Require(job.Owner, op.Target.Root, op.Target.Root, "write", ct);
                root.EnsurePrivateDirectory(dir, job.Owner);
                op.HoldingRelative = dir + "/" + op.Id + ".data";
                if (op.Method == "hardlink") op.LinkRelative = dir + "/" + op.Id + ".link";
                // Recovery mapping is deliberately persisted next to held files as well as in app data.
                root.WriteNew(dir + "/" + op.Id + ".json", JsonSerializer.SerializeToUtf8Bytes(op, AppJson.Default.Operation));
                op.State = "prepared"; db.Save(op);
            }
        }
        if (op.Method == "dedupe") { await Merge(job, db, op, root, sourceRoot, ct); return; }
        if (op.Method == "hardlink")
        { await Hardlink(job, db, op, root, sourceRoot, ct); if (op.State == "done") DiscardManifest(root, op); return; }
        if (op.State == "prepared")
        {
            var held = root.TryStamp(op.HoldingRelative!);
            if (held is null)
            {
                await Permission(job.Owner, op, ct);
                using var target = root.Open(op.Target.Relative); using var source = sourceRoot.Open(op.Keeper.Relative);
                await Confirm(job.Owner, source, op.Keeper, target, op.Target, ct);
                root.Rename(op.Target.Relative, root, op.HoldingRelative!);
                held = root.TryStamp(op.HoldingRelative!);
            }
            if (held is null || !Matches(held, op.Target.Stamp)) throw new AppError("暂存文件身份不符，保留文件等待检查");
            op.State = "staged"; db.Save(op);
        }
        if (op.State == "staged")
        {
            // Once a pathname has moved, cancellation waits for this file's stable endpoint.
            // Authorization is still checked; CancellationToken.None never bypasses ACL checks.
            await CheckHeld(job.Owner, op, root, sourceRoot, CancellationToken.None);
            if (op.Method == "trash") { op.State = "trashed"; db.Save(op); return; }
            op.State = "deleting"; db.Save(op);
        }
        if (op.State == "deleting")
        {
            if (root.TryStamp(op.HoldingRelative!) is not null)
            { await CheckHeld(job.Owner, op, root, sourceRoot, CancellationToken.None); root.Unlink(op.HoldingRelative!); }
            op.State = "done"; db.Save(op); DiscardManifest(root, op);
        }
    }
    async Task Merge(Job job, TaskDb db, Operation op, SecureRoot root, SecureRoot sourceRoot, CancellationToken ct)
    {
        using var source = sourceRoot.Open(op.Keeper!.Relative); using var target = root.Open(op.Target.Relative, writable: true);
        await Permission(job.Owner, op, ct); await Confirm(job.Owner, source, op.Keeper, target, op.Target, ct);
        CheckCapability("dedupe", LinuxFiles.FileSystem(target), LinuxFiles.FileSystem(source), target, source);
        var a = LinuxFiles.Map(target); var b = LinuxFiles.Map(source);
        // An app-data checkpoint and Btrfs metadata may live on different filesystems. Revisit the
        // prefix on resume and let current extent maps/kernel comparison prove it is still shared.
        var recordedThrough = op.Offset;
        op.Offset = 0;
        while (op.Offset < op.Target.Stamp.Size)
        {
            ct.ThrowIfCancellationRequested(); await Permission(job.Owner, op, ct);
            var n = Math.Min(16L * 1024 * 1024, op.Target.Stamp.Size - op.Offset);
            if (Sharing.RangeShared(a, b, op.Offset, n)) { op.Offset += n; db.Save(op); continue; }
            // Persist the current offset before ioctl. Replaying a range is safe; savings are never
            // derived from ioctl's bytes_deduped counter, including after a crash.
            db.Save(op); var result = LinuxFiles.Dedupe(source, target, op.Offset, n);
            if (result.Status != 0 || result.Bytes <= 0 || result.Bytes > n)
                throw new AppError(result.Status == 1 ? "文件内容已变化，区块未合并" : $"区块合并失败（状态 {result.Status}）");
            var previousOffset = op.Offset;
            op.Offset += result.Bytes;
            op.MergedBytes += Math.Max(0, op.Offset - Math.Max(previousOffset, recordedThrough));
            LinuxFiles.Sync(target); // Make extent updates durable BEFORE advancing the journal.
            db.Save(op);
            job.CurrentBytes = op.Offset; job.CurrentTotal = op.Target.Stamp.Size; db.Save(job);
        }
        LinuxFiles.Sync(target);
        var final = Sharing.Compare(LinuxFiles.Stamp(target), LinuxFiles.Stamp(source), LinuxFiles.FileSystem(target), LinuxFiles.FileSystem(source), LinuxFiles.Map(target), LinuxFiles.Map(source));
        if (!final.Complete && final.State != "fullyShared") op.EstimateComplete = false;
        op.State = "done"; db.Save(op);
    }
    async Task Hardlink(Job job, TaskDb db, Operation op, SecureRoot root, SecureRoot sourceRoot, CancellationToken ct)
    {
        if (op.State == "prepared")
        {
            if (root.TryStamp(op.LinkRelative!) is null)
            {
                await Permission(job.Owner, op, ct);
                using var target = root.Open(op.Target.Relative); using var source = sourceRoot.Open(op.Keeper!.Relative);
                await Confirm(job.Owner, source, op.Keeper, target, op.Target, ct);
                CheckCapability("hardlink", LinuxFiles.FileSystem(target), LinuxFiles.FileSystem(source), target, source);
                sourceRoot.Link(op.Keeper.Relative, root, op.LinkRelative!);
            }
            op.State = "linked"; db.Save(op);
        }
        if (op.State == "linked")
        {
            var current = root.TryStamp(op.Target.Relative); var held = root.TryStamp(op.LinkRelative!);
            if (current is not null && held is not null && Matches(current, op.Keeper!.Stamp) && Matches(held, op.Target.Stamp))
            { op.State = "swapped"; db.Save(op); } // exchange happened before a crash
            else
            {
                await Permission(job.Owner, op, CancellationToken.None);
                using var source = sourceRoot.Open(op.Keeper!.Relative); using var target = root.Open(op.Target.Relative);
                await Confirm(job.Owner, source, op.Keeper, target, op.Target, CancellationToken.None);
                if (held is null || !Matches(held, LinuxFiles.Stamp(source))) throw new AppError("临时链接身份不符");
                root.Rename(op.Target.Relative, root, op.LinkRelative!, exchange: true);
                op.State = "swapped"; db.Save(op);
            }
        }
        if (op.State is "swapped" or "deletingOld")
        {
            await access.Require(job.Owner, op.Target.Root, op.Target.Path, "replace", CancellationToken.None);
            var held = root.TryStamp(op.LinkRelative!);
            using var current = root.Open(op.Target.Relative);
            RequireIdentity(current, op.Keeper!.Stamp);
            if (held is not null)
            {
                if (!Matches(held, op.Target.Stamp)) throw new AppError("替换期间文件发生变化，原文件已保留供恢复");
                using var old = root.Open(op.LinkRelative!);
                await access.Require(job.Owner, op.Target.Root, Path.GetDirectoryName(op.Target.Path)!, "write", CancellationToken.None);
                var beforeCurrent = LinuxFiles.Stamp(current); var beforeOld = LinuxFiles.Stamp(old);
                await Confirm(job.Owner, current, op.Keeper with { Root = op.Target.Root, Relative = op.Target.Relative }, old,
                    op.Target with { Relative = op.LinkRelative! }, CancellationToken.None);
                if (!LinuxFiles.SameMetadata(current, old)) throw new AppError("替换期间元数据发生变化，旧文件已保留供检查");
                Scanner.RequireVersion(current, beforeCurrent); Scanner.RequireVersion(old, beforeOld);
                op.State = "deletingOld"; db.Save(op); root.Unlink(op.LinkRelative!);
            }
            op.State = "done"; db.Save(op);
        }
    }
    async Task CheckHeld(uint uid, Operation op, SecureRoot root, SecureRoot sourceRoot, CancellationToken ct)
    {
        var path = Path.Combine(op.Target.Root, op.HoldingRelative!);
        await access.Require(uid, op.Target.Root, path, "read", ct);
        await access.Require(uid, op.Target.Root, Path.GetDirectoryName(op.Target.Path)!, "write", ct);
        using var held = root.Open(op.HoldingRelative!); using var source = sourceRoot.Open(op.Keeper!.Relative);
        await Confirm(uid, source, op.Keeper, held, op.Target with { Relative = op.HoldingRelative! }, ct);
        var keeperPath = sourceRoot.TryStamp(op.Keeper.Relative);
        if (keeperPath is null || !Matches(keeperPath, LinuxFiles.Stamp(source)))
            throw new AppError("保留文件已不在原路径，暂存文件已保留供恢复");
    }
    async Task Confirm(uint uid, SafeFileHandle source, FileEntry keeper, SafeFileHandle target, FileEntry file, CancellationToken ct)
    {
        RequireIdentity(source, keeper.Stamp); RequireIdentity(target, file.Stamp);
        var currentKeeper = keeper with { Stamp = LinuxFiles.Stamp(source) };
        var currentTarget = file with { Stamp = LinuxFiles.Stamp(target) };
        if (!await scanner.Equal(source, currentKeeper, target, currentTarget, uid, ct)) throw new AppError("内容不再相同，请重新扫描");
        // Renames and our own hardlink operations change ctime/nlink. Verify the original scan hash
        // rather than accepting these ctime changes on trust.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[128 * 1024];
        for (long offset = 0; offset < keeper.Stamp.Size;)
        {
            ct.ThrowIfCancellationRequested();
            if (offset % (16 * 1024 * 1024) == 0) await access.Require(uid, keeper.Root, keeper.Path, "read", ct);
            var n = (int)Math.Min(buffer.Length, keeper.Stamp.Size - offset);
            Scanner.ReadExactly(source, buffer.AsSpan(0, n), offset); hash.AppendData(buffer.AsSpan(0, n)); offset += n;
        }
        if (keeper.Hash is null || Convert.ToHexString(hash.GetHashAndReset()) != keeper.Hash) throw new AppError("文件内容与扫描记录不同");
        LinuxFiles.Sync(source); LinuxFiles.Sync(target);
        Scanner.RequireVersion(source, currentKeeper.Stamp); Scanner.RequireVersion(target, currentTarget.Stamp);
    }
    public static bool Matches(FileStamp a, FileStamp b) => a.SameInode(b) && a.Size == b.Size && a.MtimeSec == b.MtimeSec && a.MtimeNs == b.MtimeNs;
    static void RequireIdentity(SafeFileHandle f, FileStamp stamp) { if (!Matches(LinuxFiles.Stamp(f), stamp)) throw new AppError("文件身份或内容版本已变化，请重新扫描"); }
    async Task TrashOperation(Job job, TaskDb db, Operation op, CancellationToken ct)
    {
        var sourceJob = store.Get(op.SourceTaskId!, job.Owner); using var sourceDb = store.Db(sourceJob.Id);
        var sourceOp = sourceDb.Operation(op.SourceOperationId!);
        void SaveSource() { sourceDb.Save(sourceOp); Recount(sourceJob, sourceDb); sourceDb.Save(sourceJob); }
        if (op.Method == "restore") op.EstimateComplete = true;
        var heldRelative = sourceOp.HoldingRelative ?? throw new AppError("回收站记录无效");
        using var root = new SecureRoot(sourceOp.Target.Root);
        if (sourceOp.State == (op.Method == "restore" ? "restored" : "purged"))
        { SaveSource(); op.State = op.State == "committing" ? "done" : "skipped"; db.Save(op); return; }
        if (sourceOp.State != "trashed") throw new AppError("回收站文件已处理或需要人工检查");
        var heldPath = Path.Combine(root.Path, heldRelative);
        var held = root.TryStamp(heldRelative);
        if (held is null)
        {
            if (op.State == "committing" && op.Method == "purge") { sourceOp.State = "purged"; SaveSource(); op.State = "done"; db.Save(op); return; }
            if (op.State == "committing" && op.Method == "restore" && root.TryStamp(sourceOp.Target.Relative) is { } restored && Matches(restored, sourceOp.Target.Stamp))
            { sourceOp.State = "restored"; SaveSource(); op.State = "done"; db.Save(op); return; }
            throw new AppError("回收站文件已不在原位置");
        }
        if (!Matches(held, sourceOp.Target.Stamp)) throw new AppError("回收站文件已变化，拒绝自动处理");
        await access.Require(job.Owner, root.Path, heldPath, "read", ct);
        await access.Require(job.Owner, root.Path, Path.GetDirectoryName(sourceOp.Target.Path)!, "write", ct);
        if (op.Method == "restore")
        {
            await access.Require(job.Owner, root.Path, Path.GetDirectoryName(sourceOp.Target.Path)!, "write", ct);
            op.State = "committing"; db.Save(op);
            root.Rename(heldRelative, root, sourceOp.Target.Relative); // RENAME_NOREPLACE
            sourceOp.State = "restored";
        }
        else
        {
            using var f = root.Open(heldRelative);
            RequireIdentity(f, sourceOp.Target.Stamp);
            op.EstimatedSaved = Sharing.ExclusiveEstimate(held, LinuxFiles.Map(f)) ?? 0;
            op.EstimateComplete = Sharing.ExclusiveEstimate(held, LinuxFiles.Map(f)) is not null;
            op.State = "committing"; db.Save(op); root.Unlink(heldRelative); sourceOp.State = "purged";
        }
        SaveSource(); DiscardManifest(root, sourceOp);
        op.State = "done"; db.Save(op);
    }
    static void DiscardManifest(SecureRoot root, Operation op)
    {
        var relative = op.Method == "hardlink" ? op.LinkRelative : op.HoldingRelative;
        if (relative is null) return;
        var manifest = Path.GetDirectoryName(relative)!.Replace('\\', '/') + "/" + op.Id + ".json";
        try
        {
            using var file = root.Open(manifest);
            if (LinuxFiles.Stamp(file).Uid == LibC.geteuid()) root.Unlink(manifest);
        }
        catch (Exception e) when (Scanner.Recoverable(e)) { /* Durable task DB already records success; leave metadata if cleanup is unavailable. */ }
    }
    public static void Recount(Job j, TaskDb db)
    {
        j.Processed = 0; j.LogicalBytes = 0; j.MergedBytes = 0; j.AlreadySharedBytes = 0; j.EstimatedSavedBytes = 0;
        j.EstimateComplete = true; j.TrashBytes = 0; j.Errors = 0; string after = "";
        while (true)
        {
            var rows = db.Operations(after: after).ToArray(); if (rows.Length == 0) break;
            foreach (var o in rows)
            {
                after = o.Id; j.MergedBytes += o.MergedBytes; j.AlreadySharedBytes += o.SharedBytes;
                if (o.State is "done" or "trashed" or "restored" or "purged" or "skipped") j.Processed++;
                if (o.State is "done" or "trashed" or "restored" or "purged") { j.LogicalBytes += o.Target.Stamp.Size; j.EstimatedSavedBytes += o.EstimatedSaved; j.EstimateComplete &= o.EstimateComplete; }
                if (o.State == "trashed") j.TrashBytes += o.Target.Stamp.Size;
                if (o.State is "error" or "attention") { j.Errors++; j.EstimateComplete = false; }
            }
        }
    }
    async Task MeasureBefore(Job job, TaskDb db, CancellationToken ct)
    {
        foreach (var rootPath in job.Roots)
        {
            await access.Require(job.Owner, rootPath, rootPath, "read", ct);
            using var root = new SecureRoot(rootPath); var fs = LinuxFiles.FileSystem(root.Handle);
            if (job.Space.All(x => x.FileSystemId != fs.Identity)) job.Space.Add(new SpaceMeasurement
            { FileSystemId = fs.Identity, Root = rootPath, BeforeAvailable = LinuxFiles.Available(root.Handle), BeforeAt = DateTimeOffset.UtcNow });
        }
        db.Save(job);
    }
    async Task MeasureAfter(Job job, TaskDb db, bool delayed, CancellationToken ct)
    {
        foreach (var s in job.Space)
        {
            await access.Require(job.Owner, s.Root, s.Root, "read", ct);
            using var root = new SecureRoot(s.Root);
            if (LinuxFiles.FileSystem(root.Handle).Identity != s.FileSystemId) { s.Error = "文件系统已变化，无法比较"; continue; }
            var value = LinuxFiles.Available(root.Handle); var now = DateTimeOffset.UtcNow;
            if (delayed) { s.DelayedAvailable = value; s.DelayedAt = now; } else { s.AfterAvailable = value; s.AfterAt = now; }
        }
        db.Save(job);
    }
    public static string SafeError(Exception e) => e is AppError or LinuxException ? e.Message : "文件操作失败，文件已保留供检查";
}
