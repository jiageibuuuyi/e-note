using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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
    private const string LockedIconPath = "M 18 8 L 17 8 L 17 6 A 5 5 0 0 0 7 6 L 7 8 L 6 8 A 2 2 0 0 0 4 10 L 4 20 A 2 2 0 0 0 6 22 L 18 22 A 2 2 0 0 0 20 20 L 20 10 A 2 2 0 0 0 18 8 Z M 9 6 A 3 3 0 0 1 15 6 L 15 8 L 9 8 L 9 6 Z";
    private const string UnlockedIconPath = "M 18 8 L 17 8 L 17 6 A 5 5 0 0 0 7 6 L 9 6 A 3 3 0 0 1 15 6 L 15 8 L 6 8 A 2 2 0 0 0 4 10 L 4 20 A 2 2 0 0 0 6 22 L 18 22 A 2 2 0 0 0 20 20 L 20 10 A 2 2 0 0 0 18 8 Z";

    private readonly Func<DetailViewModel>? _detailFactory;
    private readonly Func<CalendarViewModel>? _calendarFactory;
    private readonly Func<SettingsViewModel>? _settingsFactory;
    private readonly SettingsService? _settingsService;
    private readonly IDesktopIntegration? _desktopIntegration;
    private readonly BackupService? _backupService;
    private readonly DispatcherTimer _deadlineRefreshTimer;
    private CancellationTokenSource? _placementSaveCts;
    private bool _isPositionLocked;
    private PixelPoint _lockedPosition;
    private bool _isRestoringLockedPosition;

    public bool AllowExit { get; set; }

    public PanelWindow()
    {
        InitializeComponent();
        _deadlineRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(60)
        };
        if (OperatingSystem.IsWindows())
        {
            Win32Properties.AddWndProcHookCallback(this, OnWndProc);
        }
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
            HeaderDragRegion.AddHandler(
                InputElement.PointerReleasedEvent,
                OnHeaderPointerReleased,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
                handledEventsToo: true);
        }

        if (HeaderContextMenu is not null)
        {
            HeaderContextMenu.Opening += (_, _) => UpdateMenuStatusIndicators();
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

        if (BtnArchiveCompleted is not null)
        {
            BtnArchiveCompleted.Click += async (_, _) => await viewModel.ConfirmArchiveAllCompletedAsync(CancellationToken.None);
        }

        if (CtxArchiveAllCompleted is not null)
        {
            CtxArchiveAllCompleted.Click += async (_, _) => await viewModel.ConfirmArchiveAllCompletedAsync(CancellationToken.None);
        }

        if (MenuArchiveAllCompleted is not null)
        {
            MenuArchiveAllCompleted.Click += async (_, _) => await viewModel.ConfirmArchiveAllCompletedAsync(CancellationToken.None);
        }

        if (BtnLockToggle is not null)
        {
            BtnLockToggle.Click += (_, _) => TogglePositionLock(viewModel);
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
            MenuToggleLock.Click += (_, _) => TogglePositionLock(viewModel);
        }

        // Top Bar Right-Click ContextMenu Bindings
        if (CtxToggleLock is not null)
        {
            CtxToggleLock.Click += (_, _) => TogglePositionLock(viewModel);
        }

        if (CtxPlaceTopmost is not null)
        {
            CtxPlaceTopmost.Click += (_, _) =>
            {
                ApplyPlacementMode(AppPlacementMode.Topmost);
                viewModel.SetTransientNotice("已切换至: 悬浮在所有应用最上层 (置顶)");
            };
        }

        if (CtxPlaceBottom is not null)
        {
            CtxPlaceBottom.Click += (_, _) =>
            {
                ApplyPlacementMode(AppPlacementMode.EmbeddedDesktop);
                viewModel.SetTransientNotice("已切换至: 置于所有应用最下层 (桌面底层)");
            };
        }

        if (CtxSortDefault is not null)
        {
            CtxSortDefault.Click += (_, _) => viewModel.SetSortDefault();
        }

        if (CtxSortDeadline is not null)
        {
            CtxSortDeadline.Click += (_, _) => viewModel.SetSortByDeadline();
        }

        if (CtxSortPriority is not null)
        {
            CtxSortPriority.Click += (_, _) => viewModel.SetSortByPriority();
        }

        if (CtxSortCreated is not null)
        {
            CtxSortCreated.Click += (_, _) => viewModel.SetSortByCreatedDesc();
        }

        if (CtxSwitchTasks is not null)
        {
            CtxSwitchTasks.Click += (_, _) => viewModel.CurrentKind = RecordKind.Task;
        }

        if (CtxSwitchNotes is not null)
        {
            CtxSwitchNotes.Click += (_, _) => viewModel.CurrentKind = RecordKind.Note;
        }

        if (CtxThemeToggle is not null)
        {
            CtxThemeToggle.Click += (_, _) => ToggleTheme();
        }

        if (CtxOpenSettings is not null)
        {
            CtxOpenSettings.Click += (_, _) => OpenManagerWindow(initialTabIndex: 2);
        }

        if (CtxOpenCalendar is not null)
        {
            CtxOpenCalendar.Click += (_, _) => OpenManagerWindow(initialTabIndex: 0);
        }

        if (CtxHideToTray is not null)
        {
            CtxHideToTray.Click += (_, _) => HideToTray();
        }

        if (CtxExitApp is not null)
        {
            CtxExitApp.Click += (_, _) => ExitApplication();
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
            BtnCalendar.Click += (_, _) => OpenManagerWindow(initialTabIndex: 0);
        }

        if (BtnOpenSettings is not null)
        {
            BtnOpenSettings.Click += (_, _) => OpenManagerWindow(initialTabIndex: 2);
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

    private nint OnWndProc(nint hWnd, uint msg, nint wParam, nint lParam, ref bool handled)
    {
        if (!_isPositionLocked)
        {
            return nint.Zero;
        }

        // WM_SYSCOMMAND = 0x0112, SC_MOVE = 0xF010, SC_SIZE = 0xF000
        if (msg == 0x0112)
        {
            long cmd = wParam.ToInt64() & 0xFFF0;
            if (cmd == 0xF010 || cmd == 0xF000)
            {
                handled = true;
                return nint.Zero;
            }
        }

        return nint.Zero;
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
            var prefs = _settingsService.GetPreferences();
            ApplyPlacementMode(prefs.PlacementMode, savePreference: false);
            ApplyLockState(prefs.PositionLocked, savePreference: false);
        }
    }

    public void ExitApplication()
    {
        AllowExit = true;
        _deadlineRefreshTimer.Stop();
        Close();
    }

    private void TogglePositionLock(PanelViewModel viewModel)
    {
        bool newLock = !_isPositionLocked;
        ApplyLockState(newLock, savePreference: true);
        viewModel.SetTransientNotice(newLock
            ? "🔒 已锁定窗口位置 (禁止拖动)"
            : "🔓 已解锁窗口位置 (可按住顶端拖动)");
    }

    public void ApplyLockState(bool locked, bool savePreference = true)
    {
        _isPositionLocked = locked;
        _lockedPosition = Position;
        CanResize = !locked;

        if (_settingsService is not null && savePreference)
        {
            var prefs = _settingsService.GetPreferences();
            _settingsService.SavePreferences(prefs with { PositionLocked = locked });
        }

        UpdateMenuStatusIndicators();
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

        UpdateMenuStatusIndicators();
    }

    private void UpdateMenuStatusIndicators()
    {
        if (_settingsService is not null)
        {
            var prefs = _settingsService.GetPreferences();
            _isPositionLocked = prefs.PositionLocked;
            bool isTopmost = prefs.PlacementMode == AppPlacementMode.Topmost;

            if (CtxPlaceTopmost is not null)
            {
                CtxPlaceTopmost.Header = isTopmost
                    ? "✓ 📌  悬浮在所有应用最上层 (置顶)"
                    : "📌  悬浮在所有应用最上层 (置顶)";
            }

            if (CtxPlaceBottom is not null)
            {
                CtxPlaceBottom.Header = !isTopmost
                    ? "✓ 🖥️  置于所有应用最下层 (桌面底层)"
                    : "🖥️  置于所有应用最下层 (桌面底层)";
            }

            if (MenuPlaceTopmost is not null)
            {
                MenuPlaceTopmost.Header = isTopmost
                    ? "✓ 📌 悬浮在所有应用最上层 (置顶)"
                    : "📌 悬浮在所有应用最上层 (置顶)";
            }

            if (MenuPlaceBottom is not null)
            {
                MenuPlaceBottom.Header = !isTopmost
                    ? "✓ 🖥️ 置于所有应用最下层 (桌面底层)"
                    : "🖥️ 置于所有应用最下层 (桌面底层)";
            }
        }

        string lockHeader = _isPositionLocked
            ? "✓ 🔒  已锁定窗口位置 (点击解锁)"
            : "🔒  锁定窗口位置 (禁止拖动)";

        if (CtxToggleLock is not null)
        {
            CtxToggleLock.Header = lockHeader;
        }

        if (MenuToggleLock is not null)
        {
            MenuToggleLock.Header = lockHeader;
        }

        if (IconLockState is not null)
        {
            IconLockState.Data = StreamGeometry.Parse(_isPositionLocked ? LockedIconPath : UnlockedIconPath);
            if (this.TryFindResource(_isPositionLocked ? "AccentPrimary" : "TextSecondary", ActualThemeVariant, out var brushObj)
                && brushObj is IBrush brush)
            {
                IconLockState.Foreground = brush;
            }
        }

        if (BtnLockToggle is not null)
        {
            ToolTip.SetTip(BtnLockToggle, _isPositionLocked
                ? "当前已锁定窗口位置 (不可拖动) · 点击解锁"
                : "点击锁定窗口位置 (锁定时禁止拖动)");
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
            ApplyLockState(prefs.PositionLocked, savePreference: false);
        }
        else
        {
            Topmost = true;
            UpdateMenuStatusIndicators();
        }
    }

    private void OnWindowPositionOrSizeChanged(object? sender, EventArgs e)
    {
        if (_settingsService is null || !IsVisible)
        {
            return;
        }

        if (_isPositionLocked)
        {
            if (!_isRestoringLockedPosition && Position != _lockedPosition)
            {
                _isRestoringLockedPosition = true;
                Position = _lockedPosition;
                _isRestoringLockedPosition = false;
            }
            return;
        }

        _lockedPosition = Position;

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
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (_isPositionLocked || (_settingsService is not null && _settingsService.GetPreferences().PositionLocked))
        {
            _isPositionLocked = true;
            if (DataContext is PanelViewModel vm)
            {
                vm.SetTransientNotice("🔒 窗口已锁定，不可拖动 (右键顶端可设置解锁)");
            }
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
    }

    private void OnHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Right && HeaderContextMenu is not null && HeaderDragRegion is not null)
        {
            UpdateMenuStatusIndicators();
            HeaderContextMenu.Open(HeaderDragRegion);
            e.Handled = true;
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

    public void OpenManagerWindow(int initialTabIndex = 0)
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
            mode => ApplyPlacementMode(mode),
            locked => ApplyLockState(locked, savePreference: false),
            initialTabIndex);
        mgrWin.Show(this);
    }
}
