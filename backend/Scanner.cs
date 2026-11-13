using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace DuplicateCleanup;

public sealed class Scanner(AccessGuard access)
{
    public async Task Run(Job job, TaskDb db, CancellationToken ct)
    {
        await access.CheckRoots(job.Owner, job.Roots, ct);
        // Read-only scans can rebuild their index after a crash; avoid an fsync for every file.
        // Cleanup journals always use FULL and never share this connection or task directory.
        TaskStore.Sql(db.Connection, "PRAGMA synchronous=NORMAL");
        job.Stage = "枚举文件"; job.Processed = 0; job.Total = null; job.Discovered = 0; job.Errors = 0;
        db.Save(job); db.ResetEnumeration();
        for (var order = 0; order < job.Roots.Length; order++)
        {
            var rootPath = job.Roots[order]; using var root = new SecureRoot(rootPath);
            await Walk(root, "", order, job, db, ct);
        }
        TaskStore.Sql(db.Connection, "DELETE FROM files WHERE seen=0");
        job.Stage = "筛选同大小文件"; db.Save(job);
        // All passes are keyset-paged. No list of all paths/sizes/hashes is retained in memory.
        await Fingerprints(job, db, "sample", ct);
        await Fingerprints(job, db, "hash", ct);
        job.Stage = "逐字节确认与分析共享区块"; job.Processed = 0;
        job.Total = db.Count("files", "hash IS NOT NULL AND EXISTS(SELECT 1 FROM files b WHERE b.id<>files.id AND b.size=files.size AND b.hash=files.hash)"); db.Save(job);
        long after = 0;
        while (true)
        {
            var batch = db.Files("id>$after AND hash IS NOT NULL AND group_id IS NULL AND EXISTS(SELECT 1 FROM files b WHERE b.id<>files.id AND b.size=files.size AND b.hash=files.hash) LIMIT 32", [("$after", after)]).ToArray();
            if (batch.Length == 0) break;
            foreach (var candidate in batch)
            {
                ct.ThrowIfCancellationRequested(); after = candidate.Id;
                // Earlier candidates may already have grouped this row.
                if (db.File(candidate.Id).GroupId is not null) continue;
                try { await Group(candidate, job, db, ct); }
                catch (AppError e) when (e.Status == 503 || e.Status == 403 && e.Code != "aclDenied") { throw; }
                catch (Exception e) when (Recoverable(e)) { db.Remove(candidate.Id); RecordError(job, db, e); }
            }
        }
        TaskStore.Sql(db.Connection, "PRAGMA synchronous=FULL");
        job.Stage = "扫描完成"; job.Processed = job.Total ?? 0; db.Save(job);
    }
    async Task Walk(SecureRoot root, string relative, int order, Job job, TaskDb db, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var full = relative == "" ? root.Path : Path.Combine(root.Path, relative);
        await access.Require(job.Owner, root.Path, full, "read", ct);
        using var dir = root.Open(relative, directory: true);
        // The proc path refers only to our already-opened descriptor, never a user-provided path.
        foreach (var item in Directory.EnumerateFileSystemEntries($"/proc/self/fd/{LinuxFiles.Fd(dir)}"))
        {
            ct.ThrowIfCancellationRequested(); var name = Path.GetFileName(item);
            if (Paths.Internal(name)) continue;
            var child = relative == "" ? name : relative + "/" + name;
            var absolute = Path.Combine(root.Path, child);
            if (access.Protected(absolute)) continue;
            try
            {
                await access.Require(job.Owner, root.Path, absolute, "read", ct);
                // Open O_DIRECTORY first: no symlink traversal and no blocking FIFO opens.
                try
                {
                    using var subdir = root.Open(child, directory: true);
                    await Walk(root, child, order, job, db, ct);
                    continue;
                }
                catch (LinuxException e) when (e.Errno is 20 or 40) { }
                using var f = root.Open(child); var stamp = LinuxFiles.Stamp(f);
                if (db.Upsert(root.Path, child, order, stamp)) job.Discovered++;
                if (job.Discovered % 64 == 0) db.Save(job);
            }
            catch (AppError e) when (e.Status == 503 || e.Status == 403 && e.Code != "aclDenied") { throw; }
            catch (Exception e) when (Recoverable(e)) { RecordError(job, db, e); }
        }
    }
    async Task Fingerprints(Job job, TaskDb db, string column, CancellationToken ct)
    {
        job.Stage = column == "sample" ? "读取首尾采样" : "计算完整内容校验"; job.Processed = 0;
        var condition = column == "sample"
            ? "EXISTS(SELECT 1 FROM files b WHERE b.id<>files.id AND b.size=files.size)"
            : "sample IS NOT NULL AND EXISTS(SELECT 1 FROM files b WHERE b.id<>files.id AND b.size=files.size AND b.sample=files.sample)";
        job.Total = db.Count("files", condition); db.Save(job); long after = 0;
        while (true)
        {
            var rows = db.Files("id>$after AND " + condition + " LIMIT 32", [("$after", after)]).ToArray(); if (rows.Length == 0) break;
            foreach (var row in rows)
            {
                after = row.Id; ct.ThrowIfCancellationRequested();
                try
                {
                    await access.Require(job.Owner, row.Root, row.Path, "read", ct);
                    using var root = new SecureRoot(row.Root); using var f = root.Open(row.Relative);
                    RequireVersion(f, row.Stamp);
                    var cached = (column == "sample" ? row.Sample : row.Hash) ?? db.InodeFingerprint(row, column);
                    if (cached is not null) db.Fingerprint(row.Id, column, cached);
                    if (cached is null)
                    {
                        job.CurrentBytes = 0; job.CurrentTotal = column == "hash" ? row.Stamp.Size : 0;
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var buffer = new byte[128 * 1024];
                        if (column == "sample")
                        {
                            HashRange(f, hash, 0, Math.Min(65536, row.Stamp.Size), buffer);
                            if (row.Stamp.Size > 65536) HashRange(f, hash, Math.Max(65536, row.Stamp.Size - 65536), Math.Min(65536, row.Stamp.Size - 65536), buffer);
                        }
                        else
                        {
                            for (long offset = 0; offset < row.Stamp.Size;)
                            {
                                ct.ThrowIfCancellationRequested();
                                await access.Require(job.Owner, row.Root, row.Path, "read", ct);
                                var length = Math.Min(16 * 1024 * 1024, row.Stamp.Size - offset);
                                HashRange(f, hash, offset, length, buffer); offset += length;
                                job.CurrentBytes = offset; db.Save(job);
                            }
                        }
                        RequireVersion(f, row.Stamp); db.Fingerprint(row.Id, column, Convert.ToHexString(hash.GetHashAndReset()));
                    }
                    job.Processed++; if (job.Processed % 32 == 0) db.Save(job);
                }
                catch (AppError e) when (e.Status == 503 || e.Status == 403 && e.Code != "aclDenied") { throw; }
                catch (Exception e) when (Recoverable(e)) { db.Remove(row.Id); RecordError(job, db, e); }
            }
        }
        job.CurrentBytes = 0; job.CurrentTotal = 0;
        db.Save(job);
    }
    async Task Group(FileEntry seed, Job job, TaskDb db, CancellationToken ct)
    {
        await access.Require(job.Owner, seed.Root, seed.Path, "read", ct);
        using var sr = new SecureRoot(seed.Root); using var source = sr.Open(seed.Relative); RequireVersion(source, seed.Stamp);
        var fs = LinuxFiles.FileSystem(source); var sourceMap = LinuxFiles.Map(source);
        var groupId = seed.Id; long count = 0; long keeper = seed.Id; FileEntry best = seed;
        long after = seed.Id;
        while (true)
        {
            var rows = db.Files("id>$after AND group_id IS NULL AND size=$size AND hash=$hash LIMIT 32", [("$after", after), ("$size", seed.Stamp.Size), ("$hash", seed.Hash)]).ToArray();
            if (rows.Length == 0) break;
            foreach (var row in rows)
            {
                after = row.Id;
                try
                {
                    await access.Require(job.Owner, row.Root, row.Path, "read", ct);
                    using var root = new SecureRoot(row.Root); using var f = root.Open(row.Relative);
                    if (!await Equal(source, seed, f, row, job.Owner, ct)) continue;
                    var sharing = Sharing.Compare(row.Stamp, seed.Stamp, LinuxFiles.FileSystem(f), fs, LinuxFiles.Map(f), sourceMap);
                    db.Membership(row.Id, groupId, sharing); count++; job.Processed++;
                    if (ComparePriority(row, best, job.PriorityRoots) < 0) { best = row; keeper = row.Id; }
                }
                catch (AppError e) when (e.Status == 503 || e.Status == 403 && e.Code != "aclDenied") { throw; }
                catch (Exception e) when (Recoverable(e)) { db.Remove(row.Id); RecordError(job, db, e); }
            }
            db.Save(job);
        }
        RequireVersion(source, seed.Stamp);
        if (count > 0)
        {
            db.Membership(seed.Id, groupId, new("reference", 0, Sharing.ExclusiveEstimate(seed.Stamp, sourceMap), sourceMap.Reliable, null));
            db.Group(groupId, keeper); job.Processed++;
        }
    }
    public static int ComparePriority(FileEntry a, FileEntry b, string[] priorities)
    {
        int Rank(FileEntry x) { var i = Array.FindIndex(priorities, p => Paths.Within(p, x.Path)); return i < 0 ? int.MaxValue : i; }
        var rank = Rank(a).CompareTo(Rank(b)); if (rank != 0) return rank;
        var order = a.RootOrder.CompareTo(b.RootOrder); return order != 0 ? order : StringComparer.Ordinal.Compare(a.Path, b.Path);
    }
    public async Task<bool> Equal(SafeFileHandle source, FileEntry sourceEntry, SafeFileHandle target, FileEntry targetEntry, uint uid, CancellationToken ct)
    {
        RequireVersion(source, sourceEntry.Stamp); RequireVersion(target, targetEntry.Stamp);
        if (sourceEntry.Stamp.Size != targetEntry.Stamp.Size) return false;
        if (!sourceEntry.Stamp.SameInode(targetEntry.Stamp))
        {
            var a = new byte[128 * 1024]; var b = new byte[a.Length];
            for (long offset = 0; offset < sourceEntry.Stamp.Size;)
            {
                ct.ThrowIfCancellationRequested();
                if (offset % (16 * 1024 * 1024) == 0)
                {
                    await access.Require(uid, sourceEntry.Root, sourceEntry.Path, "read", ct);
                    await access.Require(uid, targetEntry.Root, targetEntry.Path, "read", ct);
                }
                var n = (int)Math.Min(a.Length, sourceEntry.Stamp.Size - offset);
                ReadExactly(source, a.AsSpan(0, n), offset); ReadExactly(target, b.AsSpan(0, n), offset);
                if (!a.AsSpan(0, n).SequenceEqual(b.AsSpan(0, n))) return false; offset += n;
            }
        }
        RequireVersion(source, sourceEntry.Stamp); RequireVersion(target, targetEntry.Stamp); return true;
    }
    static void HashRange(SafeFileHandle f, IncrementalHash hash, long offset, long length, byte[] buffer)
    {
        while (length > 0) { var n = (int)Math.Min(buffer.Length, length); ReadExactly(f, buffer.AsSpan(0, n), offset); hash.AppendData(buffer.AsSpan(0, n)); offset += n; length -= n; }
    }
    public static void ReadExactly(SafeFileHandle f, Span<byte> bytes, long offset)
    {
        while (!bytes.IsEmpty) { var n = RandomAccess.Read(f, bytes, offset); if (n == 0) throw new AppError("文件在读取期间发生变化"); bytes = bytes[n..]; offset += n; }
    }
    public static void RequireVersion(SafeFileHandle f, FileStamp expected)
    { if (!expected.SameContentVersion(LinuxFiles.Stamp(f))) throw new AppError("文件已变化，请重新扫描"); }
    public static bool Recoverable(Exception e) => e is AppError or IOException or UnauthorizedAccessException;
    static void RecordError(Job job, TaskDb db, Exception e)
    { job.Errors++; db.Error(e is LinuxException or AppError ? e.Message : "文件读取失败"); if (job.Errors % 32 == 0) db.Save(job); }
}
