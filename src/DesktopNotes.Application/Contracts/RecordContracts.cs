using DesktopNotes.Domain;

namespace DesktopNotes.Application.Contracts;

public static class ErrorCodes
{
    public const string Validation = "Validation";
    public const string Conflict = "Conflict";
    public const string ParentRequired = "ParentRequired";
    public const string ActiveChildren = "ActiveChildren";
    public const string AlreadyExists = "AlreadyExists";
    public const string NotFound = "NotFound";
    public const string PathUnavailable = "PathUnavailable";
    public const string InsufficientSpace = "InsufficientSpace";
    public const string SourceChanged = "SourceChanged";
    public const string UnsupportedCapability = "UnsupportedCapability";
    public const string Busy = "Busy";
}

public sealed class AppException : Exception
{
    public string Code { get; }
    public IReadOnlyList<Guid> AffectedIds { get; }
    public bool Retryable { get; }

    public AppException(
        string code,
        string message,
        IReadOnlyList<Guid>? affectedIds = null,
        bool retryable = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        AffectedIds = affectedIds ?? Array.Empty<Guid>();
        Retryable = retryable;
    }
}

public sealed record SaveRecordRequest(
    Guid Id,
    long ExpectedVersion,
    RecordKind Kind,
    Guid? ParentId,
    string Title,
    string Body,
    EffectiveFields Own,
    InheritField Inherit);

public sealed record RecordView(
    Guid Id,
    Guid? ParentId,
    RecordKind Kind,
    string Title,
    string Body,
    RecordStatus Status,
    EffectiveFields Own,
    EffectiveFields Effective,
    InheritField Inherit,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ClosedAt,
    DateTimeOffset? DeletedAt)
{
    public string DisplayTitle => DisplayName.Create(Title, Body);
}

public sealed record MutationResult(IReadOnlyList<RecordView> Changed);

public enum SortKey
{
    Priority = 0,
    Deadline = 1,
    Created = 2
}

public sealed record SortTerm(SortKey Key, bool Descending);

public sealed record RecordQuery(
    RecordKind Kind,
    RecordStatus? Status,
    bool Trash,
    string Text,
    IReadOnlyList<SortTerm> Sort,
    int Skip,
    int Take);

public sealed record Page<T>(IReadOnlyList<T> Items, int Total);

public enum CalendarAxis
{
    Due = 0,
    Start = 1,
    Created = 2,
    Closed = 3
}

public sealed record CalendarQuery(
    int Year,
    int Month,
    CalendarAxis Axis,
    RecordKind Kind,
    RecordStatus? Status);

public sealed record CalendarEntry(DateOnly? Day, RecordView Record);

public sealed record DraftView(
    string EditorId,
    Guid? RecordId,
    long? BaseVersion,
    string Title,
    string Body,
    DateTimeOffset UpdatedAt);

public interface IRecordService
{
    event EventHandler<IReadOnlyList<Guid>>? RecordsChanged;
    Task<MutationResult> SaveAsync(SaveRecordRequest request, CancellationToken ct);
    Task<MutationResult> CloseAsync(Guid id, long version, bool includeChildren, CancellationToken ct);
    Task<MutationResult> ReopenAsync(Guid id, long version, bool includeParent, CancellationToken ct);
    Task<MutationResult> TrashAsync(Guid id, long version, CancellationToken ct);
    Task<MutationResult> RestoreAsync(Guid id, long version, bool includeParent, CancellationToken ct);
    Task<RecordView?> GetAsync(Guid id, CancellationToken ct);
    Task SaveDraftAsync(string editorId, Guid? recordId, long? baseVersion, string title, string body, CancellationToken ct);
    Task<DraftView?> GetDraftAsync(string editorId, CancellationToken ct);
    Task DeleteDraftAsync(string editorId, CancellationToken ct);
}

public interface IRecordQueryService
{
    Task<Page<RecordView>> SearchAsync(RecordQuery query, CancellationToken ct);
    Task<IReadOnlyList<CalendarEntry>> CalendarAsync(CalendarQuery query, CancellationToken ct);
}
