using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using DesktopNotes.Desktop.ViewModels;
using DesktopNotes.Domain;

namespace DesktopNotes.Desktop.Views;

public partial class PanelWindow : Window
{
    private readonly Func<DetailViewModel>? _detailFactory;
    private readonly Func<CalendarViewModel>? _calendarFactory;
    private readonly Func<SettingsViewModel>? _settingsFactory;

    public PanelWindow()
    {
        InitializeComponent();
    }

    public PanelWindow(
        PanelViewModel viewModel,
        Func<DetailViewModel>? detailFactory = null,
        Func<CalendarViewModel>? calendarFactory = null,
        Func<SettingsViewModel>? settingsFactory = null) : this()
    {
        DataContext = viewModel;
        _detailFactory = detailFactory;
        _calendarFactory = calendarFactory;
        _settingsFactory = settingsFactory;

        if (HeaderDragRegion is not null)
        {
            HeaderDragRegion.PointerPressed += OnHeaderPointerPressed;
        }

        if (BtnKindSelector is not null)
        {
            BtnKindSelector.Click += (_, _) => viewModel.ToggleKind();
        }

        if (BtnTabTasks is not null)
        {
            BtnTabTasks.Click += (_, _) => viewModel.CurrentKind = RecordKind.Task;
        }

        if (BtnTabNotes is not null)
        {
            BtnTabNotes.Click += (_, _) => viewModel.CurrentKind = RecordKind.Note;
        }

        if (BtnSearchToggle is not null)
        {
            BtnSearchToggle.Click += (_, _) =>
            {
                viewModel.IsSearchOpen = !viewModel.IsSearchOpen;
                if (viewModel.IsSearchOpen)
                {
                    SearchBox?.Focus();
                }
            };
        }

        if (BtnClearSearch is not null)
        {
            BtnClearSearch.Click += async (_, _) => await viewModel.ClearSearchAsync();
        }

        if (SearchBox is not null)
        {
            SearchBox.KeyDown += async (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    await viewModel.ClearSearchAsync();
                    e.Handled = true;
                }
            };
        }

        if (QuickInputBox is not null)
        {
            QuickInputBox.KeyDown += async (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    await viewModel.QuickCreateAsync(viewModel.QuickInputText, CancellationToken.None);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    viewModel.QuickInputText = string.Empty;
                    e.Handled = true;
                }
            };
        }

        if (BtnSubmitQuick is not null)
        {
            BtnSubmitQuick.Click += async (_, _) =>
                await viewModel.QuickCreateAsync(viewModel.QuickInputText, CancellationToken.None);
        }

        if (BtnQuickAdd is not null)
        {
            BtnQuickAdd.Click += async (_, _) => await OpenDetailWindowAsync(null);
        }

        if (BtnCalendar is not null)
        {
            BtnCalendar.Click += (_, _) => OpenManagerWindow();
        }

        if (BtnThemeToggle is not null)
        {
            BtnThemeToggle.Click += (_, _) => ToggleTheme();
        }

        if (BtnCloseWindow is not null)
        {
            BtnCloseWindow.Click += (_, _) => Close();
        }

        viewModel.OpenDetailRequested += async id => await OpenDetailWindowAsync(id);
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private static void ToggleTheme()
    {
        if (global::Avalonia.Application.Current is { } app)
        {
            app.RequestedThemeVariant = app.ActualThemeVariant == ThemeVariant.Dark
                ? ThemeVariant.Light
                : ThemeVariant.Dark;
        }
    }

    private async Task OpenDetailWindowAsync(Guid? recordId)
    {
        if (_detailFactory is null || DataContext is not PanelViewModel panelVm)
        {
            return;
        }

        var detailVm = _detailFactory();
        if (recordId.HasValue)
        {
            await detailVm.LoadAsync(recordId.Value, CancellationToken.None);
        }
        else
        {
            await detailVm.LoadNewAsync(panelVm.CurrentKind, null, CancellationToken.None);
        }

        var detailWin = new DetailWindow(detailVm);
        await detailWin.ShowDialog(this);
        await panelVm.RefreshAsync(CancellationToken.None);
    }

    private void OpenManagerWindow()
    {
        var managerWin = new ManagerWindow(_calendarFactory?.Invoke(), _settingsFactory?.Invoke());
        managerWin.Show(this);
    }
}
