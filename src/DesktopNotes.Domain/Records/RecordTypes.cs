namespace DesktopNotes.Domain;

public enum RecordKind
{
    Task = 0,
    Note = 1
}

public enum RecordStatus
{
    Active = 0,
    Closed = 1
}

public enum Priority
{
    Highest = 0,
    High = 1,
    Normal = 2,
    Low = 3
}

public enum DueKind
{
    None = 0,
    Date = 1,
    Instant = 2
}

public enum DeadlineStage
{
    None = 0,
    Normal = 1,
    DueToday = 2,
    Overdue = 3,
    Completed = 4
}

[Flags]
public enum InheritField
{
    None = 0,
    Publisher = 1,
    Assignee = 2,
    Priority = 4,
    Start = 8,
    Due = 16,
    AttachmentRoot = 32,
    All = 63
}

public sealed record DueSpec(
    DueKind Kind,
    DateOnly? Date,
    DateTimeOffset? Instant,
    string? ZoneId)
{
    public static DueSpec Empty => new(DueKind.None, null, null, null);
}

public sealed record EffectiveFields(
    string? Publisher,
    string? Assignee,
    Priority Priority,
    DateTimeOffset? Start,
    DueSpec Due,
    Guid? AttachmentRootId);
