using System.Collections.ObjectModel;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Desktop.Interaction;
using DesktopNotes.Domain;

namespace DesktopNotes.Desktop.ViewModels;

public sealed class TaskRowViewModel : ViewModelBase
{
    private bool _isExpanded;
    public RecordView Record { get; }
    public DeadlineStage Stage { get; }

    public Guid Id => Record.Id;
    public string DisplayTitle => Record.DisplayTitle;
    public string Body => Record.Body;
    public string? Assignee => Record.Effective.Assignee;
    public string? Publisher => Record.Effective.Publisher;
    public Priority Priority => Record.Effective.Priority;
    public bool IsSubtask => Record.ParentId.HasValue;

    public string StageLabel => Stage switch
    {
        DeadlineStage.DueToday => "今日到期",
        DeadlineStage.Overdue => "已逾期",
        DeadlineStage.Completed => "已完成",
        _ => string.Empty
    };

    public string DueSummary => Record.Effective.Due.Kind switch
    {
        DueKind.Date when Record.Effective.Due.Date is { } d =>
            string.IsNullOrEmpty(StageLabel) ? d.ToString("yyyy-MM-dd") : $"{d:yyyy-MM-dd} ({StageLabel})",
        DueKind.Instant when Record.Effective.Due.Instant is { } inst =>
            string.IsNullOrEmpty(StageLabel)
                ? inst.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : $"{inst.ToLocalTime():yyyy-MM-dd HH:mm} ({StageLabel})",
        _ => "无截止日期"
    };

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public TaskRowViewModel(RecordView record, TimeZoneInfo displayZone, DateTimeOffset nowUtc, bool isExpanded)
    {
        Record = record;
        Stage = DeadlineRules.EvaluateStage(record.Effective.Due, record.Status, nowUtc, displayZone);
        _isExpanded = isExpanded;
    }
}

public sealed class PanelViewModel : ViewModelBase
{
    private readonly IRecordService _recordService;
    private readonly IRecordQueryService _queryService;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeZoneInfo> _zoneProvider;
    private RecordKind _currentKind = RecordKind.Task;
    private string _searchText = string.Empty;
    private Guid? _expandedRecordId;
    private int _totalCount;
    private string _statusMessage = string.Empty;
    private string _quickInputText = string.Empty;
    private long _querySequence;
    private CancellationTokenSource? _searchDebounceCts;
    private IReadOnlyList<SortTerm> _currentSort = new[]
    {
        new SortTerm(SortKey.Priority, false),
        new SortTerm(SortKey.Deadline, false),
        new SortTerm(SortKey.Created, false)
    };

    public ObservableCollection<TaskRowViewModel> Items { get; } = new();
    public TaskRowInteraction RowInteraction { get; }
    public event Action<Guid>? OpenDetailRequested;

    public RecordKind CurrentKind
    {
        get => _currentKind;
        set
        {
            if (SetProperty(ref _currentKind, value))
            {
                ExpandedRecordId = null;
                _ = RefreshAsync(CancellationToken.None);
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _ = DebouncedSearchAsync(value);
            }
        }
    }

    public Guid? ExpandedRecordId
    {
        get => _expandedRecordId;
        set
        {
            if (SetProperty(ref _expandedRecordId, value))
            {
                foreach (var item in Items)
                {
                    item.IsExpanded = item.Id == value;
                }
            }
        }
    }

    public int TotalCount
    {
        get => _totalCount;
        private set => SetProperty(ref _totalCount, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string QuickInputText
    {
        get => _quickInputText;
        set => SetProperty(ref _quickInputText, value);
    }

    public IReadOnlyList<SortTerm> CurrentSort
    {
        get => _currentSort;
        set
        {
            _currentSort = value;
            RaisePropertyChanged();
            _ = RefreshAsync(CancellationToken.None);
        }
    }

    public PanelViewModel(
        IRecordService recordService,
        IRecordQueryService queryService,
        TimeProvider? timeProvider = null,
        Func<TimeZoneInfo>? zoneProvider = null,
        TimeSpan? doubleClickThresholdOverride = null)
    {
        _recordService = recordService ?? throw new ArgumentNullException(nameof(recordService));
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _zoneProvider = zoneProvider ?? (() => TimeZoneInfo.Local);

        RowInteraction = new TaskRowInteraction(
            () => ExpandedRecordId,
            id => ExpandedRecordId = id,
            id => OpenDetailRequested?.Invoke(id),
            doubleClickThresholdOverride);

        _recordService.RecordsChanged += (_, _) => _ = RefreshAsync(CancellationToken.None);
    }

    public RecordQuery BuildDefaultQuery(int skip = 0, int take = 100)
    {
        return new RecordQuery(
            _currentKind,
            RecordStatus.Active,
            false,
            _searchText,
            _currentSort,
            skip,
            take);
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        long seq = Interlocked.Increment(ref _querySequence);
        var query = BuildDefaultQuery(0, 100);
        var page = await _queryService.SearchAsync(query, ct);

        if (seq != Interlocked.Read(ref _querySequence))
        {
            return;
        }

        var zone = _zoneProvider();
        var now = _timeProvider.GetUtcNow();

        Items.Clear();
        foreach (var item in page.Items)
        {
            Items.Add(new TaskRowViewModel(item, zone, now, item.Id == _expandedRecordId));
        }

        TotalCount = page.Total;
    }

    public async Task LoadMoreAsync(CancellationToken ct)
    {
        if (Items.Count >= TotalCount)
        {
            return;
        }

        long seq = Interlocked.Read(ref _querySequence);
        int take = Math.Min(200, TotalCount - Items.Count);
        var query = BuildDefaultQuery(Items.Count, take);
        var page = await _queryService.SearchAsync(query, ct);

        if (seq != Interlocked.Read(ref _querySequence))
        {
            return;
        }

        var zone = _zoneProvider();
        var now = _timeProvider.GetUtcNow();
        foreach (var item in page.Items)
        {
            Items.Add(new TaskRowViewModel(item, zone, now, item.Id == _expandedRecordId));
        }

        TotalCount = page.Total;
    }

    public async Task ClearSearchAsync()
    {
        _searchDebounceCts?.Cancel();
        _searchText = string.Empty;
        RaisePropertyChanged(nameof(SearchText));
        await RefreshAsync(CancellationToken.None);
    }

    private async Task DebouncedSearchAsync(string text)
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;

        try
        {
            await Task.Delay(200, cts.Token);
            await RefreshAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer input
        }
    }

    public async Task<RecordView?> QuickCreateAsync(string titleOrBody, CancellationToken ct)
    {
        if (!RecordRules.HasValidText(titleOrBody, string.Empty))
        {
            StatusMessage = "标题或内容至少填写一项";
            return null;
        }

        var req = new SaveRecordRequest(
            Guid.NewGuid(),
            0,
            _currentKind,
            null,
            titleOrBody.Trim(),
            string.Empty,
            new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null),
            InheritField.None);

        var res = await _recordService.SaveAsync(req, ct);
        QuickInputText = string.Empty;
        StatusMessage = "已保存";
        await RefreshAsync(ct);
        return res.Changed.FirstOrDefault();
    }

    public async Task CompleteRecordAsync(Guid id, bool includeChildren, CancellationToken ct)
    {
        var rec = await _recordService.GetAsync(id, ct);
        if (rec is null) return;

        await _recordService.CloseAsync(id, rec.Version, includeChildren, ct);
        if (ExpandedRecordId == id)
        {
            ExpandedRecordId = null;
        }
        StatusMessage = rec.Kind == RecordKind.Task ? "已完成并归档" : "已归档";
        await RefreshAsync(ct);
    }
}
