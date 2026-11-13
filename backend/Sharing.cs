namespace DuplicateCleanup;

public static class Sharing
{
    public static SharingInfo Compare(FileStamp target, FileStamp keeper, FileSystemInfo targetFs,
        FileSystemInfo keeperFs, ExtentMap targetMap, ExtentMap keeperMap)
    {
        if (target.SameInode(keeper)) return new("hardlink", target.Size, 0, true, "多个路径已指向同一个文件");
        if (!targetMap.Reliable || !keeperMap.Reliable)
            return new("unknown", 0, null, false, targetMap.Reason ?? keeperMap.Reason);
        var compatible = targetFs.Identity == keeperFs.Identity;
        long shared = 0; var j = 0;
        if (compatible)
        {
            foreach (var a in targetMap.Extents)
            {
                while (j < keeperMap.Extents.Length && End(keeperMap.Extents[j]) <= a.Logical) j++;
                for (var k = j; k < keeperMap.Extents.Length && keeperMap.Extents[k].Logical < End(a); k++)
                {
                    var b = keeperMap.Extents[k]; var start = Math.Max(a.Logical, b.Logical);
                    var end = Math.Min((ulong)target.Size, Math.Min(End(a), End(b)));
                    if (end > start && a.Physical + start - a.Logical == b.Physical + start - b.Logical)
                        shared = checked(shared + (long)(end - start));
                }
            }
        }
        // Holes use no data blocks. Fully shared means every allocated target range already maps
        // to the keeper; it does NOT mean shared-with-a-snapshot can be ignored.
        var allocated = targetMap.Extents.Sum(e => (long)Math.Max(0, (decimal)Math.Min((ulong)target.Size, End(e)) - e.Logical));
        if (allocated == 0) return new("noAllocatedData", 0, 0, true, "未分配数据区块；文件本身仍有元数据开销");
        var exclusive = targetMap.Extents.Where(e => (e.Flags & 0x2000) == 0).Sum(e => checked((long)e.Length));
        var externalShared = targetMap.Extents.Any(e => (e.Flags & 0x2000) != 0);
        var full = compatible && shared == allocated;
        return new(full ? "fullyShared" : shared > 0 ? "partlyShared" : "independent", shared, exclusive,
            full || !externalShared, full ? "已与保留文件共用全部已分配数据区块" : externalShared ? "存在其他共享引用，可能包含快照，释放量无法完整确定" : null);
    }
    public static ulong End(Extent e) => checked(e.Logical + e.Length);
    public static long? ExclusiveEstimate(FileStamp stamp, ExtentMap map)
    {
        if (!map.Reliable || stamp.Links != 1) return null;
        // Shared references outside our authorized set cannot be counted as released.
        if (map.Extents.Any(e => (e.Flags & 0x2000) != 0)) return null;
        return map.Extents.Sum(e => checked((long)e.Length));
    }
    public static long? KnownExclusiveEstimate(FileStamp stamp, ExtentMap map) => !map.Reliable || stamp.Links != 1
        ? null : map.Extents.Where(e => (e.Flags & 0x2000) == 0).Sum(e => checked((long)e.Length));
    public static bool RangeShared(ExtentMap a, ExtentMap b, long offset, long length)
    {
        if (!a.Reliable || !b.Reliable) return false;
        var end = checked((ulong)(offset + length)); var cursor = (ulong)offset;
        foreach (var e in a.Extents)
        {
            if (End(e) <= cursor) continue;
            if (e.Logical >= end) break;
            // Gaps are holes; they do not require an ioctl.
            if (e.Logical > cursor) cursor = Math.Min(e.Logical, end);
            while (cursor < Math.Min(End(e), end))
            {
                var other = b.Extents.FirstOrDefault(x => x.Logical <= cursor && End(x) > cursor);
                if (other is null || e.Physical + cursor - e.Logical != other.Physical + cursor - other.Logical) return false;
                cursor = Math.Min(end, Math.Min(End(e), End(other)));
            }
        }
        return true;
    }
}
