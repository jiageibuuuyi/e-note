using DesktopNotes.Application.Contracts;

namespace DesktopNotes.Application.Ports;

public sealed record StorageRootRecord(
    Guid Id,
    string AbsolutePath,
    string Platform,
    long CreatedUtcMs);

public sealed record AttachmentJobRecord(
    Guid Id,
    Guid BatchId,
    Guid RecordId,
    Guid AttachmentId,
    Guid RootId,
    string Operation,
    string SourcePath,
    string TargetRelativePath,
    string StagingRelativePath,
    string State,
    string SourceDeviceId,
    string? ErrorCode,
    long CreatedUtcMs,
    long UpdatedUtcMs);

public sealed record AttachmentJobItemRecord(
    Guid JobId,
    string RelativePath,
    string EntryKind,
    string SourceIdentity,
    long SourceSize,
    long SourceMtimeNs,
    string? Sha256,
    bool Verified,
    bool SourceRemoved);

public interface IAttachmentRepositoryPort
{
    StorageRootRecord EnsureDefaultStorageRoot(string defaultRootPath, string platform, DateTimeOffset nowUtc);
    StorageRootRecord RegisterStorageRoot(Guid id, string absolutePath, string platform, DateTimeOffset nowUtc);
    StorageRootRecord? GetStorageRoot(Guid id);
    IReadOnlyList<StorageRootRecord> ListStorageRoots();
    Guid ResolveAttachmentRootForRecord(Guid recordId, Guid defaultRootId);

    void CreatePlannedJob(AttachmentJobRecord job, IReadOnlyList<AttachmentJobItemRecord> items);
    void ReplaceJobManifest(Guid jobId, IReadOnlyList<AttachmentJobItemRecord> items, string newState, DateTimeOffset nowUtc);
    void UpdateJobState(Guid jobId, string state, string? errorCode, DateTimeOffset nowUtc);
    void MarkJobItemsVerified(Guid jobId, IReadOnlyDictionary<string, string?> fileHashes, string newState, DateTimeOffset nowUtc);
    void MarkJobItemSourceRemoved(Guid jobId, string relativePath);
    void RegisterPublishedAttachment(
        Guid jobId,
        AttachmentView attachment,
        string nextJobState,
        DateTimeOffset nowUtc);

    AttachmentJobRecord? GetJobRecord(Guid jobId);
    IReadOnlyList<AttachmentJobItemRecord> GetJobItems(Guid jobId);
    IReadOnlyList<AttachmentJobRecord> GetIncompleteJobs();

    AttachmentView? GetAttachment(Guid attachmentId);
    IReadOnlyList<AttachmentView> ListAttachmentsForRecord(Guid recordId, bool includeTrashed);
    void SetAttachmentTrashState(Guid attachmentId, DateTimeOffset? trashedAtUtc);
    void SetAttachmentState(Guid attachmentId, string state);
}
