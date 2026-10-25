using System.Collections.ObjectModel;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Domain;

namespace DesktopNotes.Desktop.ViewModels;

public sealed class DetailViewModel : ViewModelBase
{
    private readonly IRecordService _recordService;
    private readonly IRecordQueryService _queryService;
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

    public DetailViewModel(IRecordService recordService, IRecordQueryService queryService)
    {
        _recordService = recordService ?? throw new ArgumentNullException(nameof(recordService));
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
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
        _ownStart = null;
        _ownDue = DueSpec.Empty;
        _ownAttachmentRootId = null;
        _inheritMask = parentId.HasValue ? InheritField.All : InheritField.None;
        _statusMessage = "新建记录";
        HasConflict = false;
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
        RaiseAllProperties();
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
    }

    public void RestoreFieldInheritance(InheritField field)
    {
        if (!_parentId.HasValue)
        {
            return;
        }

        InheritMask |= (field & InheritField.All);
        RaisePropertyChanged(nameof(Effective));
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

    private void RaiseAllProperties()
    {
        RaisePropertyChanged(nameof(Title));
        RaisePropertyChanged(nameof(Body));
        RaisePropertyChanged(nameof(Version));
        RaisePropertyChanged(nameof(Own));
        RaisePropertyChanged(nameof(Effective));
        RaisePropertyChanged(nameof(InheritMask));
        RaisePropertyChanged(nameof(Status));
    }
}
