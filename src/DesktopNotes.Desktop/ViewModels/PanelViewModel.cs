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
    public bool HasBody => !string.IsNullOrWhiteSpace(Body);
    public bool HasAssignee => !string.IsNullOrWhiteSpace(Assignee);

    public string StageLabel => Stage switch
    {
        DeadlineStage.DueToday => "今日",
        DeadlineStage.Overdue => "逾期",
        DeadlineStage.Completed => "已完成",
        _ => string.Empty
    };

    public bool HasStageLabel => !string.IsNullOrEmpty(StageLabel);
    public bool IsOverdue => Stage == DeadlineStage.Overdue;
    public bool IsDueToday => Stage == DeadlineStage.DueToday;

    public string PriorityBadge => Priority switch
    {
        Priority.Highest => "P0",
        Priority.High => "P1",
        _ => string.Empty
    };

    public bool HasPriorityBadge => !string.IsNullOrEmpty(PriorityBadge);

    public string DueSummary => Record.Effective.Due.Kind switch
    {
        DueKind.Date when Record.Effective.Due.Date is { } d =>
            string.IsNullOrEmpty(StageLabel) ? d.ToString("yyyy-MM-dd") : $"{d:yyyy-MM-dd} · {StageLabel}",
        DueKind.Instant when Record.Effective.Due.Instant is { } inst =>
            string.IsNullOrEmpty(StageLabel)
                ? inst.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : $"{inst.ToLocalTime():yyyy-MM-dd HH:mm} · {StageLabel}",
        _ => "无截止期"
    };

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    private bool _isEditingInline;
    private string _inlineEditText = string.Empty;

    public bool IsEditingInline
    {
        get => _isEditingInline;
        set
        {
            if (SetProperty(ref _isEditingInline, value))
            {
                RaisePropertyChanged(nameof(IsDisplayMode));
            }
        }
    }

    public bool IsDisplayMode => !_isEditingInline;

    public string InlineEditText
    {
        get => _inlineEditText;
        set => SetProperty(ref _inlineEditText, value);
    }

    public static TaskRowViewModel CreateInlineDraft(RecordKind kind, TimeZoneInfo displayZone, DateTimeOffset nowUtc)
    {
        var emptyFields = new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null);
        var draftRecord = new RecordView(
            Guid.NewGuid(),
            null,
            kind,
            string.Empty,
            string.Empty,
            RecordStatus.Active,
            emptyFields,
            emptyFields,
            InheritField.None,
            0,
            nowUtc,
            null,
            null);
        return new TaskRowViewModel(draftRecord, displayZone, nowUtc, false)
        {
            IsEditingInline = true
        };
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
    private bool _isSearchOpen;
    private Guid? _expandedRecordId;
    private int _totalCount;
    private string _statusMessage = "双击任务编辑详情 · 拖入文件自动归档";
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

    public bool IsTaskTab => _currentKind == RecordKind.Task;
    public bool IsNoteTab => _currentKind == RecordKind.Note;
    public string CurrentKindText => _currentKind == RecordKind.Task ? "待办" : "笔记";
    public string KindIconData => _currentKind == RecordKind.Task
        ? "M 9 16.17 L 4.83 12 L 3.41 13.41 L 9 19 L 21 7 L 19.59 5.59 L 9 16.17 Z"
        : "M 3 17.25 L 3 21 L 6.75 21 L 17.81 9.94 L 14.06 6.19 L 3 17.25 Z M 20.71 7.04 L 18.37 4.7 L 16.54 6.53 L 20.29 10.28 L 20.71 7.04 Z";
    public bool HasSearchText => !string.IsNullOrEmpty(_searchText);
    public bool IsEmpty => Items.Count == 0;
    public string WhisperStatus => HasSearchText
        ? $"搜索到 {TotalCount} 项匹配"
        : (TotalCount > 0 ? $"{TotalCount} 项{CurrentKindText}" : string.Empty);
    public bool HasFooterContent => !string.IsNullOrEmpty(WhisperStatus);

    public string QuickInputPlaceholder => IsTaskTab
        ? "添加待办，按 Enter 创建..."
        : "记录灵感或笔记，按 Enter 创建...";

    public string EmptyStateTitle => HasSearchText
        ? "未找到匹配内容"
        : (IsTaskTab ? "今天还没有待办事项" : "还没有笔记记录");

    public string EmptyStateSubtitle => HasSearchText
        ? "尝试更换关键词，或按 Esc 清空搜索"
        : "点击 + 号在列表末尾直接新增一行输入";

    public bool IsSearchOpen
    {
        get => _isSearchOpen || HasSearchText;
        set => SetProperty(ref _isSearchOpen, value);
    }

    public TaskRowViewModel StartInlineCreate()
    {
        var existing = Items.FirstOrDefault(x => x.IsEditingInline);
        if (existing is not null)
        {
            return existing;
        }

        var draft = TaskRowViewModel.CreateInlineDraft(
            _currentKind,
            _zoneProvider(),
            _timeProvider.GetUtcNow());
        Items.Add(draft);
        RaisePropertyChanged(nameof(IsEmpty));
        return draft;
    }

    public async Task CommitInlineCreateAsync(TaskRowViewModel draft, CancellationToken ct)
    {
        if (!draft.IsEditingInline)
        {
            return;
        }

        draft.IsEditingInline = false;
        string text = draft.InlineEditText?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            Items.Remove(draft);
            RaisePropertyChanged(nameof(IsEmpty));
            return;
        }

        await QuickCreateAsync(text, ct);
    }

    public void CancelInlineCreate(TaskRowViewModel draft)
    {
        draft.IsEditingInline = false;
        Items.Remove(draft);
        RaisePropertyChanged(nameof(IsEmpty));
    }

    public void ToggleKind()
    {
        CurrentKind = _currentKind == RecordKind.Task ? RecordKind.Note : RecordKind.Task;
    }

    public RecordKind CurrentKind
    {
        get => _currentKind;
        set
        {
            if (SetProperty(ref _currentKind, value))
            {
                ExpandedRecordId = null;
                RaisePropertyChanged(nameof(IsTaskTab));
                RaisePropertyChanged(nameof(IsNoteTab));
                RaisePropertyChanged(nameof(CurrentKindText));
                RaisePropertyChanged(nameof(KindIconData));
                RaisePropertyChanged(nameof(WhisperStatus));
                RaisePropertyChanged(nameof(HasFooterContent));
                RaisePropertyChanged(nameof(QuickInputPlaceholder));
                RaisePropertyChanged(nameof(EmptyStateTitle));
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
                RaisePropertyChanged(nameof(HasSearchText));
                RaisePropertyChanged(nameof(IsSearchOpen));
                RaisePropertyChanged(nameof(EmptyStateTitle));
                RaisePropertyChanged(nameof(EmptyStateSubtitle));
                RaisePropertyChanged(nameof(WhisperStatus));
                RaisePropertyChanged(nameof(HasFooterContent));
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
        private set
        {
            if (SetProperty(ref _totalCount, value))
            {
                RaisePropertyChanged(nameof(WhisperStatus));
                RaisePropertyChanged(nameof(HasFooterContent));
            }
        }
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
        RaisePropertyChanged(nameof(IsEmpty));
        RaisePropertyChanged(nameof(EmptyStateTitle));
        RaisePropertyChanged(nameof(EmptyStateSubtitle));
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
        RaisePropertyChanged(nameof(IsEmpty));
    }

    public async Task ClearSearchAsync()
    {
        _searchDebounceCts?.Cancel();
        _searchText = string.Empty;
        _isSearchOpen = false;
        RaisePropertyChanged(nameof(SearchText));
        RaisePropertyChanged(nameof(HasSearchText));
        RaisePropertyChanged(nameof(IsSearchOpen));
        RaisePropertyChanged(nameof(EmptyStateTitle));
        RaisePropertyChanged(nameof(EmptyStateSubtitle));
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
            StatusMessage = "请输入内容后再按 Enter 创建";
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
        StatusMessage = "已创建";
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
