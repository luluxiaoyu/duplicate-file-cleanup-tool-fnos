using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DuplicateCleanup;

internal static partial class LibC
{
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int openat(int fd, string path, int flags, uint mode);
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int statx(int fd, string path, int flags, uint mask, nint buffer);
    [LibraryImport("libc", SetLastError = true)] internal static partial int fstatfs(int fd, nint buffer);
    [LibraryImport("libc", SetLastError = true)] internal static partial int ioctl(int fd, nuint request, nint buffer);
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int renameat2(int oldfd, string oldpath, int newfd, string newpath, uint flags);
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int linkat(int oldfd, string oldpath, int newfd, string newpath, int flags);
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int unlinkat(int fd, string path, int flags);
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mkdirat(int fd, string path, uint mode);
    [LibraryImport("libc", SetLastError = true)] internal static partial int fsync(int fd);
    [LibraryImport("libc", SetLastError = true)] internal static partial int fchmod(int fd, uint mode);
    [LibraryImport("libc", SetLastError = true)] internal static partial nint flistxattr(int fd, nint list, nuint size);
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint fgetxattr(int fd, string name, nint value, nuint size);
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int fsetxattr(int fd, string name, nint value, nuint size, int flags);
    [LibraryImport("libc")] internal static partial uint geteuid();
}

public static class Paths
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || path.Contains('\0') ||
            path.Split('/').Any(p => p is ".." or ".")) throw new AppError("目录路径无效");
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (normalized == "/") throw new AppError("不能扫描系统根目录");
        return normalized;
    }
    public static bool Within(string root, string path) => path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
    public static string[] Parts(string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains('\0')) throw new AppError("相对路径无效");
        var parts = relative.Split('/');
        if (parts.Any(x => x is "" or "." or "..")) throw new AppError("相对路径无效");
        return parts;
    }
    public static bool Internal(string name) => name == AppInfo.Trash || name == AppInfo.Staging;
}

// Every user path component is opened relative to an already opened directory with O_NOFOLLOW.
// Directory descriptors, not checked strings, anchor subsequent mutations.
public sealed class SecureRoot : IDisposable
{
    const int DirectoryFlags = 0x10000 | 0x20000 | 0x80000;
    public SafeFileHandle Handle { get; }
    public string Path { get; }
    public ulong MountId { get; }
    public SecureRoot(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("文件操作仅支持 Linux");
        Path = Paths.Normalize(path);
        var current = LinuxFiles.CheckedHandle(LibC.openat(-100, "/", DirectoryFlags, 0));
        try
        {
            foreach (var part in Path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = LinuxFiles.CheckedHandle(LibC.openat(LinuxFiles.Fd(current), part, DirectoryFlags, 0));
                current.Dispose(); current = next;
            }
            Handle = current;
            MountId = LinuxFiles.Stamp(Handle).MountId;
        }
        catch { current.Dispose(); throw; }
    }
    public SafeFileHandle Open(string relative, bool writable = false, bool directory = false)
    {
        if (relative == "") return LinuxFiles.CheckedHandle(LibC.openat(LinuxFiles.Fd(Handle), ".", DirectoryFlags, 0));
        using var parent = Parent(relative, out var name);
        var flags = 0x20000 | 0x80000 | 0x800 | (directory ? 0x10000 : 0) | (writable ? 2 : 0);
        var handle = LinuxFiles.CheckedHandle(LibC.openat(LinuxFiles.Fd(parent), name, flags, 0));
        try
        {
            var s = LinuxFiles.Stamp(handle);
            if (s.MountId != MountId) throw new AppError("嵌套挂载点需要单独授权和选择");
            if (directory ? !s.Directory : !s.Regular) throw new AppError("仅支持普通文件或目录");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }
    public SafeFileHandle Parent(string relative, out string name)
    {
        var parts = Paths.Parts(relative); name = parts[^1];
        var current = LinuxFiles.CheckedHandle(LibC.openat(LinuxFiles.Fd(Handle), ".", DirectoryFlags, 0));
        try
        {
            foreach (var part in parts[..^1])
            {
                var next = LinuxFiles.CheckedHandle(LibC.openat(LinuxFiles.Fd(current), part, DirectoryFlags, 0));
                current.Dispose(); current = next;
                if (LinuxFiles.Stamp(current).MountId != MountId) throw new AppError("禁止跨挂载访问");
            }
            return current;
        }
        catch { current.Dispose(); throw; }
    }
    public FileStamp? TryStamp(string relative)
    {
        try { using var f = Open(relative); return LinuxFiles.Stamp(f); }
        catch (LinuxException e) when (e.Errno == 2) { return null; }
    }
    public void Rename(string source, SecureRoot destination, string target, bool exchange = false)
    {
        using var a = Parent(source, out var an); using var b = destination.Parent(target, out var bn);
        LinuxFiles.Check(LibC.renameat2(LinuxFiles.Fd(a), an, LinuxFiles.Fd(b), bn, exchange ? 2u : 1u));
        LinuxFiles.Sync(a); LinuxFiles.Sync(b);
    }
    public void Link(string source, SecureRoot destination, string target)
    {
        using var a = Parent(source, out var an); using var b = destination.Parent(target, out var bn);
        LinuxFiles.Check(LibC.linkat(LinuxFiles.Fd(a), an, LinuxFiles.Fd(b), bn, 0));
        LinuxFiles.Sync(b);
    }
    public void Unlink(string relative)
    {
        using var p = Parent(relative, out var name);
        LinuxFiles.Check(LibC.unlinkat(LinuxFiles.Fd(p), name, 0)); LinuxFiles.Sync(p);
    }
    public void EnsurePrivateDirectory(string relative, uint uid)
    {
        // Existing directories must be ours; never chmod an arbitrary pre-existing user directory.
        var parts = Paths.Parts(relative); var prefix = "";
        foreach (var part in parts)
        {
            prefix = prefix.Length == 0 ? part : prefix + "/" + part;
            using var parent = Parent(prefix, out var name);
            var rc = LibC.mkdirat(LinuxFiles.Fd(parent), name, 0x1c0); // 0700 before installing ACL
            if (rc != 0 && Marshal.GetLastPInvokeError() != 17) LinuxFiles.Check(rc);
            using var dir = Open(prefix, directory: true);
            if (LinuxFiles.Stamp(dir).Uid != LibC.geteuid()) throw new AppError("回收站目录不属于应用，拒绝使用");
            if (prefix == parts[0]) LinuxFiles.SetTraversalAcl(dir);
            else LinuxFiles.SetPrivateAcl(dir, uid);
            LinuxFiles.Sync(parent);
        }
    }
    public void WriteNew(string relative, byte[] bytes)
    {
        using var parent = Parent(relative, out var name);
        using var f = LinuxFiles.CheckedHandle(LibC.openat(LinuxFiles.Fd(parent), name, 0x80000 | 0x20000 | 0x40 | 0x80 | 1, 0x180));
        RandomAccess.Write(f, bytes, 0); LinuxFiles.Sync(f); LinuxFiles.Sync(parent);
    }
    public void Dispose() => Handle.Dispose();
}

public sealed class LinuxException(int errno) : IOException($"文件系统操作失败（errno {errno}）")
{
    public int Errno { get; } = errno;
}
public static unsafe class LinuxFiles
{
    public static int Fd(SafeFileHandle f) => checked((int)f.DangerousGetHandle());
    public static void Check(int rc) { if (rc < 0) throw new LinuxException(Marshal.GetLastPInvokeError()); }
    public static SafeFileHandle CheckedHandle(int fd) { Check(fd); return new SafeFileHandle(fd, true); }
    public static void Sync(SafeFileHandle f) => Check(LibC.fsync(Fd(f)));
    public static void SyncAppDirectory(string trustedPath)
    {
        // App Center paths may themselves be symlinks. This helper is only for trusted app-owned
        // metadata directories; user file operations always use SecureRoot and O_NOFOLLOW.
        using var directory = CheckedHandle(LibC.openat(-100, trustedPath, 0x10000 | 0x80000, 0)); Sync(directory);
    }
    public static FileStamp Stamp(SafeFileHandle f)
    {
        byte* b = stackalloc byte[256]; new Span<byte>(b, 256).Clear();
        Check(LibC.statx(Fd(f), "", 0x1000 | 0x100, 0x7ff | 0x1000, (nint)b));
        var s = new ReadOnlySpan<byte>(b, 256);
        uint U(int o) => BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(b + o, 4));
        ulong L(int o) => BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(b + o, 8));
        if ((U(0) & 0x17ff) != 0x17ff) throw new AppError("内核无法提供安全操作所需的文件身份信息");
        return new(((ulong)U(136) << 32) | U(140), L(32), BinaryPrimitives.ReadUInt16LittleEndian(s[28..]),
            U(20), U(24), U(16), checked((long)L(40)), L(48), (long)L(112), U(120), (long)L(96), U(104), L(144));
    }
    public static FileSystemInfo FileSystem(SafeFileHandle f)
    {
        long* b = stackalloc long[15]; new Span<long>(b, 15).Clear();
        Check(LibC.fstatfs(Fd(f), (nint)b)); var stamp = Stamp(f);
        var btrfs = b[0] == 0x9123683e;
        var type = btrfs ? "Btrfs" : b[0] switch { 0xef53 => "ext2/3/4", 0x58465342 => "XFS", 0x6969 => "NFS", 0xff534d42 => "SMB", 0x4d44 => "FAT", 0x01021994 => "tmpfs", _ => $"0x{b[0]:x}" };
        var fsid = $"{(ulong)b[7]:x16}";
        ulong subvolume = 0;
        if (btrfs)
        {
            byte* info = stackalloc byte[1024]; new Span<byte>(info, 1024).Clear();
            if (LibC.ioctl(Fd(f), 0x8400941f, (nint)info) == 0) fsid = Convert.ToHexString(new ReadOnlySpan<byte>(info + 16, 16));
            byte* lookup = stackalloc byte[4096]; new Span<byte>(lookup, 4096).Clear();
            *(ulong*)(lookup + 8) = 256; // BTRFS_IOC_INO_LOOKUP, treeid=0, objectid=FIRST_FREE_OBJECTID
            if (LibC.ioctl(Fd(f), 0xd0009412, (nint)lookup) == 0) subvolume = *(ulong*)lookup;
        }
        var ro = (b[10] & 1) != 0;
        return new(type, type + ":" + fsid, stamp.MountId, subvolume, ro, btrfs,
            !ro && b[0] is not (0x4d44 or 0x2011bab0), ro ? "文件系统只读" : null);
    }
    public static long Available(SafeFileHandle f)
    {
        long* b = stackalloc long[15]; new Span<long>(b, 15).Clear(); Check(LibC.fstatfs(Fd(f), (nint)b));
        return checked(b[4] * (b[9] > 0 ? b[9] : b[1]));
    }
    public static ExtentMap Map(SafeFileHandle f)
    {
        const int count = 128, bytes = 32 + count * 56;
        var extents = new List<Extent>(); ulong start = 0;
        // Refuse pathological maps instead of allowing one fragmented file to exhaust memory.
        byte* b = stackalloc byte[bytes];
        for (var page = 0; page < 8192; page++)
        {
            new Span<byte>(b, bytes).Clear();
            *(ulong*)b = start; *(ulong*)(b + 8) = ulong.MaxValue - start; *(uint*)(b + 24) = count;
            if (LibC.ioctl(Fd(f), 0xc020660b, (nint)b) != 0) return new(false, [], "无法读取区块映射");
            var n = *(uint*)(b + 20);
            if (n > count) return new(false, [], "内核返回的区块映射无效");
            if (n == 0) return new(true, extents.ToArray(), null);
            for (var i = 0; i < n; i++)
            {
                var p = b + 32 + i * 56;
                var e = new Extent(*(ulong*)p, *(ulong*)(p + 8), *(ulong*)(p + 16), *(uint*)(p + 40));
                // Encoded/inline/delalloc/unknown/unwritten/merged maps cannot prove physical savings.
                if ((e.Flags & (0x2u | 0x4u | 0x8u | 0x80u | 0x100u | 0x200u | 0x400u | 0x800u | 0x1000u)) != 0)
                    return new(false, [], "压缩、内联、稀疏预分配或未知区块，空间统计不完整");
                if (e.Length == 0 || e.Logical < start || e.Physical == 0) return new(false, [], "区块映射不连续、不可见或已变化");
                extents.Add(e); start = checked(e.Logical + e.Length);
                if ((e.Flags & 1) != 0) return new(true, extents.ToArray(), null);
            }
        }
        return new(false, [], "文件区块过于碎片化，空间统计不完整");
    }
    public static (long Bytes, int Status) Dedupe(SafeFileHandle source, SafeFileHandle target, long offset, long length)
    {
        byte* b = stackalloc byte[56]; new Span<byte>(b, 56).Clear();
        *(ulong*)b = (ulong)offset; *(ulong*)(b + 8) = (ulong)length; *(ushort*)(b + 16) = 1;
        *(long*)(b + 24) = Fd(target); *(ulong*)(b + 32) = (ulong)offset;
        Check(LibC.ioctl(Fd(source), 0xc0189436, (nint)b));
        return (checked((long)*(ulong*)(b + 40)), *(int*)(b + 48));
    }
    public static bool SameMetadata(SafeFileHandle a, SafeFileHandle b)
    {
        var x = Stamp(a); var y = Stamp(b);
        if (x.Uid != y.Uid || x.Gid != y.Gid || x.Mode != y.Mode) return false;
        long af = 0, bf = 0;
        Check(LibC.ioctl(Fd(a), 0x80086601, (nint)(&af)));
        Check(LibC.ioctl(Fd(b), 0x80086601, (nint)(&bf)));
        if (af != bf) return false;
        var ax = Xattrs(a); var bx = Xattrs(b);
        return ax.Count == bx.Count && ax.All(v => bx.TryGetValue(v.Key, out var other) && v.Value.AsSpan().SequenceEqual(other));
    }
    static Dictionary<string, byte[]> Xattrs(SafeFileHandle f)
    {
        var size = (long)LibC.flistxattr(Fd(f), 0, 0);
        if (size < 0) { if (Marshal.GetLastPInvokeError() == 95) return []; throw new LinuxException(Marshal.GetLastPInvokeError()); }
        if (size > 65536) throw new AppError("扩展属性过大，无法核验");
        byte* names = stackalloc byte[(int)Math.Max(1, size)];
        var actual = (long)LibC.flistxattr(Fd(f), (nint)names, (nuint)size); Check((int)actual);
        var result = new Dictionary<string, byte[]>();
        foreach (var name in System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>(names, (int)actual)).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var n = (long)LibC.fgetxattr(Fd(f), name, 0, 0); Check((int)n);
            if (n > 65536) throw new AppError("扩展属性过大");
            var data = new byte[n];
            fixed (byte* p = data) { var read = (long)LibC.fgetxattr(Fd(f), name, (nint)p, (nuint)n); if (read != n) throw new AppError("扩展属性在检查期间变化"); }
            result[name] = data;
        }
        return result;
    }
    public static void SetPrivateAcl(SafeFileHandle dir, uint uid)
    {
        // Only the app may replace directory entries. The task user can inspect files, but restore
        // and purge go through task ownership + current authorization/original-parent ACL checks.
        var named = uid != LibC.geteuid(); var data = new byte[4 + (named ? 5 : 3) * 8];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 2);
        var index = 4;
        void Add(ushort tag, ushort perm, uint id)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(index), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(index + 2), perm);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(index + 4), id); index += 8;
        }
        Add(1, 7, uint.MaxValue); if (named) Add(2, 5, uid); Add(4, 0, uint.MaxValue);
        if (named) Add(16, 7, uint.MaxValue); Add(32, 0, uint.MaxValue);
        fixed (byte* p = data) CheckAcl(LibC.fsetxattr(Fd(dir), "system.posix_acl_access", (nint)p, (nuint)data.Length, 0));
        Sync(dir);
    }
    public static void SetTraversalAcl(SafeFileHandle dir)
    {
        // The common namespace is searchable, never listable; user buckets below it carry the
        // actual named-user ACL. Adding user B must not revoke user A's existing trash access.
        byte* data = stackalloc byte[28]; new Span<byte>(data, 28).Clear(); *(uint*)data = 2;
        *(ushort*)(data + 4) = 1; *(ushort*)(data + 6) = 7; *(uint*)(data + 8) = uint.MaxValue;
        *(ushort*)(data + 12) = 4; *(ushort*)(data + 14) = 1; *(uint*)(data + 16) = uint.MaxValue;
        *(ushort*)(data + 20) = 32; *(ushort*)(data + 22) = 1; *(uint*)(data + 24) = uint.MaxValue;
        CheckAcl(LibC.fsetxattr(Fd(dir), "system.posix_acl_access", (nint)data, 28, 0)); Sync(dir);
    }
    static void CheckAcl(int result)
    {
        if (result >= 0) return;
        var error = Marshal.GetLastPInvokeError();
        throw new AppError(error == 95 ? "当前文件系统不支持隔离 ACL，无法安全建立回收站或暂存目录" : $"无法设置回收站或暂存目录权限（errno {error}）");
    }
}
