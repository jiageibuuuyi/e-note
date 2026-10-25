using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Ports;

namespace DesktopNotes.Application.Records;

public sealed class RecordService : IRecordService
{
    private readonly IRecordRepositoryPort _repository;
    private readonly TimeProvider _timeProvider;
    private Func<TimeZoneInfo> _zoneProvider;

    public event EventHandler<IReadOnlyList<Guid>>? RecordsChanged;

    public RecordService(
        IRecordRepositoryPort repository,
        TimeProvider? timeProvider = null,
        Func<TimeZoneInfo>? zoneProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _zoneProvider = zoneProvider ?? (() => TimeZoneInfo.Local);
    }

    public void SetTimeZoneProvider(Func<TimeZoneInfo> zoneProvider)
    {
        _zoneProvider = zoneProvider ?? throw new ArgumentNullException(nameof(zoneProvider));
    }

    public Task<MutationResult> SaveAsync(SaveRecordRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var result = _repository.SaveRecord(request, _timeProvider.GetUtcNow(), _zoneProvider());
            NotifyChanged(result);
            return result;
        }, ct);
    }

    public Task<MutationResult> CloseAsync(Guid id, long version, bool includeChildren, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var result = _repository.CloseRecord(id, version, includeChildren, _timeProvider.GetUtcNow(), _zoneProvider());
            NotifyChanged(result);
            return result;
        }, ct);
    }

    public Task<MutationResult> ReopenAsync(Guid id, long version, bool includeParent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var result = _repository.ReopenRecord(id, version, includeParent, _timeProvider.GetUtcNow(), _zoneProvider());
            NotifyChanged(result);
            return result;
        }, ct);
    }

    public Task<MutationResult> TrashAsync(Guid id, long version, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var result = _repository.TrashRecord(id, version, _timeProvider.GetUtcNow(), _zoneProvider());
            NotifyChanged(result);
            return result;
        }, ct);
    }

    public Task<MutationResult> RestoreAsync(Guid id, long version, bool includeParent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var result = _repository.RestoreRecord(id, version, includeParent, _timeProvider.GetUtcNow(), _zoneProvider());
            NotifyChanged(result);
            return result;
        }, ct);
    }

    public Task<RecordView?> GetAsync(Guid id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run(() => _repository.GetRecord(id), ct);
    }

    public Task SaveDraftAsync(string editorId, Guid? recordId, long? baseVersion, string title, string body, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run(() => _repository.SaveDraft(editorId, recordId, baseVersion, title, body, _timeProvider.GetUtcNow()), ct);
    }

    public Task<DraftView?> GetDraftAsync(string editorId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run(() => _repository.GetDraft(editorId), ct);
    }

    public Task DeleteDraftAsync(string editorId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run(() => _repository.DeleteDraft(editorId), ct);
    }

    private void NotifyChanged(MutationResult result)
    {
        if (result.Changed.Count > 0)
        {
            var ids = result.Changed.Select(r => r.Id).ToArray();
            RecordsChanged?.Invoke(this, ids);
        }
    }
}
