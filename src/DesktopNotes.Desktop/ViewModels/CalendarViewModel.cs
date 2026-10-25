using System.Collections.ObjectModel;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Domain;

namespace DesktopNotes.Desktop.ViewModels;

public sealed class CalendarViewModel : ViewModelBase
{
    private readonly IRecordQueryService _queryService;
    private int _year = 2026;
    private int _month = 10;
    private CalendarAxis _axis = CalendarAxis.Due;
    private RecordKind _kind = RecordKind.Task;
    private RecordStatus? _statusFilter = null;
    private DateOnly? _selectedDay;

    public ObservableCollection<CalendarEntry> MonthEntries { get; } = new();
    public ObservableCollection<RecordView> SelectedDayItems { get; } = new();
    public ObservableCollection<RecordView> DatelessItems { get; } = new();

    public int Year
    {
        get => _year;
        set => SetProperty(ref _year, value);
    }

    public int Month
    {
        get => _month;
        set => SetProperty(ref _month, value);
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

    public CalendarViewModel(IRecordQueryService queryService)
    {
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
    }

    public async Task LoadMonthAsync(int year, int month, CalendarAxis axis, RecordKind kind, RecordStatus? statusFilter, CancellationToken ct)
    {
        _year = year;
        _month = month;
        _axis = axis;
        _kind = kind;
        _statusFilter = statusFilter;

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

        RebuildSelectedDayItems();
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
