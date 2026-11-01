using System.Collections.ObjectModel;
using System.Diagnostics;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Ports;
using DesktopNotes.Domain;

namespace DesktopNotes.Desktop.ViewModels;

public sealed class DetailViewModel : ViewModelBase
{
    private readonly IRecordService _recordService;
    private readonly IRecordQueryService _queryService;
    private readonly IAttachmentService? _attachmentService;
    private readonly IAttachmentRepositoryPort? _attachmentRepository;
    private readonly string? _defaultAttachmentRootPath;

    private Guid _recordId;
    private Guid? _parentId;
    private RecordKind _kind = RecordKind.Task;
    private RecordStatus _status = RecordStatus.Active;
    private long _version;
    private string _title = string.Empty;
    private string _body = string.Empty;
    private string? _ownPublisher;
    private string? _ownAssignee;
    private Priority _ownPriority = Priority.Normal;
    private DateTimeOffset? _ownStart;
    private DueSpec _ownDue = DueSpec.Empty;
    private Guid? _ownAttachmentRootId;
    private InheritField _inheritMask = InheritField.None;
    private EffectiveFields? _parentEffective;
    private string _statusMessage = string.Empty;
    private bool _hasConflict;
    private CancellationTokenSource? _draftDebounceCts;

    public ObservableCollection<RecordView> Subtasks { get; } = new();
    public ObservableCollection<AttachmentView> Attachments { get; } = new();

    public Guid RecordId => _recordId;
    public Guid? ParentId => _parentId;
    public bool IsSubtask => _parentId.HasValue;
    public RecordKind Kind => _kind;
    public RecordStatus Status => _status;
    public long Version => _version;

    public string EditorId => $"detail:{_recordId:D}";

    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                ScheduleDraftSave();
            }
        }
    }

    public string Body
    {
        get => _body;
        set
        {
            if (SetProperty(ref _body, value))
            {
                ScheduleDraftSave();
            }
        }
    }

    public string AssigneeText
    {
        get => Effective.Assignee ?? string.Empty;
        set
        {
            string? cleaned = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (Effective.Assignee != cleaned)
            {
                SetOwnAssignee(cleaned);
            }
        }
    }

    public string PublisherText
    {
        get => Effective.Publisher ?? string.Empty;
        set
        {
            string? cleaned = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (Effective.Publisher != cleaned)
            {
                SetOwnPublisher(cleaned);
            }
        }
    }

    public InheritField InheritMask
    {
        get => _inheritMask;
        private set
        {
            if (SetProperty(ref _inheritMask, value))
            {
                RaisePropertyChanged(nameof(Effective));
            }
        }
    }

    public EffectiveFields Own => new(
        _ownPublisher,
        _ownAssignee,
        _ownPriority,
        _ownStart,
        _ownDue,
        _ownAttachmentRootId);

    public EffectiveFields Effective => _parentId.HasValue
        ? InheritanceResolver.Resolve(Own, _parentEffective, _inheritMask)
        : Own;

    public string RecordKindTitle => _kind == RecordKind.Task
        ? (_parentId.HasValue ? "↳ 子任务详情 (支持继承父任务属性)" : "任务详情")
        : "笔记详情";
    public bool CanHaveSubtasks => _kind == RecordKind.Task && !_parentId.HasValue;
    public bool HasSubtasks => Subtasks.Count > 0;

    private string _newSubtaskTitle = string.Empty;
    public string NewSubtaskTitle
    {
        get => _newSubtaskTitle;
        set => SetProperty(ref _newSubtaskTitle, value);
    }

    public bool IsPriorityP0 => Effective.Priority == Priority.Highest;
    public bool IsPriorityP1 => Effective.Priority == Priority.High;
    public bool IsPriorityP2 => Effective.Priority == Priority.Normal;
    public bool IsPriorityP3 => Effective.Priority == Priority.Low;

    public bool HasDue => Effective.Due.Kind != DueKind.None;

    public string DueDisplayText => Effective.Due.Kind switch
    {
        DueKind.Date when Effective.Due.Date is { } d =>
            IsInheriting(InheritField.Due) ? $"{d:yyyy-MM-dd} (继承)" : d.ToString("yyyy-MM-dd"),
        DueKind.Instant when Effective.Due.Instant is { } inst =>
            IsInheriting(InheritField.Due)
                ? $"{inst.ToLocalTime():yyyy-MM-dd HH:mm} (继承)"
                : inst.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
        _ => "无截止期"
    };

    public DateTime? SelectedDueDate
    {
        get => Effective.Due.Kind switch
        {
            DueKind.Date when Effective.Due.Date is { } d => d.ToDateTime(TimeOnly.MinValue),
            DueKind.Instant when Effective.Due.Instant is { } inst => inst.ToLocalTime().Date,
            _ => null
        };
        set
        {
            if (value is { } dt)
            {
                SetOwnDue(new DueSpec(DueKind.Date, DateOnly.FromDateTime(dt), null, null));
            }
            else
            {
                ClearDue();
            }
        }
    }

    public bool HasStart => Effective.Start.HasValue;

    public string StartDisplayText => Effective.Start switch
    {
        { } s => IsInheriting(InheritField.Start)
            ? $"{s.ToLocalTime():yyyy-MM-dd} (继承)"
            : s.ToLocalTime().ToString("yyyy-MM-dd"),
        _ => "未设置"
    };

    public DateTime? SelectedStartDate
    {
        get => Effective.Start?.ToLocalTime().Date;
        set
        {
            if (value is { } dt)
            {
                var localOffset = TimeZoneInfo.Local.GetUtcOffset(dt);
                SetOwnStart(new DateTimeOffset(dt.Year, dt.Month, dt.Day, 9, 0, 0, localOffset));
            }
            else
            {
                SetOwnStart(null);
            }
        }
    }

    public string AttachmentRootPathText
    {
        get
        {
            if (Effective.AttachmentRootId is { } rid && _attachmentRepository is not null)
            {
                var root = _attachmentRepository.GetStorageRoot(rid);
                if (root is not null)
                {
                    return IsInheriting(InheritField.AttachmentRoot)
                        ? $"{root.AbsolutePath} (继承)"
                        : root.AbsolutePath;
                }
            }

            if (!string.IsNullOrWhiteSpace(_defaultAttachmentRootPath))
            {
                return $"默认路径: {_defaultAttachmentRootPath}";
            }

            return "使用系统默认附件目录";
        }
    }

    public bool HasAttachments => Attachments.Count > 0;

    public void SetDueToday() => SetOwnDue(new DueSpec(DueKind.Date, DateOnly.FromDateTime(DateTime.Now), null, null));
    public void SetDueTomorrow() => SetOwnDue(new DueSpec(DueKind.Date, DateOnly.FromDateTime(DateTime.Now.AddDays(1)), null, null));
    public void SetDueNextWeek() => SetOwnDue(new DueSpec(DueKind.Date, DateOnly.FromDateTime(DateTime.Now.AddDays(7)), null, null));
    public void SetStartToday() => SelectedStartDate = DateTime.Now.Date;
    public void ClearStart() => SetOwnStart(null);

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool HasConflict
    {
        get => _hasConflict;
        private set => SetProperty(ref _hasConflict, value);
    }

    public DetailViewModel(
        IRecordService recordService,
        IRecordQueryService queryService,
        IAttachmentService? attachmentService = null,
        IAttachmentRepositoryPort? attachmentRepository = null,
        string? defaultAttachmentRootPath = null)
    {
        _recordService = recordService ?? throw new ArgumentNullException(nameof(recordService));
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        _attachmentService = attachmentService;
        _attachmentRepository = attachmentRepository;
        _defaultAttachmentRootPath = defaultAttachmentRootPath;
    }

    public Task LoadNewAsync(RecordKind kind, Guid? parentId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _recordId = Guid.NewGuid();
        _parentId = parentId;
        _kind = kind;
        _status = RecordStatus.Active;
        _version = 0;
        _title = string.Empty;
        _body = string.Empty;
        _ownPublisher = null;
        _ownAssignee = null;
        _ownPriority = Priority.Normal;
        _ownStart = DateTimeOffset.Now;
        _ownDue = DueSpec.Empty;
        _ownAttachmentRootId = null;
        _inheritMask = parentId.HasValue ? InheritField.All : InheritField.None;
        _statusMessage = "新建记录";
        HasConflict = false;
        Attachments.Clear();
        Subtasks.Clear();
        RaiseAllProperties();
        return Task.CompletedTask;
    }

    public async Task LoadAsync(Guid id, CancellationToken ct)
    {
        var rec = await _recordService.GetAsync(id, ct)
            ?? throw new AppException(ErrorCodes.NotFound, "Record not found.", new[] { id });

        ApplyRecordView(rec);

        if (rec.ParentId is { } pid)
        {
            var parent = await _recordService.GetAsync(pid, ct);
            _parentEffective = parent?.Effective;
        }
        else
        {
            _parentEffective = null;
        }

        var draft = await _recordService.GetDraftAsync(EditorId, ct);
        if (draft is not null && draft.BaseVersion == rec.Version)
        {
            _title = draft.Title;
            _body = draft.Body;
            StatusMessage = "已恢复未提交草稿";
        }
        else
        {
            StatusMessage = string.Empty;
        }

        HasConflict = false;
        await RefreshSubtasksAsync(ct);
        await RefreshAttachmentsAsync(ct);
        RaiseAllProperties();
    }

    public async Task RefreshSubtasksAsync(CancellationToken ct)
    {
        Subtasks.Clear();
        if (CanHaveSubtasks && _version > 0)
        {
            var page = await _queryService.SearchAsync(new RecordQuery(
                RecordKind.Task,
                RecordStatus.Active,
                false,
                string.Empty,
                new[] { new SortTerm(SortKey.Created, false) },
                0,
                200), ct);
            foreach (var item in page.Items.Where(x => x.ParentId == _recordId))
            {
                Subtasks.Add(item);
            }
        }
        RaisePropertyChanged(nameof(HasSubtasks));
    }

    public async Task AddSubtaskAsync(CancellationToken ct)
    {
        string text = _newSubtaskTitle.Trim();
        if (string.IsNullOrEmpty(text) || !CanHaveSubtasks)
        {
            return;
        }

        if (_version == 0)
        {
            if (string.IsNullOrWhiteSpace(_title) && string.IsNullOrWhiteSpace(_body))
            {
                _title = "未命名主任务";
            }
            bool saved = await SaveAsync(ct);
            if (!saved) return;
        }

        var req = new SaveRecordRequest(
            Guid.NewGuid(),
            0,
            RecordKind.Task,
            _recordId,
            text,
            string.Empty,
            new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null),
            InheritField.All);

        await _recordService.SaveAsync(req, ct);
        NewSubtaskTitle = string.Empty;
        await RefreshSubtasksAsync(ct);
        StatusMessage = "已添加子任务";
    }

    public async Task CompleteSubtaskAsync(Guid subtaskId, CancellationToken ct)
    {
        var sub = await _recordService.GetAsync(subtaskId, ct);
        if (sub is null) return;
        await _recordService.CloseAsync(subtaskId, sub.Version, includeChildren: false, ct);
        await RefreshSubtasksAsync(ct);
        StatusMessage = "子任务已完成并归档";
    }

    public async Task RemoveSubtaskAsync(Guid subtaskId, CancellationToken ct)
    {
        var sub = await _recordService.GetAsync(subtaskId, ct);
        if (sub is null) return;
        await _recordService.TrashAsync(subtaskId, sub.Version, ct);
        await RefreshSubtasksAsync(ct);
        StatusMessage = "已移除子任务";
    }

    public async Task RefreshAttachmentsAsync(CancellationToken ct)
    {
        Attachments.Clear();
        if (_attachmentService is not null && _version > 0)
        {
            var list = await _attachmentService.ListForRecordAsync(_recordId, includeTrashed: false, ct);
            foreach (var item in list)
            {
                Attachments.Add(item);
            }
        }
        RaisePropertyChanged(nameof(HasAttachments));
    }

    public void SetCustomAttachmentDirectory(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || _attachmentRepository is null)
        {
            return;
        }

        string fullPath = Path.GetFullPath(folderPath);
        Directory.CreateDirectory(fullPath);
        var existing = _attachmentRepository.ListStorageRoots()
            .FirstOrDefault(r => string.Equals(r.AbsolutePath, fullPath, StringComparison.OrdinalIgnoreCase));
        var root = existing ?? _attachmentRepository.RegisterStorageRoot(
            Guid.NewGuid(),
            fullPath,
            "windows",
            DateTimeOffset.UtcNow);
        SetOwnAttachmentRoot(root.Id);
        StatusMessage = "已设置任务专属附件目录";
    }

    public async Task ImportFilesAsync(IReadOnlyList<string> filePaths, ImportOrigin origin, CancellationToken ct)
    {
        if (_attachmentService is null || filePaths.Count == 0)
        {
            return;
        }

        // Ensure record exists in DB before linking attachments
        if (_version == 0)
        {
            if (string.IsNullOrWhiteSpace(_title) && string.IsNullOrWhiteSpace(_body))
            {
                _title = Path.GetFileNameWithoutExtension(filePaths[0]);
            }
            bool saved = await SaveAsync(ct);
            if (!saved)
            {
                return;
            }
        }

        try
        {
            var req = new ImportRequest(
                Guid.NewGuid(),
                _recordId,
                filePaths,
                ImportIntent.Copy,
                ImportOrigin.FilePicker);
            await _attachmentService.EnqueueAsync(req, ct);
            await RefreshAttachmentsAsync(ct);
            StatusMessage = $"已复制关联 {filePaths.Count} 个附件（源文件已保留）";
        }
        catch (Exception ex)
        {
            StatusMessage = $"附件导入失败: {ex.Message}";
        }
    }

    public async Task OpenAttachmentAsync(Guid attachmentId, CancellationToken ct)
    {
        if (_attachmentService is null) return;
        try
        {
            await _attachmentService.OpenAsync(attachmentId, ct);
        }
        catch (Exception ex)
        {
            StatusMessage = $"无法打开附件: {ex.Message}";
        }
    }

    public async Task RemoveAttachmentAsync(Guid attachmentId, CancellationToken ct)
    {
        if (_attachmentService is null) return;
        try
        {
            await _attachmentService.TrashAttachmentAsync(attachmentId, ct);
            await RefreshAttachmentsAsync(ct);
            StatusMessage = "已移除附件";
        }
        catch (Exception ex)
        {
            StatusMessage = $"移除附件失败: {ex.Message}";
        }
    }

    public void OpenAttachmentRootDirectory()
    {
        try
        {
            string? targetDir = null;
            if (Effective.AttachmentRootId is { } rid && _attachmentRepository is not null)
            {
                targetDir = _attachmentRepository.GetStorageRoot(rid)?.AbsolutePath;
            }
            targetDir ??= _defaultAttachmentRootPath;

            if (!string.IsNullOrWhiteSpace(targetDir))
            {
                Directory.CreateDirectory(targetDir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetDir,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开目录失败: {ex.Message}";
        }
    }

    public void SetOwnAssignee(string? assignee)
    {
        _ownAssignee = assignee;
        if (_parentId.HasValue)
        {
            InheritMask &= ~InheritField.Assignee;
        }
        RaisePropertyChanged(nameof(Own));
        RaisePropertyChanged(nameof(Effective));
        RaisePropertyChanged(nameof(AssigneeText));
    }

    public void SetOwnPublisher(string? publisher)
    {
        _ownPublisher = publisher;
        if (_parentId.HasValue)
        {
            InheritMask &= ~InheritField.Publisher;
        }
        RaisePropertyChanged(nameof(Own));
        RaisePropertyChanged(nameof(Effective));
        RaisePropertyChanged(nameof(PublisherText));
    }

    public void SetOwnPriority(Priority priority)
    {
        _ownPriority = priority;
        if (_parentId.HasValue)
        {
            InheritMask &= ~InheritField.Priority;
        }
        RaisePropertyChanged(nameof(Own));
        RaisePropertyChanged(nameof(Effective));
        RaisePriorityProperties();
    }

    public void SetOwnStart(DateTimeOffset? start)
    {
        _ownStart = start;
        if (_parentId.HasValue)
        {
            InheritMask &= ~InheritField.Start;
        }
        RaisePropertyChanged(nameof(Own));
        RaisePropertyChanged(nameof(Effective));
        RaiseStartProperties();
    }

    public void SetOwnDue(DueSpec due)
    {
        _ownDue = due;
        if (_parentId.HasValue)
        {
            // Explicit change or clearing deadline clears Due inheritance bit
            InheritMask &= ~InheritField.Due;
        }
        RaisePropertyChanged(nameof(Own));
        RaisePropertyChanged(nameof(Effective));
        RaiseDueProperties();
    }

    public void ClearDue()
    {
        SetOwnDue(DueSpec.Empty);
    }

    public void SetOwnAttachmentRoot(Guid? rootId)
    {
        _ownAttachmentRootId = rootId;
        if (_parentId.HasValue)
        {
            InheritMask &= ~InheritField.AttachmentRoot;
        }
        RaisePropertyChanged(nameof(Own));
        RaisePropertyChanged(nameof(Effective));
        RaisePropertyChanged(nameof(AttachmentRootPathText));
    }

    public void RestoreFieldInheritance(InheritField field)
    {
        if (!_parentId.HasValue)
        {
            return;
        }

        InheritMask |= (field & InheritField.All);
        RaisePropertyChanged(nameof(Effective));
        RaisePropertyChanged(nameof(AssigneeText));
        RaisePropertyChanged(nameof(PublisherText));
        RaisePropertyChanged(nameof(AttachmentRootPathText));
        RaisePriorityProperties();
        RaiseDueProperties();
        RaiseStartProperties();
    }

    public bool IsInheriting(InheritField field) => _parentId.HasValue && (_inheritMask & field) != 0;

    public async Task<bool> SaveAsync(CancellationToken ct)
    {
        _draftDebounceCts?.Cancel();
        try
        {
            var req = new SaveRecordRequest(
                _recordId,
                _version,
                _kind,
                _parentId,
                _title,
                _body,
                Own,
                _inheritMask);

            var res = await _recordService.SaveAsync(req, ct);
            var updatedSelf = res.Changed.First(r => r.Id == _recordId);
            ApplyRecordView(updatedSelf);
            await _recordService.DeleteDraftAsync(EditorId, ct);
            HasConflict = false;
            StatusMessage = "已保存";
            RaiseAllProperties();
            return true;
        }
        catch (AppException ex) when (ex.Code == ErrorCodes.Conflict)
        {
            HasConflict = true;
            StatusMessage = "版本冲突：该记录已被更新，请重新加载合并后再保存";
            return false;
        }
        catch (AppException ex)
        {
            StatusMessage = $"保存失败 ({ex.Code}): {ex.Message}";
            return false;
        }
    }

    public async Task FlushDraftNowAsync(CancellationToken ct)
    {
        _draftDebounceCts?.Cancel();
        await _recordService.SaveDraftAsync(EditorId, _recordId, _version, _title, _body, ct);
        StatusMessage = "草稿已暂存";
    }

    private void ScheduleDraftSave()
    {
        _draftDebounceCts?.Cancel();
        _draftDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _draftDebounceCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, cts.Token);
                await _recordService.SaveDraftAsync(EditorId, _recordId, _version, _title, _body, cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void ApplyRecordView(RecordView rec)
    {
        _recordId = rec.Id;
        _parentId = rec.ParentId;
        _kind = rec.Kind;
        _status = rec.Status;
        _version = rec.Version;
        _title = rec.Title;
        _body = rec.Body;
        _ownPublisher = rec.Own.Publisher;
        _ownAssignee = rec.Own.Assignee;
        _ownPriority = rec.Own.Priority;
        _ownStart = rec.Own.Start;
        _ownDue = rec.Own.Due;
        _ownAttachmentRootId = rec.Own.AttachmentRootId;
        _inheritMask = rec.Inherit;
    }

    private void RaisePriorityProperties()
    {
        RaisePropertyChanged(nameof(IsPriorityP0));
        RaisePropertyChanged(nameof(IsPriorityP1));
        RaisePropertyChanged(nameof(IsPriorityP2));
        RaisePropertyChanged(nameof(IsPriorityP3));
    }

    private void RaiseDueProperties()
    {
        RaisePropertyChanged(nameof(HasDue));
        RaisePropertyChanged(nameof(DueDisplayText));
        RaisePropertyChanged(nameof(SelectedDueDate));
    }

    private void RaiseStartProperties()
    {
        RaisePropertyChanged(nameof(HasStart));
        RaisePropertyChanged(nameof(StartDisplayText));
        RaisePropertyChanged(nameof(SelectedStartDate));
    }

    private void RaiseAllProperties()
    {
        RaisePropertyChanged(nameof(RecordKindTitle));
        RaisePropertyChanged(nameof(CanHaveSubtasks));
        RaisePropertyChanged(nameof(HasSubtasks));
        RaisePropertyChanged(nameof(IsSubtask));
        RaisePropertyChanged(nameof(Title));
        RaisePropertyChanged(nameof(Body));
        RaisePropertyChanged(nameof(AssigneeText));
        RaisePropertyChanged(nameof(PublisherText));
        RaisePropertyChanged(nameof(AttachmentRootPathText));
        RaisePropertyChanged(nameof(HasAttachments));
        RaisePropertyChanged(nameof(Version));
        RaisePropertyChanged(nameof(Own));
        RaisePropertyChanged(nameof(Effective));
        RaisePropertyChanged(nameof(InheritMask));
        RaisePropertyChanged(nameof(Status));
        RaisePriorityProperties();
        RaiseDueProperties();
        RaiseStartProperties();
    }
}
