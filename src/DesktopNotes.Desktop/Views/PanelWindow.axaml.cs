using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using DesktopNotes.Application.Backup;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Settings;
using DesktopNotes.Desktop.ViewModels;
using DesktopNotes.Domain;
using AppPlacementMode = DesktopNotes.Application.Contracts.PlacementMode;

namespace DesktopNotes.Desktop.Views;

public partial class PanelWindow : Window
{
    private readonly Func<DetailViewModel>? _detailFactory;
    private readonly Func<CalendarViewModel>? _calendarFactory;
    private readonly Func<SettingsViewModel>? _settingsFactory;
    private readonly SettingsService? _settingsService;
    private readonly IDesktopIntegration? _desktopIntegration;
    private readonly BackupService? _backupService;
    private readonly DispatcherTimer _deadlineRefreshTimer;
    private CancellationTokenSource? _placementSaveCts;

    public bool AllowExit { get; set; }

    public PanelWindow()
    {
        InitializeComponent();
        _deadlineRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(60)
        };
    }

    public PanelWindow(
        PanelViewModel viewModel,
        Func<DetailViewModel>? detailFactory = null,
        Func<CalendarViewModel>? calendarFactory = null,
        Func<SettingsViewModel>? settingsFactory = null,
        SettingsService? settingsService = null,
        IDesktopIntegration? desktopIntegration = null,
        BackupService? backupService = null) : this()
    {
        DataContext = viewModel;
        _detailFactory = detailFactory;
        _calendarFactory = calendarFactory;
        _settingsFactory = settingsFactory;
        _settingsService = settingsService;
        _desktopIntegration = desktopIntegration;
        _backupService = backupService;

        if (HeaderDragRegion is not null)
        {
            HeaderDragRegion.PointerPressed += OnHeaderPointerPressed;
        }

        if (BtnKindSelector is not null)
        {
            BtnKindSelector.Click += (_, _) => viewModel.ToggleKind();
        }

        if (BtnAddInline is not null)
        {
            BtnAddInline.Click += (_, _) => TriggerInlineAdd(viewModel);
        }

        if (BtnEmptyAdd is not null)
        {
            BtnEmptyAdd.Click += (_, _) => TriggerInlineAdd(viewModel);
        }

        if (BtnBottomAdd is not null)
        {
            BtnBottomAdd.Click += (_, _) => TriggerInlineAdd(viewModel);
        }

        if (BtnTabTasks is not null)
        {
            BtnTabTasks.Click += (_, _) => viewModel.CurrentKind = RecordKind.Task;
        }

        if (BtnTabNotes is not null)
        {
            BtnTabNotes.Click += (_, _) => viewModel.CurrentKind = RecordKind.Note;
        }

        if (MenuSortDefault is not null)
        {
            MenuSortDefault.Click += (_, _) => viewModel.SetSortDefault();
        }

        if (MenuSortDeadline is not null)
        {
            MenuSortDeadline.Click += (_, _) => viewModel.SetSortByDeadline();
        }

        if (MenuSortPriority is not null)
        {
            MenuSortPriority.Click += (_, _) => viewModel.SetSortByPriority();
        }

        if (MenuSortCreated is not null)
        {
            MenuSortCreated.Click += (_, _) => viewModel.SetSortByCreatedDesc();
        }

        if (MenuPlaceTopmost is not null)
        {
            MenuPlaceTopmost.Click += (_, _) =>
            {
                ApplyPlacementMode(AppPlacementMode.Topmost);
                viewModel.SetTransientNotice("已切换至: 悬浮在所有应用最上层 (置顶)");
            };
        }

        if (MenuPlaceBottom is not null)
        {
            MenuPlaceBottom.Click += (_, _) =>
            {
                ApplyPlacementMode(AppPlacementMode.EmbeddedDesktop);
                viewModel.SetTransientNotice("已切换至: 置于所有应用最下层 (桌面底层)");
            };
        }

        if (MenuToggleLock is not null)
        {
            MenuToggleLock.Click += (_, _) =>
            {
                if (_settingsService is not null)
                {
                    var prefs = _settingsService.GetPreferences();
                    bool newLock = !prefs.PositionLocked;
                    _settingsService.SavePreferences(prefs with { PositionLocked = newLock });
                    viewModel.SetTransientNotice(newLock ? "🔒 已锁定当前桌面位置" : "🔓 已解锁桌面位置");
                }
            };
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

        if (MenuHideToTray is not null)
        {
            MenuHideToTray.Click += (_, _) => HideToTray();
        }

        if (MenuExitApp is not null)
        {
            MenuExitApp.Click += (_, _) => ExitApplication();
        }

        if (BtnCloseWindow is not null)
        {
            BtnCloseWindow.Click += (_, _) => HideToTray();
        }

        Opened += OnWindowOpened;
        PositionChanged += OnWindowPositionOrSizeChanged;
        SizeChanged += OnWindowPositionOrSizeChanged;

        _deadlineRefreshTimer.Tick += async (_, _) =>
        {
            await viewModel.RefreshAsync(CancellationToken.None);
        };
        _deadlineRefreshTimer.Start();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.N && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                TriggerInlineAdd(viewModel);
                e.Handled = true;
            }
            else if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                viewModel.IsSearchOpen = true;
                SearchBox?.Focus();
                e.Handled = true;
            }
        };

        viewModel.OpenDetailRequested += async id => await OpenDetailWindowAsync(id);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowExit && _settingsService is not null)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _deadlineRefreshTimer.Stop();
        base.OnClosing(e);
    }

    public void HideToTray()
    {
        Hide();
    }

    public void RestoreFromTray()
    {
        if (!IsVisible)
        {
            Show();
        }
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        if (_settingsService is not null)
        {
            ApplyPlacementMode(_settingsService.GetPreferences().PlacementMode, savePreference: false);
        }
    }

    public void ExitApplication()
    {
        AllowExit = true;
        _deadlineRefreshTimer.Stop();
        Close();
    }

    public void ApplyPlacementMode(AppPlacementMode mode, bool savePreference = true)
    {
        if (_settingsService is not null && savePreference)
        {
            var prefs = _settingsService.GetPreferences();
            _settingsService.SavePreferences(prefs with { PlacementMode = mode });
        }

        Topmost = mode == AppPlacementMode.Topmost;

        if (_desktopIntegration is not null && TryGetPlatformHandle()?.Handle is { } hwnd && hwnd != nint.Zero)
        {
            try
            {
                _ = _desktopIntegration.ApplyModeAsync(hwnd, mode, CancellationToken.None);
            }
            catch
            {
                // Fallback if desktop host attachment is unavailable
            }
        }
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        if (_settingsService is not null)
        {
            var placement = _settingsService.GetWindowPlacement();
            Position = new PixelPoint((int)placement.X, (int)placement.Y);
            if (placement.Width >= MinWidth) Width = placement.Width;
            if (placement.Height >= MinHeight) Height = placement.Height;

            var prefs = _settingsService.GetPreferences();
            ApplyPlacementMode(prefs.PlacementMode, savePreference: false);
        }
        else
        {
            Topmost = true;
        }
    }

    private void OnWindowPositionOrSizeChanged(object? sender, EventArgs e)
    {
        if (_settingsService is null || !IsVisible)
        {
            return;
        }

        _placementSaveCts?.Cancel();
        _placementSaveCts?.Dispose();
        var cts = new CancellationTokenSource();
        _placementSaveCts = cts;

        var pos = Position;
        double w = Width > 0 ? Width : 350;
        double h = Height > 0 ? Height : 520;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, cts.Token);
                _settingsService.SaveWindowPlacement(new WindowPlacement("primary", pos.X, pos.Y, w, h, 1.0));
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public void TriggerInlineAddFromOutside()
    {
        if (DataContext is PanelViewModel vm)
        {
            RestoreFromTray();
            TriggerInlineAdd(vm);
        }
    }

    private void TriggerInlineAdd(PanelViewModel viewModel)
    {
        var draft = viewModel.StartInlineCreate();
        Dispatcher.UIThread.Post(() =>
        {
            RecordListBox?.ScrollIntoView(draft);
        }, DispatcherPriority.Loaded);
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_settingsService is not null && _settingsService.GetPreferences().PositionLocked)
        {
            if (DataContext is PanelViewModel vm)
            {
                vm.SetTransientNotice("🔒 窗口位置已锁定 (可在 ⋯ 菜单中解锁)");
            }
            return;
        }

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

    public void OpenManagerWindow()
    {
        if (_calendarFactory is null || _settingsFactory is null)
        {
            return;
        }

        RestoreFromTray();
        var calVm = _calendarFactory();
        var setVm = _settingsFactory();
        var mgrWin = new ManagerWindow(
            calVm,
            setVm,
            _backupService,
            mode => ApplyPlacementMode(mode));
        mgrWin.Show(this);
    }
}
