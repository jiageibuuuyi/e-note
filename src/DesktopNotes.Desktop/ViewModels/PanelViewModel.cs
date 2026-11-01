using System.Collections.ObjectModel;
using System.Text.Json;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Ports;
using DesktopNotes.Desktop.Interaction;
using DesktopNotes.Domain;

namespace DesktopNotes.Desktop.ViewModels;

public sealed class TaskRowViewModel : ViewModelBase
{
    private bool _isExpanded;
    private string _editableTitle;
    public RecordView Record { get; }
    public DeadlineStage Stage { get; }

    public Guid Id => Record.Id;
    public string DisplayTitle => Record.DisplayTitle;
    public string EditableTitle
    {
        get => _editableTitle;
        set
        {
            if (SetProperty(ref _editableTitle, value))
            {
                RaisePropertyChanged(nameof(IsTitleDirty));
            }
        }
    }
    public bool IsTitleDirty => !string.Equals(
        _editableTitle?.Trim() ?? string.Empty,
        (!string.IsNullOrWhiteSpace(Record.Title) ? Record.Title : Record.DisplayTitle).Trim(),
        StringComparison.Ordinal);
    public string Body => Record.Body;
    public string? Assignee => Record.Effective.Assignee;
    public string? Publisher => Record.Effective.Publisher;
    public Priority Priority => Record.Effective.Priority;
    public bool IsSubtask => Record.ParentId.HasValue;
    public bool CanAddSubtask => Record.Kind == RecordKind.Task && !Record.ParentId.HasValue;
    public bool IsTaskRow => Record.Kind == RecordKind.Task && !IsEditingInline;
    public int SubtaskCount { get; }
    public bool HasSubtasks => SubtaskCount > 0;
    public string SubtaskCountText => SubtaskCount > 0 ? $"{SubtaskCount} 子任务" : string.Empty;
    public bool IsTreeExpanded { get; }
    public string TreeToggleGlyph => IsTreeExpanded ? "▾" : "▸";
    public string TreeToggleTooltip => IsTreeExpanded ? "点击折叠子任务树" : "点击展开子任务树";
    public bool IsLastSubtask { get; }
    public string TreeBranchPrefix => IsLastSubtask ? "└─ " : "├─ ";
    public Avalonia.Thickness RowMargin => IsSubtask ? new(22, 1.5, 0, 1.5) : new(0, 3.5, 0, 3.5);
    public string InlinePlaceholder => IsSubtask
        ? "输入子任务内容后按 Enter 保存，Esc 取消..."
        : "输入内容后按 Enter 保存，Esc 取消...";
    public bool HasBody => !string.IsNullOrWhiteSpace(Body);
    public bool HasAssignee => !string.IsNullOrWhiteSpace(Assignee);

    public string StageLabel => IsMarkedCompleted
        ? "✓ 已完成"
        : Stage switch
        {
            DeadlineStage.DueToday => "今日",
            DeadlineStage.Overdue => "逾期",
            DeadlineStage.Completed => "已完成",
            _ => string.Empty
        };

    public bool HasStageLabel => !string.IsNullOrEmpty(StageLabel);
    public bool IsOverdue => !IsMarkedCompleted && Stage == DeadlineStage.Overdue;
    public bool IsDueToday => !IsMarkedCompleted && Stage == DeadlineStage.DueToday;

    public string PriorityBadge => IsMarkedCompleted
        ? string.Empty
        : Priority switch
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

    private bool _isMarkedCompleted;
    private bool _isEditingInline;
    private string _inlineEditText = string.Empty;

    public bool IsMarkedCompleted
    {
        get => _isMarkedCompleted;
        set
        {
            if (SetProperty(ref _isMarkedCompleted, value))
            {
                RaisePropertyChanged(nameof(IsDisplayNormal));
                RaisePropertyChanged(nameof(IsDisplayCompleted));
                RaisePropertyChanged(nameof(StageLabel));
                RaisePropertyChanged(nameof(HasStageLabel));
                RaisePropertyChanged(nameof(IsOverdue));
                RaisePropertyChanged(nameof(IsDueToday));
                RaisePropertyChanged(nameof(PriorityBadge));
                RaisePropertyChanged(nameof(HasPriorityBadge));
                RaisePropertyChanged(nameof(DueSummary));
                RaisePropertyChanged(nameof(CompleteMenuHeader));
            }
        }
    }

    public bool IsEditingInline
    {
        get => _isEditingInline;
        set
        {
            if (SetProperty(ref _isEditingInline, value))
            {
                RaisePropertyChanged(nameof(IsDisplayMode));
                RaisePropertyChanged(nameof(IsDisplayNormal));
                RaisePropertyChanged(nameof(IsDisplayCompleted));
            }
        }
    }

    public bool IsDisplayMode => !_isEditingInline;
    public bool IsDisplayNormal => !_isEditingInline && !_isMarkedCompleted;
    public bool IsDisplayCompleted => !_isEditingInline && _isMarkedCompleted;
    public string CompleteMenuHeader => _isMarkedCompleted ? "↩️  取消完成标记" : "✅  标记为已完成";

    public string InlineEditText
    {
        get => _inlineEditText;
        set => SetProperty(ref _inlineEditText, value);
    }

    public static TaskRowViewModel CreateInlineDraft(
        RecordKind kind,
        TimeZoneInfo displayZone,
        DateTimeOffset nowUtc,
        Guid? parentId = null)
    {
        var emptyFields = new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null);
        var draftRecord = new RecordView(
            Guid.NewGuid(),
            parentId,
            kind,
            string.Empty,
            string.Empty,
            RecordStatus.Active,
            emptyFields,
            emptyFields,
            parentId.HasValue ? InheritField.All : InheritField.None,
            0,
            nowUtc,
            null,
            null);
        return new TaskRowViewModel(draftRecord, displayZone, nowUtc, false)
        {
            IsEditingInline = true
        };
    }

    public TaskRowViewModel(
        RecordView record,
        TimeZoneInfo displayZone,
        DateTimeOffset nowUtc,
        bool isExpanded,
        bool isMarkedCompleted = false,
        int subtaskCount = 0,
        bool isTreeExpanded = true,
        bool isLastSubtask = false)
    {
        Record = record;
        _editableTitle = !string.IsNullOrWhiteSpace(record.Title) ? record.Title : record.DisplayTitle;
        Stage = DeadlineRules.EvaluateStage(record.Effective.Due, record.Status, nowUtc, displayZone);
        _isExpanded = isExpanded;
        _isMarkedCompleted = isMarkedCompleted;
        SubtaskCount = subtaskCount;
        IsTreeExpanded = isTreeExpanded;
        IsLastSubtask = isLastSubtask;
    }
}

public sealed class PanelViewModel : ViewModelBase
{
    private const string MarkedCompletedSettingKey = "panel.marked_completed_ids";
    private readonly IRecordService _recordService;
    private readonly IRecordQueryService _queryService;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeZoneInfo> _zoneProvider;
    private readonly HashSet<Guid> _markedCompletedIds = new();
    private readonly HashSet<Guid> _collapsedTreeParentIds = new();
    private bool _markedCompletedLoaded;
    private RecordKind _currentKind = RecordKind.Task;
    private string _searchText = string.Empty;
    private bool _isSearchOpen;
    private Guid? _expandedRecordId;
    private int _totalCount;
    private string _statusMessage = "双击任务编辑详情 · 拖入文件自动归档";
    private string? _transientNotice;
    private string _quickInputText = string.Empty;
    private long _querySequence;
    private CancellationTokenSource? _searchDebounceCts;
    private IReadOnlyList<SortTerm> _currentSort = new[]
    {
        new SortTerm(SortKey.Priority, false),
        new SortTerm(SortKey.Deadline, false),
        new SortTerm(SortKey.Created, false)
    };

    public IAttachmentService? AttachmentService { get; set; }
    public ISettingsRepositoryPort? SettingsRepository { get; set; }
    public ObservableCollection<TaskRowViewModel> Items { get; } = new();
    public TaskRowInteraction RowInteraction { get; }
    public event Action<Guid>? OpenDetailRequested;

    public int CompletedPendingArchiveCount => Items.Count(x => x.IsMarkedCompleted);
    public bool HasCompletedPendingArchive => CompletedPendingArchiveCount > 0;
    public string ArchiveCompletedButtonText => $"📦 归档已完成 ({CompletedPendingArchiveCount})";

    public bool IsTaskTab => _currentKind == RecordKind.Task;
    public bool IsNoteTab => _currentKind == RecordKind.Note;
    public string CurrentKindText => _currentKind == RecordKind.Task ? "待办" : "笔记";
    public string KindIconData => _currentKind == RecordKind.Task
        ? "M 9 16.17 L 4.83 12 L 3.41 13.41 L 9 19 L 21 7 L 19.59 5.59 L 9 16.17 Z"
        : "M 3 17.25 L 3 21 L 6.75 21 L 17.81 9.94 L 14.06 6.19 L 3 17.25 Z M 20.71 7.04 L 18.37 4.7 L 16.54 6.53 L 20.29 10.28 L 20.71 7.04 Z";
    public bool HasSearchText => !string.IsNullOrEmpty(_searchText);
    public bool IsEmpty => Items.Count == 0;
    public string WhisperStatus => !string.IsNullOrEmpty(_transientNotice)
        ? _transientNotice
        : (HasSearchText
            ? $"搜索到 {TotalCount} 项匹配"
            : (TotalCount > 0 ? $"{TotalCount} 项{CurrentKindText}" : string.Empty));
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

    public TaskRowViewModel StartInlineSubtaskCreate(Guid parentId)
    {
        _collapsedTreeParentIds.Remove(parentId);
        var existing = Items.FirstOrDefault(x => x.IsEditingInline);
        if (existing is not null)
        {
            Items.Remove(existing);
        }

        var draft = TaskRowViewModel.CreateInlineDraft(
            RecordKind.Task,
            _zoneProvider(),
            _timeProvider.GetUtcNow(),
            parentId);

        int insertIndex = Items.Count;
        for (int i = 0; i < Items.Count; i++)
        {
            if (Items[i].Id == parentId)
            {
                insertIndex = i + 1;
                while (insertIndex < Items.Count && Items[insertIndex].Record.ParentId == parentId)
                {
                    insertIndex++;
                }
                break;
            }
        }

        Items.Insert(insertIndex, draft);
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

        if (draft.Record.ParentId is { } parentId)
        {
            await QuickCreateSubtaskAsync(parentId, text, ct);
        }
        else
        {
            await QuickCreateAsync(text, ct);
        }
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

    public void SetSortDefault()
    {
        SetTransientNotice("排序: 默认 (优先级 → 截止期 → 创建时间)");
        CurrentSort = new[]
        {
            new SortTerm(SortKey.Priority, false),
            new SortTerm(SortKey.Deadline, false),
            new SortTerm(SortKey.Created, false)
        };
    }

    public void SetSortByDeadline()
    {
        SetTransientNotice("排序: 截止期优先 (最近到期在前)");
        CurrentSort = new[]
        {
            new SortTerm(SortKey.Deadline, false),
            new SortTerm(SortKey.Priority, false),
            new SortTerm(SortKey.Created, false)
        };
    }

    public void SetSortByPriority()
    {
        SetTransientNotice("排序: 优先级优先 (紧急在前)");
        CurrentSort = new[]
        {
            new SortTerm(SortKey.Priority, false),
            new SortTerm(SortKey.Created, true)
        };
    }

    public void SetSortByCreatedDesc()
    {
        SetTransientNotice("排序: 最新创建在前");
        CurrentSort = new[]
        {
            new SortTerm(SortKey.Created, true),
            new SortTerm(SortKey.Priority, false)
        };
    }

    public async Task DropFilesOnRecordAsync(Guid recordId, IReadOnlyList<string> filePaths, CancellationToken ct)
    {
        if (AttachmentService is null || filePaths.Count == 0)
        {
            return;
        }

        try
        {
            var req = new ImportRequest(
                Guid.NewGuid(),
                recordId,
                filePaths,
                ImportIntent.Copy,
                ImportOrigin.FilePicker);
            await AttachmentService.EnqueueAsync(req, ct);
            SetTransientNotice($"已复制关联 {filePaths.Count} 个附件（源文件保留）");
        }
        catch (Exception ex)
        {
            SetTransientNotice($"拖入失败: {ex.Message}");
        }
    }

    public void SetTransientNotice(string message)
    {
        _transientNotice = message;
        RaisePropertyChanged(nameof(WhisperStatus));
        RaisePropertyChanged(nameof(HasFooterContent));
        _ = Task.Run(async () =>
        {
            await Task.Delay(3500);
            if (_transientNotice == message)
            {
                _transientNotice = null;
                RaisePropertyChanged(nameof(WhisperStatus));
                RaisePropertyChanged(nameof(HasFooterContent));
            }
        });
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
        EnsureMarkedCompletedLoaded();

        // Preserve any active inline editing row so background refreshes never discard user input
        var activeInlineDraft = Items.FirstOrDefault(x => x.IsEditingInline);

        Items.Clear();

        // Keep subtasks grouped directly underneath their parent task when parent is present in active list
        var childrenByParent = page.Items
            .Where(x => x.ParentId.HasValue)
            .GroupBy(x => x.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        var rootIds = page.Items
            .Where(x => !x.ParentId.HasValue)
            .Select(x => x.Id)
            .ToHashSet();

        foreach (var item in page.Items)
        {
            if (item.ParentId is { } pid && rootIds.Contains(pid))
            {
                continue;
            }

            List<RecordView>? subtasks = null;
            bool hasChildren = !item.ParentId.HasValue && childrenByParent.TryGetValue(item.Id, out subtasks);
            int childCount = hasChildren && subtasks is not null ? subtasks.Count : 0;
            bool isTreeExpanded = !_collapsedTreeParentIds.Contains(item.Id);

            Items.Add(new TaskRowViewModel(
                item,
                zone,
                now,
                item.Id == _expandedRecordId,
                _markedCompletedIds.Contains(item.Id),
                childCount,
                isTreeExpanded,
                isLastSubtask: false));

            if (hasChildren && subtasks is not null && isTreeExpanded)
            {
                for (int i = 0; i < subtasks.Count; i++)
                {
                    var sub = subtasks[i];
                    bool isLast = i == subtasks.Count - 1 &&
                                  (activeInlineDraft is null || activeInlineDraft.Record.ParentId != item.Id);
                    Items.Add(new TaskRowViewModel(
                        sub,
                        zone,
                        now,
                        sub.Id == _expandedRecordId,
                        _markedCompletedIds.Contains(sub.Id),
                        subtaskCount: 0,
                        isTreeExpanded: true,
                        isLastSubtask: isLast));
                }
            }

            if (activeInlineDraft is not null && activeInlineDraft.Record.ParentId == item.Id)
            {
                Items.Add(activeInlineDraft);
                activeInlineDraft = null;
            }
        }

        if (activeInlineDraft is not null)
        {
            Items.Add(activeInlineDraft);
        }

        TotalCount = page.Total;
        RaiseCompletedArchiveProperties();
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

    public async Task<RecordView?> QuickCreateSubtaskAsync(Guid parentId, string titleOrBody, CancellationToken ct)
    {
        if (!RecordRules.HasValidText(titleOrBody, string.Empty))
        {
            return null;
        }

        var req = new SaveRecordRequest(
            Guid.NewGuid(),
            0,
            RecordKind.Task,
            parentId,
            titleOrBody.Trim(),
            string.Empty,
            new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null),
            InheritField.All);

        var res = await _recordService.SaveAsync(req, ct);
        SetTransientNotice("已添加子任务 (默认继承父任务属性)");
        await RefreshAsync(ct);
        return res.Changed.FirstOrDefault();
    }

    public async Task SaveEditedTitleAsync(TaskRowViewModel row, CancellationToken ct)
    {
        if (row.IsEditingInline || !row.IsTitleDirty)
        {
            return;
        }

        string newTitle = row.EditableTitle?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(newTitle))
        {
            // Restore original title if cleared completely
            row.EditableTitle = !string.IsNullOrWhiteSpace(row.Record.Title) ? row.Record.Title : row.Record.DisplayTitle;
            return;
        }

        var existing = await _recordService.GetAsync(row.Id, ct);
        if (existing is null || existing.Status != RecordStatus.Active)
        {
            return;
        }

        var req = new SaveRecordRequest(
            existing.Id,
            existing.Version,
            existing.Kind,
            existing.ParentId,
            newTitle,
            existing.Body,
            existing.Own,
            existing.Inherit);

        await _recordService.SaveAsync(req, ct);
        SetTransientNotice("✓ 标题已更新");
        await RefreshAsync(ct);
    }

    public async Task ToggleTreeCollapseAsync(Guid parentId, CancellationToken ct)
    {
        if (!_collapsedTreeParentIds.Remove(parentId))
        {
            _collapsedTreeParentIds.Add(parentId);
        }

        await RefreshAsync(ct);
    }

    public async Task ConvertTaskToSubtaskAsync(Guid sourceTaskId, Guid targetTaskId, CancellationToken ct)
    {
        if (sourceTaskId == targetTaskId)
        {
            return;
        }

        var source = await _recordService.GetAsync(sourceTaskId, ct);
        var target = await _recordService.GetAsync(targetTaskId, ct);
        if (source is null || target is null)
        {
            return;
        }

        if (source.Kind != RecordKind.Task || target.Kind != RecordKind.Task)
        {
            SetTransientNotice("仅待办任务支持拖入设为子任务");
            return;
        }

        if (source.Status != RecordStatus.Active || target.Status != RecordStatus.Active)
        {
            return;
        }

        // If dragged onto an existing subtask, attach to that subtask's root parent task
        Guid newParentId = target.ParentId ?? target.Id;
        if (newParentId == source.Id)
        {
            return;
        }

        if (source.ParentId == newParentId)
        {
            SetTransientNotice("该任务已经是此父任务下的子任务");
            return;
        }

        try
        {
            // If source itself had active subtasks, move those subtasks under newParentId as well so tree stays valid
            var sourceChildren = Items
                .Where(x => !x.IsEditingInline && x.Record.ParentId == source.Id)
                .Select(x => x.Id)
                .ToList();

            foreach (var childId in sourceChildren)
            {
                if (childId == newParentId) continue;
                var childRec = await _recordService.GetAsync(childId, ct);
                if (childRec is not null && childRec.Status == RecordStatus.Active)
                {
                    var childInherit = ComputeSubtaskInheritanceMask(childRec);
                    var childReq = new SaveRecordRequest(
                        childRec.Id,
                        childRec.Version,
                        RecordKind.Task,
                        newParentId,
                        childRec.Title,
                        childRec.Body,
                        childRec.Own,
                        childInherit);
                    await _recordService.SaveAsync(childReq, ct);
                }
            }

            // Re-fetch source in case version changed, then reparent source under newParentId:
            // Existing attributes on source are preserved (Inherit bit = 0); unset attributes inherit from parent (Inherit bit = 1)
            source = await _recordService.GetAsync(sourceTaskId, ct);
            if (source is null)
            {
                return;
            }

            var inheritMask = ComputeSubtaskInheritanceMask(source);
            var req = new SaveRecordRequest(
                source.Id,
                source.Version,
                RecordKind.Task,
                newParentId,
                source.Title,
                source.Body,
                source.Own,
                inheritMask);

            _collapsedTreeParentIds.Remove(newParentId);
            await _recordService.SaveAsync(req, ct);
            SetTransientNotice($"↳ 已设为子任务（保留自身已有属性，未设属性继承父任务）");
            await RefreshAsync(ct);
        }
        catch (Exception ex)
        {
            SetTransientNotice($"设为子任务失败: {ex.Message}");
        }
    }

    public async Task PromoteSubtaskToRootAsync(Guid subtaskId, CancellationToken ct)
    {
        var existing = await _recordService.GetAsync(subtaskId, ct);
        if (existing is null || !existing.ParentId.HasValue || existing.Status != RecordStatus.Active)
        {
            return;
        }

        try
        {
            var req = new SaveRecordRequest(
                existing.Id,
                existing.Version,
                existing.Kind,
                null,
                existing.Title,
                existing.Body,
                existing.Effective,
                InheritField.None);

            await _recordService.SaveAsync(req, ct);
            SetTransientNotice("⬆️ 已提升为独立主任务");
            await RefreshAsync(ct);
        }
        catch (Exception ex)
        {
            SetTransientNotice($"提升主任务失败: {ex.Message}");
        }
    }

    private static InheritField ComputeSubtaskInheritanceMask(RecordView record)
    {
        // Rule: 子任务已存在的属性不变，不存在的属性继承父任务
        // If record was previously a subtask and already inherited a field, its Own value for that field was not explicitly set
        var own = record.Own;
        bool wasSubtask = record.ParentId.HasValue;
        var prevInherit = record.Inherit;

        InheritField mask = InheritField.None;

        if ((wasSubtask && prevInherit.HasFlag(InheritField.Publisher)) || string.IsNullOrWhiteSpace(own.Publisher))
        {
            mask |= InheritField.Publisher;
        }

        if ((wasSubtask && prevInherit.HasFlag(InheritField.Assignee)) || string.IsNullOrWhiteSpace(own.Assignee))
        {
            mask |= InheritField.Assignee;
        }

        if ((wasSubtask && prevInherit.HasFlag(InheritField.Priority)) || own.Priority == Priority.Normal)
        {
            mask |= InheritField.Priority;
        }

        if ((wasSubtask && prevInherit.HasFlag(InheritField.Start)) || !own.Start.HasValue)
        {
            mask |= InheritField.Start;
        }

        if ((wasSubtask && prevInherit.HasFlag(InheritField.Due)) || own.Due.Kind == DueKind.None)
        {
            mask |= InheritField.Due;
        }

        if ((wasSubtask && prevInherit.HasFlag(InheritField.AttachmentRoot)) || !own.AttachmentRootId.HasValue)
        {
            mask |= InheritField.AttachmentRoot;
        }

        return mask;
    }

    public async Task TrashRecordAsync(Guid id, CancellationToken ct)
    {
        var rec = await _recordService.GetAsync(id, ct);
        if (rec is null) return;

        await _recordService.TrashAsync(id, rec.Version, ct);
        if (ExpandedRecordId == id)
        {
            ExpandedRecordId = null;
        }
        SetTransientNotice("已移入回收站");
        await RefreshAsync(ct);
    }

    public void ToggleCompletedMark(Guid id)
    {
        EnsureMarkedCompletedLoaded();
        bool markCompleted = !_markedCompletedIds.Contains(id);

        if (markCompleted)
        {
            _markedCompletedIds.Add(id);
            // If marking a parent task as completed, also mark its active subtasks in the panel
            foreach (var child in Items.Where(x => x.Record.ParentId == id))
            {
                _markedCompletedIds.Add(child.Id);
                child.IsMarkedCompleted = true;
            }
        }
        else
        {
            _markedCompletedIds.Remove(id);
            // If unmarking a subtask whose parent was marked completed, unmark the parent too
            var targetRow = Items.FirstOrDefault(x => x.Id == id);
            if (targetRow?.Record.ParentId is { } parentId && _markedCompletedIds.Remove(parentId))
            {
                var parentRow = Items.FirstOrDefault(x => x.Id == parentId);
                if (parentRow is not null)
                {
                    parentRow.IsMarkedCompleted = false;
                }
            }
        }

        var row = Items.FirstOrDefault(x => x.Id == id);
        if (row is not null)
        {
            row.IsMarkedCompleted = markCompleted;
        }

        PersistMarkedCompletedIds();
        RaiseCompletedArchiveProperties();
        SetTransientNotice(markCompleted
            ? "✓ 已标记为完成（点击「归档」后移出列表）"
            : "↩️ 已恢复为未完成状态");
    }

    public async Task ConfirmArchiveRecordAsync(Guid id, CancellationToken ct)
    {
        await CompleteRecordAsync(id, includeChildren: true, ct);
        _markedCompletedIds.Remove(id);
        PersistMarkedCompletedIds();
        RaiseCompletedArchiveProperties();
        SetTransientNotice("📦 已归档并移入历史归档列表");
    }

    public async Task ConfirmArchiveAllCompletedAsync(CancellationToken ct)
    {
        var completedRows = Items.Where(x => x.IsMarkedCompleted).ToList();
        if (completedRows.Count == 0)
        {
            SetTransientNotice("当前没有已标记完成的事项可归档");
            return;
        }

        int archivedCount = 0;
        // Archive parent tasks first (which also archives their children), then any remaining standalone subtasks
        foreach (var rootRow in completedRows.Where(x => !x.IsSubtask))
        {
            var rec = await _recordService.GetAsync(rootRow.Id, ct);
            if (rec is not null && rec.Status == RecordStatus.Active)
            {
                await _recordService.CloseAsync(rootRow.Id, rec.Version, includeChildren: true, ct);
                archivedCount++;
            }
            _markedCompletedIds.Remove(rootRow.Id);
        }

        foreach (var subRow in completedRows.Where(x => x.IsSubtask))
        {
            var rec = await _recordService.GetAsync(subRow.Id, ct);
            if (rec is not null && rec.Status == RecordStatus.Active)
            {
                await _recordService.CloseAsync(subRow.Id, rec.Version, includeChildren: false, ct);
                archivedCount++;
            }
            _markedCompletedIds.Remove(subRow.Id);
        }

        PersistMarkedCompletedIds();
        await RefreshAsync(ct);
        SetTransientNotice($"📦 已确认归档 {completedRows.Count} 项完成记录");
    }

    private void EnsureMarkedCompletedLoaded()
    {
        if (_markedCompletedLoaded || SettingsRepository is null)
        {
            return;
        }

        _markedCompletedLoaded = true;
        try
        {
            string? json = SettingsRepository.GetSettingJson(MarkedCompletedSettingKey);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var ids = JsonSerializer.Deserialize<Guid[]>(json);
                if (ids is not null)
                {
                    foreach (var id in ids)
                    {
                        _markedCompletedIds.Add(id);
                    }
                }
            }
        }
        catch
        {
            // Ignore malformed persisted state
        }
    }

    private void PersistMarkedCompletedIds()
    {
        if (SettingsRepository is null)
        {
            return;
        }

        try
        {
            string json = JsonSerializer.Serialize(_markedCompletedIds.ToArray());
            SettingsRepository.SetSettingJson(MarkedCompletedSettingKey, json);
        }
        catch
        {
        }
    }

    private void RaiseCompletedArchiveProperties()
    {
        RaisePropertyChanged(nameof(CompletedPendingArchiveCount));
        RaisePropertyChanged(nameof(HasCompletedPendingArchive));
        RaisePropertyChanged(nameof(ArchiveCompletedButtonText));
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
