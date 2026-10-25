using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Ports;

namespace DesktopNotes.Application.Queries;

public sealed class RecordQueryService : IRecordQueryService
{
    private readonly IRecordRepositoryPort _repository;
    private readonly Func<TimeZoneInfo> _zoneProvider;

    public RecordQueryService(
        IRecordRepositoryPort repository,
        Func<TimeZoneInfo>? zoneProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _zoneProvider = zoneProvider ?? (() => TimeZoneInfo.Local);
    }

    public Task<Page<RecordView>> SearchAsync(RecordQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return _repository.SearchRecords(query);
        }, ct);
    }

    public Task<IReadOnlyList<CalendarEntry>> CalendarAsync(CalendarQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return _repository.QueryCalendar(query, _zoneProvider());
        }, ct);
    }
}
