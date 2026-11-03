namespace DesktopNotes.Application.Contracts;

public enum ImportIntent
{
    Copy = 0,
    Move = 1
}

public enum ImportOrigin
{
    Drag = 0,
    Paste = 1,
    FilePicker = 2
}

public static class JobStates
{
    public const string Planned = "planned";
    public const string Copying = "copying";
    public const string Verified = "verified";
    public const string Published = "published";
    public const string Registered = "registered";
    public const string Cleaning = "cleaning";
    public const string Done = "done";
    public const string SourceCleanupPending = "source_cleanup_pending";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string NeedsReview = "needs_review";
}

public enum SourceCleanupOutcome
{
    Removed = 0,
    Changed = 1,
    Denied = 2,
    Uncertain = 3
}

public sealed record ImportRequest(
    Guid BatchId,
    Guid RecordId,
    IReadOnlyList<string> SourcePaths,
    ImportIntent Intent,
    ImportOrigin Origin);

public sealed record JobView(
    Guid Id,
    Guid BatchId,
    string State,
    long BytesCopied,
    long BytesTotal,
    string? ErrorCode);

public sealed record AttachmentView(
    Guid Id,
    Guid RecordId,
    Guid RootId,
    string RelativePath,
    string OriginalName,
    string EntryKind,
    string State,
    long BytesAtImport,
    DateTimeOffset CreatedAt,
    DateTimeOffset? TrashedAt);

public sealed record ClipboardFiles(
    IReadOnlyList<string> Paths,
    ImportIntent Intent,
    string ClipboardToken);

public interface ISourceCleanupPort
{
    string GetDeviceId(string path);
    string GetFileIdentity(string path);
    SourceCleanupOutcome RemoveVerifiedSource(string sourcePath, string expectedIdentity, string? expectedSha256);
    SourceCleanupOutcome RemoveEmptyVerifiedDirectory(string directoryPath, string expectedIdentity, IReadOnlySet<string> expectedChildRelativeNames);
}

public interface IAttachmentService
{
    string DefaultRootPath { get; }
    Task<IReadOnlyList<Guid>> EnqueueAsync(ImportRequest request, CancellationToken ct);
    Task<JobView> GetJobAsync(Guid jobId, CancellationToken ct);
    Task CancelAsync(Guid jobId, CancellationToken ct);
    Task RetryAsync(Guid jobId, CancellationToken ct);
    Task OpenAsync(Guid attachmentId, CancellationToken ct);
    Task TrashAttachmentAsync(Guid attachmentId, CancellationToken ct);
    Task RestoreAttachmentAsync(Guid attachmentId, CancellationToken ct);
    Task<IReadOnlyList<AttachmentView>> ListForRecordAsync(Guid recordId, bool includeTrashed, CancellationToken ct);
}

public interface IFileClipboard
{
    Task<ClipboardFiles?> ReadFilesAsync(CancellationToken ct);
    Task AcknowledgeMoveAsync(string clipboardToken, CancellationToken ct);
}
