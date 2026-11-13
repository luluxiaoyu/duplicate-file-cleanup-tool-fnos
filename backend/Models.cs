using System.Text.Json.Serialization;

namespace DuplicateCleanup;

public static class AppInfo
{
    public const string Name = "duplicate-file-cleanup-tool-fnos";
    public const string Prefix = "/app/" + Name;
    public const string Trash = ".duplicate-file-cleanup-tool-fnos-trash";
    public const string Staging = ".duplicate-file-cleanup-tool-fnos-staging";
}
public sealed class AppError(string message, int status = 400, string? code = null) : Exception(message)
{
    public int Status { get; } = status;
    public string? Code { get; } = code;
}
public record GatewayUser(uint Uid, string Username);
public record ErrorResponse(string Error);
public record PathRequest(string Path);
public record ScanRequest(string[] Roots, string[]? PriorityRoots, string IdempotencyKey);
public record GroupChoice(long GroupId, long KeeperId, long[]? FileIds, bool Exclude = false, long[]? ExcludedFileIds = null);
public record CleanupRequest(string ScanId, string Method, bool AllGroups, GroupChoice[] Choices,
    string[]? PriorityRoots, bool Confirmed, string IdempotencyKey);
public record TrashRef(string TaskId, string OperationId);
public record TrashRequest(string Kind, TrashRef[] Items, bool Confirmed, string IdempotencyKey);
public record FileSystemInfo(string Type, string Identity, ulong MountId, ulong SubvolumeId,
    bool ReadOnly, bool Btrfs, bool HardlinkCandidate, string? Reason);
public record FolderInfo(string Path, string DisplayPath, FileSystemInfo? FileSystem, string? Error);
public record AclEntry(string Path, bool Readable, bool Writable, bool Deletable);
public record PathLabel(string Path, string SemanticPath);
public record AccessiblePaths(string[] Paths);
public record TrimReply(int Code, string? Msg, System.Text.Json.JsonElement Data);
public record TrimCall(string ReqId, string Req, string AppName, System.Text.Json.JsonElement Data);
public record FileStamp(ulong Device, ulong Inode, uint Mode, uint Uid, uint Gid, uint Links,
    long Size, ulong Blocks, long MtimeSec, uint MtimeNs, long CtimeSec, uint CtimeNs, ulong MountId)
{
    public bool SameContentVersion(FileStamp other) => Device == other.Device && Inode == other.Inode &&
        Size == other.Size && MtimeSec == other.MtimeSec && MtimeNs == other.MtimeNs &&
        CtimeSec == other.CtimeSec && CtimeNs == other.CtimeNs;
    public bool SameInode(FileStamp other) => Device == other.Device && Inode == other.Inode;
    public bool Regular => (Mode & 0xf000) == 0x8000;
    public bool Directory => (Mode & 0xf000) == 0x4000;
}
public record Extent(ulong Logical, ulong Physical, ulong Length, uint Flags);
public record ExtentMap(bool Reliable, Extent[] Extents, string? Reason);
public record SharingInfo(string State, long SharedBytes, long? ExclusiveBytes, bool Complete, string? Reason);
public record FileEntry(long Id, string Root, string Relative, int RootOrder, FileStamp Stamp,
    string? Sample, string? Hash, long? GroupId, SharingInfo? Sharing)
{
    public string Path => System.IO.Path.Combine(Root, Relative);
}
public record FileView(long Id, string DisplayPath, long Size, uint Links, string Sharing,
    long SharedBytes, long? ExclusiveBytes, FileSystemInfo? FileSystem, string? Reason);
public record DuplicateGroup(long Id, long KeeperId, long Count, long Size, FileView[] Files, long NextFileId, bool HasMoreFiles);
public record GroupsPage(DuplicateGroup[] Groups, long NextGroupId, bool HasMore);
public record FilePage(FileView[] Files, long NextFileId, bool HasMore);
public record TaskPage(Job[] Tasks, long NextOffset, bool HasMore);
public record TrashView(string TaskId, string OperationId, string DisplayPath, long Size, string State, string? Error);
public record TrashPage(TrashView[] Items, long NextOffset);
public sealed class SpaceMeasurement
{
    public required string FileSystemId { get; set; }
    public required string Root { get; set; }
    public long BeforeAvailable { get; set; }
    public long? AfterAvailable { get; set; }
    public long? DelayedAvailable { get; set; }
    public DateTimeOffset BeforeAt { get; set; }
    public DateTimeOffset? AfterAt { get; set; }
    public DateTimeOffset? DelayedAt { get; set; }
    public string? Error { get; set; }
}
public sealed class Job
{
    public required string Id { get; set; }
    public required uint Owner { get; set; }
    public required string Kind { get; set; }
    public string State { get; set; } = "queued";
    public string Stage { get; set; } = "等待执行";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string[] Roots { get; set; } = [];
    public string[] PriorityRoots { get; set; } = [];
    public string? ScanId { get; set; }
    public string? Method { get; set; }
    public long Discovered { get; set; }
    public long Processed { get; set; }
    public long? Total { get; set; }
    public long CurrentBytes { get; set; }
    public long CurrentTotal { get; set; }
    public long LogicalBytes { get; set; }
    public long MergedBytes { get; set; }
    public long AlreadySharedBytes { get; set; }
    public long EstimatedSavedBytes { get; set; }
    public bool EstimateComplete { get; set; } = true;
    public long TrashBytes { get; set; }
    public long Errors { get; set; }
    public string? Error { get; set; }
    public List<SpaceMeasurement> Space { get; set; } = [];
}
public sealed class Operation
{
    public required string Id { get; set; }
    public required FileEntry Target { get; set; }
    public FileEntry? Keeper { get; set; }
    public required string Method { get; set; }
    public string State { get; set; } = "pending";
    public string? HoldingRelative { get; set; }
    public string? LinkRelative { get; set; }
    public long Offset { get; set; }
    public long MergedBytes { get; set; }
    public long SharedBytes { get; set; }
    public long EstimatedSaved { get; set; }
    public bool EstimateComplete { get; set; }
    public string? Error { get; set; }
    public string? SourceTaskId { get; set; }
    public string? SourceOperationId { get; set; }
}
public record Preview(long Files, long LogicalBytes, long AlreadySharedBytes, long EstimatedSavedBytes,
    bool EstimateComplete, long Unavailable, string[] Reasons);
public record HealthResponse(string Status, string Version);
public record TaskIssue(string Id, string DisplayPath, string Message, string? RecoveryPath);
public record IssuesPage(TaskIssue[] Issues, string NextId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Job))]
[JsonSerializable(typeof(Operation))]
[JsonSerializable(typeof(FileEntry))]
[JsonSerializable(typeof(FileStamp))]
[JsonSerializable(typeof(SharingInfo))]
[JsonSerializable(typeof(ScanRequest))]
[JsonSerializable(typeof(CleanupRequest))]
[JsonSerializable(typeof(TrashRequest))]
[JsonSerializable(typeof(TrashRef))]
[JsonSerializable(typeof(PathRequest))]
[JsonSerializable(typeof(GatewayUser))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(FolderInfo[]))]
[JsonSerializable(typeof(AclEntry[]))]
[JsonSerializable(typeof(PathLabel[]))]
[JsonSerializable(typeof(AccessiblePaths))]
[JsonSerializable(typeof(TrimCall))]
[JsonSerializable(typeof(TrimReply))]
[JsonSerializable(typeof(GroupsPage))]
[JsonSerializable(typeof(FilePage))]
[JsonSerializable(typeof(TaskPage))]
[JsonSerializable(typeof(TrashPage))]
[JsonSerializable(typeof(Preview))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(IssuesPage))]
public partial class AppJson : JsonSerializerContext;
