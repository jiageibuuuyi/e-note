using System.Collections.ObjectModel;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Desktop.Localization;
using DesktopNotes.Domain;

namespace DesktopNotes.Desktop.ViewModels;

public sealed class CalendarViewModel : ViewModelBase
{
    private readonly IRecordQueryService _queryService;
    private readonly IRecordService? _recordService;
    private int _year = DateTime.Now.Year;
    private int _month = DateTime.Now.Month;
    private CalendarAxis _axis = CalendarAxis.Due;
    private RecordKind _kind = RecordKind.Task;
    private RecordStatus? _statusFilter = null;
    private DateOnly? _selectedDay;

    private bool _showTrash;
    private string _historySearchText = string.Empty;

    public ObservableCollection<CalendarEntry> MonthEntries { get; } = new();
    public ObservableCollection<RecordView> SelectedDayItems { get; } = new();
    public ObservableCollection<RecordView> DatelessItems { get; } = new();
    public ObservableCollection<RecordView> HistoricalItems { get; } = new();

    public int Year
    {
        get => _year;
        set
        {
            if (SetProperty(ref _year, value))
            {
                RaisePropertyChanged(nameof(YearMonthTitle));
            }
        }
    }

    public int Month
    {
        get => _month;
        set
        {
            if (SetProperty(ref _month, value))
            {
                RaisePropertyChanged(nameof(YearMonthTitle));
            }
        }
    }

    public string YearMonthTitle => Loc.IsChinese
        ? $"{_year} 年 {_month:D2} 月"
        : new DateTime(_year, _month, 1).ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
    public string MonthEntriesBadgeText => Loc.T(
        $"{MonthEntries.Count} item(s)",
        $"本月 {MonthEntries.Count} 项记录");
    public bool HasMonthEntries => MonthEntries.Count > 0;
    public bool HasHistoricalItems => HistoricalItems.Count > 0;

    public bool IsAxisDue => _axis == CalendarAxis.Due;
    public bool IsAxisStart => _axis == CalendarAxis.Start;
    public bool IsAxisCreated => _axis == CalendarAxis.Created;
    public bool IsAxisClosed => _axis == CalendarAxis.Closed;

    public bool IsArchiveView => !_showTrash;
    public bool IsTrashView => _showTrash;

    public string HistorySearchText
    {
        get => _historySearchText;
        set
        {
            if (SetProperty(ref _historySearchText, value))
            {
                _ = LoadHistoricalAsync(CancellationToken.None);
            }
        }
    }

    public CalendarAxis Axis
    {
        get => _axis;
        set => SetProperty(ref _axis, value);
    }

    public RecordKind Kind
    {
        get => _kind;
        set => SetProperty(ref _kind, value);
    }

    public RecordStatus? StatusFilter
    {
        get => _statusFilter;
        set => SetProperty(ref _statusFilter, value);
    }

    public DateOnly? SelectedDay
    {
        get => _selectedDay;
        set
        {
            if (SetProperty(ref _selectedDay, value))
            {
                RebuildSelectedDayItems();
            }
        }
    }

    public CalendarViewModel(IRecordQueryService queryService, IRecordService? recordService = null)
    {
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        _recordService = recordService;
        AppLocalizer.Instance.LanguageChanged += () =>
        {
            RaisePropertyChanged(nameof(Loc));
            RaisePropertyChanged(nameof(YearMonthTitle));
            RaisePropertyChanged(nameof(MonthEntriesBadgeText));
        };
    }

    public async Task LoadMonthAsync(int year, int month, CalendarAxis axis, RecordKind kind, RecordStatus? statusFilter, CancellationToken ct)
    {
        _year = year;
        _month = month;
        _axis = axis;
        _kind = kind;
        _statusFilter = statusFilter;

        RaisePropertyChanged(nameof(Year));
        RaisePropertyChanged(nameof(Month));
        RaisePropertyChanged(nameof(YearMonthTitle));
        RaisePropertyChanged(nameof(IsAxisDue));
        RaisePropertyChanged(nameof(IsAxisStart));
        RaisePropertyChanged(nameof(IsAxisCreated));
        RaisePropertyChanged(nameof(IsAxisClosed));

        var entries = await _queryService.CalendarAsync(
            new CalendarQuery(year, month, axis, kind, statusFilter), ct);

        MonthEntries.Clear();
        DatelessItems.Clear();

        foreach (var e in entries)
        {
            MonthEntries.Add(e);
            if (e.Day is null)
            {
                DatelessItems.Add(e.Record);
            }
        }

        RaisePropertyChanged(nameof(HasMonthEntries));
        RaisePropertyChanged(nameof(MonthEntriesBadgeText));
        RebuildSelectedDayItems();
    }

    public Task PreviousMonthAsync(CancellationToken ct)
    {
        var dt = new DateTime(_year, _month, 1).AddMonths(-1);
        return LoadMonthAsync(dt.Year, dt.Month, _axis, _kind, _statusFilter, ct);
    }

    public Task NextMonthAsync(CancellationToken ct)
    {
        var dt = new DateTime(_year, _month, 1).AddMonths(1);
        return LoadMonthAsync(dt.Year, dt.Month, _axis, _kind, _statusFilter, ct);
    }

    public Task GoToTodayAsync(CancellationToken ct)
    {
        var now = DateTime.Now;
        return LoadMonthAsync(now.Year, now.Month, _axis, _kind, _statusFilter, ct);
    }

    public Task ChangeAxisAsync(CalendarAxis axis, CancellationToken ct)
    {
        return LoadMonthAsync(_year, _month, axis, _kind, _statusFilter, ct);
    }

    public async Task SetHistoryModeAsync(bool showTrash, CancellationToken ct)
    {
        _showTrash = showTrash;
        RaisePropertyChanged(nameof(IsArchiveView));
        RaisePropertyChanged(nameof(IsTrashView));
        await LoadHistoricalAsync(ct);
    }

    public async Task LoadHistoricalAsync(CancellationToken ct)
    {
        var sort = new[]
        {
            new SortTerm(SortKey.Created, true)
        };

        var taskQuery = new RecordQuery(
            RecordKind.Task,
            _showTrash ? null : RecordStatus.Closed,
            _showTrash,
            _historySearchText,
            sort,
            0,
            200);

        var noteQuery = new RecordQuery(
            RecordKind.Note,
            _showTrash ? null : RecordStatus.Closed,
            _showTrash,
            _historySearchText,
            sort,
            0,
            200);

        var taskPage = await _queryService.SearchAsync(taskQuery, ct);
        var notePage = await _queryService.SearchAsync(noteQuery, ct);

        HistoricalItems.Clear();
        var combined = taskPage.Items
            .Concat(notePage.Items)
            .OrderByDescending(x => x.ClosedAt ?? x.DeletedAt ?? x.CreatedAt);

        foreach (var item in combined)
        {
            if (_showTrash && item.DeletedAt is null)
            {
                continue;
            }
            HistoricalItems.Add(item);
        }
        RaisePropertyChanged(nameof(HasHistoricalItems));
    }

    public async Task ReopenRecordAsync(Guid id, CancellationToken ct)
    {
        if (_recordService is null) return;
        var rec = await _recordService.GetAsync(id, ct);
        if (rec is null) return;
        await _recordService.ReopenAsync(id, rec.Version, includeParent: true, ct);
        await LoadHistoricalAsync(ct);
        await LoadMonthAsync(_year, _month, _axis, _kind, _statusFilter, ct);
    }

    public async Task TrashRecordAsync(Guid id, CancellationToken ct)
    {
        if (_recordService is null) return;
        var rec = await _recordService.GetAsync(id, ct);
        if (rec is null) return;
        await _recordService.TrashAsync(id, rec.Version, ct);
        await LoadHistoricalAsync(ct);
    }

    public async Task RestoreRecordAsync(Guid id, CancellationToken ct)
    {
        if (_recordService is null) return;
        var rec = await _recordService.GetAsync(id, ct);
        if (rec is null) return;
        await _recordService.RestoreAsync(id, rec.Version, includeParent: true, ct);
        await LoadHistoricalAsync(ct);
    }

    private void RebuildSelectedDayItems()
    {
        SelectedDayItems.Clear();
        if (_selectedDay is null)
        {
            return;
        }

        foreach (var e in MonthEntries.Where(x => x.Day == _selectedDay))
        {
            SelectedDayItems.Add(e.Record);
        }
    }
}
