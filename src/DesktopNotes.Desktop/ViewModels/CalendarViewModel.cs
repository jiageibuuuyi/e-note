using System.Collections.ObjectModel;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Desktop.Localization;
using DesktopNotes.Domain;

namespace DesktopNotes.Desktop.ViewModels;

public sealed class CalendarDayCellViewModel : ViewModelBase
{
    private bool _isSelected;

    public DateOnly Date { get; }
    public int DayNumber => Date.Day;
    public bool IsCurrentMonth { get; }
    public bool IsToday { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public ObservableCollection<RecordView> CreatedTasks { get; } = new();
    public ObservableCollection<RecordView> CompletedTasks { get; } = new();
    public ObservableCollection<RecordView> DueTasks { get; } = new();

    public int CreatedCount => CreatedTasks.Count;
    public int CompletedCount => CompletedTasks.Count;
    public int DueCount => DueTasks.Count;
    public int TotalTaskCount => CreatedCount + CompletedCount + DueCount;
    public bool HasTasks => TotalTaskCount > 0;

    public bool HasCreatedTasks => CreatedCount > 0;
    public bool HasCompletedTasks => CompletedCount > 0;
    public bool HasDueTasks => DueCount > 0;

    public string CreatedBadgeText => $"+{CreatedCount}";
    public string CompletedBadgeText => $"✓{CompletedCount}";
    public string DueBadgeText => $"⏰{DueCount}";

    public string DayHeaderTitle => Date.ToString(
        AppLocalizer.Instance.IsChinese ? "yyyy年MM月dd日 dddd" : "dddd, MMMM dd, yyyy",
        AppLocalizer.Instance.IsChinese ? System.Globalization.CultureInfo.GetCultureInfo("zh-CN") : System.Globalization.CultureInfo.InvariantCulture);

    public string CreatedSectionTitle => AppLocalizer.Instance.T(
        $"🌱 Created TODOs ({CreatedCount})",
        $"🌱 本日创建任务 ({CreatedCount})");

    public string CompletedSectionTitle => AppLocalizer.Instance.T(
        $"✅ Completed TODOs ({CompletedCount})",
        $"✅ 本日完成任务 ({CompletedCount})");

    public string DueSectionTitle => AppLocalizer.Instance.T(
        $"⏰ Due TODOs ({DueCount})",
        $"⏰ 本日截止任务 ({DueCount})");

    public string EmptyHoverText => AppLocalizer.Instance.T(
        "No task activity on this day",
        "该日期无创建或完成任务记录");

    public CalendarDayCellViewModel(DateOnly date, bool isCurrentMonth, bool isToday)
    {
        Date = date;
        IsCurrentMonth = isCurrentMonth;
        IsToday = isToday;
    }

    public void RefreshProperties()
    {
        RaisePropertyChanged(nameof(CreatedCount));
        RaisePropertyChanged(nameof(CompletedCount));
        RaisePropertyChanged(nameof(DueCount));
        RaisePropertyChanged(nameof(TotalTaskCount));
        RaisePropertyChanged(nameof(HasTasks));
        RaisePropertyChanged(nameof(HasCreatedTasks));
        RaisePropertyChanged(nameof(HasCompletedTasks));
        RaisePropertyChanged(nameof(HasDueTasks));
        RaisePropertyChanged(nameof(CreatedBadgeText));
        RaisePropertyChanged(nameof(CompletedBadgeText));
        RaisePropertyChanged(nameof(DueBadgeText));
        RaisePropertyChanged(nameof(CreatedSectionTitle));
        RaisePropertyChanged(nameof(CompletedSectionTitle));
        RaisePropertyChanged(nameof(DueSectionTitle));
        RaisePropertyChanged(nameof(DayHeaderTitle));
        RaisePropertyChanged(nameof(EmptyHoverText));
    }
}

public sealed class CalendarViewModel : ViewModelBase
{
    private readonly IRecordQueryService _queryService;
    private readonly IRecordService? _recordService;
    private readonly Func<TimeZoneInfo> _zoneProvider;
    private int _year = DateTime.Now.Year;
    private int _month = DateTime.Now.Month;
    private CalendarAxis _axis = CalendarAxis.Due;
    private RecordKind _kind = RecordKind.Task;
    private RecordStatus? _statusFilter = null;
    private DateOnly? _selectedDay;
    private bool _isMonthGridView = true;
    private CalendarDayCellViewModel? _selectedDayCell;

    private bool _showTrash;
    private string _historySearchText = string.Empty;

    public ObservableCollection<CalendarEntry> MonthEntries { get; } = new();
    public ObservableCollection<CalendarDayCellViewModel> MonthDays { get; } = new();
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

    public bool IsMonthGridView
    {
        get => _isMonthGridView;
        set
        {
            if (SetProperty(ref _isMonthGridView, value))
            {
                RaisePropertyChanged(nameof(IsListView));
            }
        }
    }

    public bool IsListView => !_isMonthGridView;

    public CalendarDayCellViewModel? SelectedDayCell
    {
        get => _selectedDayCell;
        set => SetProperty(ref _selectedDayCell, value);
    }

    public string YearMonthTitle => Loc.IsChinese
        ? $"{_year} 年 {_month:D2} 月"
        : new DateTime(_year, _month, 1).ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    public string MonthEntriesBadgeText => Loc.T(
        $"{MonthEntries.Count} item(s)",
        $"本月 {MonthEntries.Count} 项记录");

    public string SelectedDayHeader => _selectedDay.HasValue
        ? _selectedDay.Value.ToString(
            Loc.IsChinese ? "yyyy年MM月dd日 dddd 任务清单" : "TODOs for dddd, MMMM dd, yyyy",
            Loc.IsChinese ? System.Globalization.CultureInfo.GetCultureInfo("zh-CN") : System.Globalization.CultureInfo.InvariantCulture)
        : Loc.CalSelectedDateTitle;

    public bool HasMonthEntries => MonthEntries.Count > 0;
    public bool HasHistoricalItems => HistoricalItems.Count > 0;
    public bool HasSelectedDayItems => SelectedDayItems.Count > 0;

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

    public CalendarViewModel(
        IRecordQueryService queryService,
        IRecordService? recordService = null,
        Func<TimeZoneInfo>? zoneProvider = null)
    {
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        _recordService = recordService;
        _zoneProvider = zoneProvider ?? (() => TimeZoneInfo.Local);

        AppLocalizer.Instance.LanguageChanged += () =>
        {
            RaisePropertyChanged(nameof(Loc));
            RaisePropertyChanged(nameof(YearMonthTitle));
            RaisePropertyChanged(nameof(MonthEntriesBadgeText));
            RaisePropertyChanged(nameof(SelectedDayHeader));
            foreach (var cell in MonthDays)
            {
                cell.RefreshProperties();
            }
        };

        AppLocalizer.Instance.VisualStyleChanged += () =>
        {
            RaisePropertyChanged(nameof(Loc));
        };
    }

    public void SelectDay(CalendarDayCellViewModel cell)
    {
        if (cell is null) return;
        if (_selectedDayCell is not null)
        {
            _selectedDayCell.IsSelected = false;
        }

        cell.IsSelected = true;
        _selectedDayCell = cell;
        SelectedDay = cell.Date;
        RaisePropertyChanged(nameof(SelectedDayCell));
        RaisePropertyChanged(nameof(SelectedDayHeader));
    }

    public void SetViewMode(bool monthGrid)
    {
        IsMonthGridView = monthGrid;
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

        // Build 42-day month grid (6 weeks x 7 days)
        BuildMonthGrid(year, month);

        // Fetch task activities: created tasks, completed tasks, due tasks
        await PopulateDayActivitiesAsync(year, month, ct);

        RebuildSelectedDayItems();
    }

    private void BuildMonthGrid(int year, int month)
    {
        MonthDays.Clear();
        var firstDayOfMonth = new DateOnly(year, month, 1);
        int dayOfWeek = (int)firstDayOfMonth.DayOfWeek;
        int offset = ((dayOfWeek - (int)DayOfWeek.Monday) + 7) % 7;
        DateOnly startDate = firstDayOfMonth.AddDays(-offset);

        var today = DateOnly.FromDateTime(DateTime.Today);

        for (int i = 0; i < 42; i++)
        {
            var date = startDate.AddDays(i);
            bool isCurMonth = date.Month == month && date.Year == year;
            bool isToday = date == today;
            var cell = new CalendarDayCellViewModel(date, isCurMonth, isToday);
            if (_selectedDay.HasValue && cell.Date == _selectedDay.Value)
            {
                cell.IsSelected = true;
                _selectedDayCell = cell;
            }
            MonthDays.Add(cell);
        }

        if (_selectedDayCell is null)
        {
            var defCell = MonthDays.FirstOrDefault(x => x.IsToday && x.IsCurrentMonth)
                ?? MonthDays.FirstOrDefault(x => x.IsCurrentMonth && x.DayNumber == 1);
            if (defCell is not null)
            {
                defCell.IsSelected = true;
                _selectedDayCell = defCell;
                _selectedDay = defCell.Date;
            }
        }
    }

    private async Task PopulateDayActivitiesAsync(int year, int month, CancellationToken ct)
    {
        var createdEntries = await _queryService.CalendarAsync(
            new CalendarQuery(year, month, CalendarAxis.Created, RecordKind.Task, null), ct);
        var closedEntries = await _queryService.CalendarAsync(
            new CalendarQuery(year, month, CalendarAxis.Closed, RecordKind.Task, null), ct);
        var dueEntries = await _queryService.CalendarAsync(
            new CalendarQuery(year, month, CalendarAxis.Due, RecordKind.Task, null), ct);

        var prevMonthDt = new DateTime(year, month, 1).AddMonths(-1);
        var prevCreated = await _queryService.CalendarAsync(
            new CalendarQuery(prevMonthDt.Year, prevMonthDt.Month, CalendarAxis.Created, RecordKind.Task, null), ct);
        var prevClosed = await _queryService.CalendarAsync(
            new CalendarQuery(prevMonthDt.Year, prevMonthDt.Month, CalendarAxis.Closed, RecordKind.Task, null), ct);

        var nextMonthDt = new DateTime(year, month, 1).AddMonths(1);
        var nextCreated = await _queryService.CalendarAsync(
            new CalendarQuery(nextMonthDt.Year, nextMonthDt.Month, CalendarAxis.Created, RecordKind.Task, null), ct);
        var nextClosed = await _queryService.CalendarAsync(
            new CalendarQuery(nextMonthDt.Year, nextMonthDt.Month, CalendarAxis.Closed, RecordKind.Task, null), ct);

        var allCreated = createdEntries.Concat(prevCreated).Concat(nextCreated).Where(x => x.Day.HasValue);
        var allClosed = closedEntries.Concat(prevClosed).Concat(nextClosed).Where(x => x.Day.HasValue);
        var allDue = dueEntries.Where(x => x.Day.HasValue);

        var dayMap = MonthDays.ToDictionary(x => x.Date, x => x);

        foreach (var entry in allCreated)
        {
            if (dayMap.TryGetValue(entry.Day!.Value, out var cell))
            {
                if (!cell.CreatedTasks.Any(x => x.Id == entry.Record.Id))
                {
                    cell.CreatedTasks.Add(entry.Record);
                }
            }
        }

        foreach (var entry in allClosed)
        {
            if (dayMap.TryGetValue(entry.Day!.Value, out var cell))
            {
                if (!cell.CompletedTasks.Any(x => x.Id == entry.Record.Id))
                {
                    cell.CompletedTasks.Add(entry.Record);
                }
            }
        }

        foreach (var entry in allDue)
        {
            if (dayMap.TryGetValue(entry.Day!.Value, out var cell))
            {
                if (!cell.DueTasks.Any(x => x.Id == entry.Record.Id))
                {
                    cell.DueTasks.Add(entry.Record);
                }
            }
        }

        foreach (var cell in MonthDays)
        {
            cell.RefreshProperties();
        }
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

    public async Task<int> EmptyTrashAsync(CancellationToken ct)
    {
        if (_recordService is null) return 0;
        int count = await _recordService.PurgeTrashAsync(ct);
        await LoadHistoricalAsync(ct);
        return count;
    }

    private void RebuildSelectedDayItems()
    {
        SelectedDayItems.Clear();
        if (_selectedDay is null)
        {
            RaisePropertyChanged(nameof(HasSelectedDayItems));
            RaisePropertyChanged(nameof(SelectedDayHeader));
            return;
        }

        var cell = MonthDays.FirstOrDefault(x => x.Date == _selectedDay.Value);
        if (cell is not null)
        {
            var seen = new HashSet<Guid>();
            foreach (var t in cell.CreatedTasks.Concat(cell.CompletedTasks).Concat(cell.DueTasks))
            {
                if (seen.Add(t.Id))
                {
                    SelectedDayItems.Add(t);
                }
            }
        }
        else
        {
            foreach (var e in MonthEntries.Where(x => x.Day == _selectedDay))
            {
                SelectedDayItems.Add(e.Record);
            }
        }

        RaisePropertyChanged(nameof(HasSelectedDayItems));
        RaisePropertyChanged(nameof(SelectedDayHeader));
    }
}
