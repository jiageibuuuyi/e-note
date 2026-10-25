using DesktopNotes.Application.Contracts;

namespace DesktopNotes.Application.Ports;

public interface IRecordRepositoryPort
{
    MutationResult SaveRecord(SaveRecordRequest request, DateTimeOffset nowUtc, TimeZoneInfo displayZone);
    MutationResult CloseRecord(Guid id, long expectedVersion, bool includeChildren, DateTimeOffset nowUtc, TimeZoneInfo displayZone);
    MutationResult ReopenRecord(Guid id, long expectedVersion, bool includeParent, DateTimeOffset nowUtc, TimeZoneInfo displayZone);
    MutationResult TrashRecord(Guid id, long expectedVersion, DateTimeOffset nowUtc, TimeZoneInfo displayZone);
    MutationResult RestoreRecord(Guid id, long expectedVersion, bool includeParent, DateTimeOffset nowUtc, TimeZoneInfo displayZone);
    RecordView? GetRecord(Guid id);
    Page<RecordView> SearchRecords(RecordQuery query);
    IReadOnlyList<CalendarEntry> QueryCalendar(CalendarQuery query, TimeZoneInfo displayZone);
    void SaveDraft(string editorId, Guid? recordId, long? baseVersion, string title, string body, DateTimeOffset nowUtc);
    DraftView? GetDraft(string editorId);
    void DeleteDraft(string editorId);
    void ReprojectAll(TimeZoneInfo displayZone);
}
