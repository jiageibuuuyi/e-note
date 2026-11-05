using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DesktopNotes.Application.Backup;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Settings;
using DesktopNotes.Desktop.Localization;
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
    private AppPlacementMode _currentPlacementMode = AppPlacementMode.Topmost;

    public bool AllowExit { get; set; }

    public PanelWindow()
    {
        InitializeComponent();
        ShowInTaskbar = false;
        try { Icon = App.CreateAppWindowIcon(); } catch { }
        _deadlineRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(60)
        };
        if (OperatingSystem.IsWindows())
        {
            Win32Properties.AddWndProcHookCallback(this, OnWndProc);
        }

        AddHandler(PointerPressedEvent, OnWindowTunnelPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnWindowTunnelPointerMoved, RoutingStrategies.Tunnel);
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

        if (PushpinRegion is not null)
        {
            PushpinRegion.PointerPressed += OnHeaderPointerPressed;
            PushpinRegion.AddHandler(
                InputElement.PointerReleasedEvent,
                OnHeaderPointerReleased,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
                handledEventsToo: true);
        }

        if (WindowFrameBorder is not null)
        {
            WindowFrameBorder.PointerPressed += OnWindowFramePointerPressed;
        }

        if (HeaderContextMenu is not null)
        {
            HeaderContextMenu.Opening += (_, _) => UpdateMenuStatusIndicators();
        }

        if (CtxUndoAction is not null)
        {
            CtxUndoAction.Click += async (_, _) => await viewModel.UndoLastActionAsync(CancellationToken.None);
        }

        void SetupHeaderPromoteDrop(Control control)
        {
            DragDrop.SetAllowDrop(control, true);
            control.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            {
                if (Controls.TaskRow.ActiveDraggedTaskId.HasValue)
                {
                    e.DragEffects = DragDropEffects.Move;
                    e.Handled = true;
                }
            });
            control.AddHandler(DragDrop.DropEvent, async (_, e) =>
            {
                if (Controls.TaskRow.ActiveDraggedTaskId is { } draggedId)
                {
                    await viewModel.PromoteSubtaskToRootAsync(draggedId, CancellationToken.None);
                    e.Handled = true;
                }
            });
        }

        if (PushpinRegion is not null) SetupHeaderPromoteDrop(PushpinRegion);
        if (HeaderDragRegion is not null) SetupHeaderPromoteDrop(HeaderDragRegion);

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
                bool willBeTopmost = _currentPlacementMode != AppPlacementMode.Topmost;
                TogglePlacementMode(AppPlacementMode.Topmost);
                viewModel.SetTransientNotice(willBeTopmost
                    ? AppLocalizer.Instance.T("📌 Mode: Always on Top", "已切换至: 悬浮在所有应用最上层 (置顶)")
                    : AppLocalizer.Instance.T("🪟 Mode: Standard Window", "已取消置顶: 恢复为标准应用层级 (正常窗口)"));
            };
        }

        if (MenuPlaceBottom is not null)
        {
            MenuPlaceBottom.Click += (_, _) =>
            {
                bool willBeBottom = _currentPlacementMode != AppPlacementMode.EmbeddedDesktop;
                TogglePlacementMode(AppPlacementMode.EmbeddedDesktop);
                viewModel.SetTransientNotice(willBeBottom
                    ? AppLocalizer.Instance.T("🖥️ Mode: Desktop Back", "已切换至: 置于所有应用最下层 (桌面底层)")
                    : AppLocalizer.Instance.T("🪟 Mode: Standard Window", "已取消桌面底层: 恢复为标准应用层级 (正常窗口)"));
            };
        }

        if (MenuPlaceNormal is not null)
        {
            MenuPlaceNormal.Click += (_, _) =>
            {
                ApplyPlacementMode(AppPlacementMode.Normal);
                viewModel.SetTransientNotice(AppLocalizer.Instance.T(
                    "🪟 Mode: Standard Window",
                    "已恢复为标准应用层级 (正常窗口)"));
            };
        }

        if (MenuToggleLock is not null)
        {
            MenuToggleLock.Click += (_, _) => TogglePositionLock(viewModel);
        }

        if (MenuLangEn is not null)
        {
            MenuLangEn.Click += (_, _) =>
            {
                viewModel.SetLanguage(AppLanguage.English);
                UpdateMenuStatusIndicators();
            };
        }

        if (MenuLangZh is not null)
        {
            MenuLangZh.Click += (_, _) =>
            {
                viewModel.SetLanguage(AppLanguage.Chinese);
                UpdateMenuStatusIndicators();
            };
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
                bool willBeTopmost = _currentPlacementMode != AppPlacementMode.Topmost;
                TogglePlacementMode(AppPlacementMode.Topmost);
                viewModel.SetTransientNotice(willBeTopmost
                    ? AppLocalizer.Instance.T("📌 Mode: Always on Top", "已切换至: 悬浮在所有应用最上层 (置顶)")
                    : AppLocalizer.Instance.T("🪟 Mode: Standard Window", "已取消置顶: 恢复为标准应用层级 (正常窗口)"));
            };
        }

        if (CtxPlaceBottom is not null)
        {
            CtxPlaceBottom.Click += (_, _) =>
            {
                bool willBeBottom = _currentPlacementMode != AppPlacementMode.EmbeddedDesktop;
                TogglePlacementMode(AppPlacementMode.EmbeddedDesktop);
                viewModel.SetTransientNotice(willBeBottom
                    ? AppLocalizer.Instance.T("🖥️ Mode: Desktop Back", "已切换至: 置于所有应用最下层 (桌面底层)")
                    : AppLocalizer.Instance.T("🪟 Mode: Standard Window", "已取消桌面底层: 恢复为标准应用层级 (正常窗口)"));
            };
        }

        if (CtxPlaceNormal is not null)
        {
            CtxPlaceNormal.Click += (_, _) =>
            {
                ApplyPlacementMode(AppPlacementMode.Normal);
                viewModel.SetTransientNotice(AppLocalizer.Instance.T(
                    "🪟 Mode: Standard Window",
                    "已恢复为标准应用层级 (正常窗口)"));
            };
        }

        if (CtxLangEn is not null)
        {
            CtxLangEn.Click += (_, _) =>
            {
                viewModel.SetLanguage(AppLanguage.English);
                UpdateMenuStatusIndicators();
            };
        }

        if (CtxLangZh is not null)
        {
            CtxLangZh.Click += (_, _) =>
            {
                viewModel.SetLanguage(AppLanguage.Chinese);
                UpdateMenuStatusIndicators();
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

        if (CtxStyleRealistic is not null)
        {
            CtxStyleRealistic.Click += (_, _) => viewModel.SetVisualStyle(AppVisualStyle.Realistic);
        }

        if (CtxStyleMinimalist is not null)
        {
            CtxStyleMinimalist.Click += (_, _) => viewModel.SetVisualStyle(AppVisualStyle.Minimalist);
        }

        if (FlyoutStyleRealistic is not null)
        {
            FlyoutStyleRealistic.Click += (_, _) => viewModel.SetVisualStyle(AppVisualStyle.Realistic);
        }

        if (FlyoutStyleMinimalist is not null)
        {
            FlyoutStyleMinimalist.Click += (_, _) => viewModel.SetVisualStyle(AppVisualStyle.Minimalist);
        }



        if (BtnOpenArchiveBox is not null)
        {
            BtnOpenArchiveBox.Click += (_, _) => OpenManagerWindow(initialTabIndex: 1);
        }

        AppLocalizer.Instance.VisualStyleChanged += ApplyVisualStyle;
        ApplyVisualStyle();

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
        AppLocalizer.Instance.LanguageChanged += UpdateMenuStatusIndicators;

        _deadlineRefreshTimer.Tick += async (_, _) =>
        {
            // Never refresh the list while the user is actively typing in an inline draft row
            if (viewModel.Items.Any(x => x.IsEditingInline))
            {
                return;
            }
            await viewModel.RefreshAsync(CancellationToken.None);
        };
        _deadlineRefreshTimer.Start();

        KeyDown += async (_, e) =>
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
            else if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                await viewModel.UndoLastActionAsync(CancellationToken.None);
                e.Handled = true;
            }
        };

        viewModel.OpenDetailRequested += async id => await OpenDetailWindowAsync(id);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PanelViewModel.ExpandedRecordId) &&
                viewModel.ExpandedRecordId is { } expId)
            {
                var expItem = viewModel.Items.FirstOrDefault(x => x.Id == expId);
                if (expItem is not null)
                {
                    int idx = viewModel.Items.IndexOf(expItem);
                    bool isNearBottom = idx >= viewModel.Items.Count - 2;
                    Dispatcher.UIThread.Post(() =>
                    {
                        RecordListBox?.ScrollIntoView(expItem);
                        if (isNearBottom && RecordListBox?.FindDescendantOfType<ScrollViewer>() is { } sv)
                        {
                            sv.ScrollToEnd();
                        }
                    }, DispatcherPriority.Background);
                }
            }
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public nint hwnd;
        public nint hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    private nint OnWndProc(nint hWnd, uint msg, nint wParam, nint lParam, ref bool handled)
    {
        // When in EmbeddedDesktop (desktop bottom layer), enforce HWND_BOTTOM so no click or restore brings it to top
        if (_currentPlacementMode == AppPlacementMode.EmbeddedDesktop)
        {
            if (msg == 0x0046 /* WM_WINDOWPOSCHANGING */ && lParam != nint.Zero)
            {
                try
                {
                    var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                    wp.hwndInsertAfter = (nint)1; // HWND_BOTTOM
                    Marshal.StructureToPtr(wp, lParam, false);
                }
                catch
                {
                }
            }
            else if (msg == 0x0021 /* WM_MOUSEACTIVATE */)
            {
                handled = true;
                return 3; // MA_NOACTIVATE: process mouse clicks without activating above normal windows
            }
        }

        if (_isPositionLocked)
        {
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

        // WM_NCHITTEST = 0x0084
        if (msg == 0x0084 && CanResize)
        {
            if (GetWindowRect(hWnd, out RECT rect))
            {
                int screenX = unchecked((short)(lParam.ToInt64() & 0xFFFF));
                int screenY = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
                const int border = 10;

                bool isLeft = screenX >= rect.Left && screenX < rect.Left + border;
                bool isRight = screenX <= rect.Right && screenX > rect.Right - border;
                bool isTop = screenY >= rect.Top && screenY < rect.Top + border;
                bool isBottom = screenY <= rect.Bottom && screenY > rect.Bottom - border;

                if (isTop && isLeft) { handled = true; return 13; } // HTTOPLEFT
                if (isTop && isRight) { handled = true; return 14; } // HTTOPRIGHT
                if (isBottom && isLeft) { handled = true; return 16; } // HTBOTTOMLEFT
                if (isBottom && isRight) { handled = true; return 17; } // HTBOTTOMRIGHT
                if (isLeft) { handled = true; return 10; } // HTLEFT
                if (isRight) { handled = true; return 11; } // HTRIGHT
                if (isTop) { handled = true; return 12; } // HTTOP
                if (isBottom) { handled = true; return 15; } // HTBOTTOM
            }
        }

        return nint.Zero;
    }

    private WindowEdge? GetResizeEdge(Point pos)
    {
        if (_isPositionLocked || !CanResize)
        {
            return null;
        }

        const double thickness = 10.0;
        double w = Bounds.Width;
        double h = Bounds.Height;

        if (w <= 0 || h <= 0)
        {
            return null;
        }

        bool left = pos.X >= 0 && pos.X <= thickness;
        bool right = pos.X >= w - thickness && pos.X <= w;
        bool top = pos.Y >= 0 && pos.Y <= thickness;
        bool bottom = pos.Y >= h - thickness && pos.Y <= h;

        if (top && left) return WindowEdge.NorthWest;
        if (top && right) return WindowEdge.NorthEast;
        if (bottom && left) return WindowEdge.SouthWest;
        if (bottom && right) return WindowEdge.SouthEast;
        if (top) return WindowEdge.North;
        if (bottom) return WindowEdge.South;
        if (left) return WindowEdge.West;
        if (right) return WindowEdge.East;

        return null;
    }

    private void OnWindowTunnelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (!_isPositionLocked && CanResize)
        {
            var edge = GetResizeEdge(e.GetPosition(this));
            if (edge.HasValue)
            {
                BeginResizeDrag(edge.Value, e);
                e.Handled = true;
            }
        }
    }

    private void OnWindowTunnelPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isPositionLocked || !CanResize)
        {
            Cursor = Cursor.Default;
            return;
        }

        var edge = GetResizeEdge(e.GetPosition(this));
        Cursor = edge switch
        {
            WindowEdge.NorthWest => new Cursor(StandardCursorType.TopLeftCorner),
            WindowEdge.NorthEast => new Cursor(StandardCursorType.TopRightCorner),
            WindowEdge.SouthWest => new Cursor(StandardCursorType.BottomLeftCorner),
            WindowEdge.SouthEast => new Cursor(StandardCursorType.BottomRightCorner),
            WindowEdge.North => new Cursor(StandardCursorType.TopSide),
            WindowEdge.South => new Cursor(StandardCursorType.BottomSide),
            WindowEdge.West => new Cursor(StandardCursorType.LeftSide),
            WindowEdge.East => new Cursor(StandardCursorType.RightSide),
            _ => Cursor.Default
        };
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

        var mode = _currentPlacementMode;
        if (_settingsService is not null)
        {
            var prefs = _settingsService.GetPreferences();
            mode = prefs.PlacementMode;
            ApplyLockState(prefs.PositionLocked, savePreference: false);
        }

        if (mode == AppPlacementMode.EmbeddedDesktop)
        {
            // Do NOT call Activate() which brings it to the top!
            ApplyPlacementMode(AppPlacementMode.EmbeddedDesktop, savePreference: false);
        }
        else
        {
            Activate();
            ApplyPlacementMode(mode, savePreference: false);
        }
    }

    public void ExitApplication()
    {
        AllowExit = true;
        _deadlineRefreshTimer.Stop();
        Close();
    }

    private void ApplyVisualStyle()

    {

        bool isRealistic = AppLocalizer.Instance.IsRealisticStyle;

        Classes.Set("realistic", isRealistic);

        Classes.Set("minimalist", !isRealistic);

        UpdateMenuStatusIndicators();

    }



    private void TogglePositionLock(PanelViewModel viewModel)
    {
        bool newLock = !_isPositionLocked;
        ApplyLockState(newLock, savePreference: true);
        viewModel.SetTransientNotice(newLock
            ? AppLocalizer.Instance.T("🔒 Window position locked", "🔒 已锁定窗口位置 (禁止拖动)")
            : AppLocalizer.Instance.T("🔓 Window unlocked (drag top bar to move)", "🔓 已解锁窗口位置 (可按住顶端拖动)"));
    }

    public void ApplyLockState(bool locked, bool savePreference = true)
    {
        _isPositionLocked = locked;
        _lockedPosition = Position;
        CanResize = !locked;
        if (locked)
        {
            Cursor = Cursor.Default;
        }

        if (_settingsService is not null && savePreference)
        {
            var prefs = _settingsService.GetPreferences();
            _settingsService.SavePreferences(prefs with { PositionLocked = locked });
        }

        UpdateMenuStatusIndicators();
    }

    public AppPlacementMode CurrentPlacementMode => _currentPlacementMode;
    public event Action<AppPlacementMode>? PlacementModeChanged;

    public void TogglePlacementMode(AppPlacementMode targetMode)
    {
        var newMode = _currentPlacementMode == targetMode
            ? AppPlacementMode.Normal
            : targetMode;
        ApplyPlacementMode(newMode);
    }

    public void ApplyPlacementMode(AppPlacementMode mode, bool savePreference = true)
    {
        _currentPlacementMode = mode;
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
        PlacementModeChanged?.Invoke(mode);
    }

    private void UpdateMenuStatusIndicators()
    {
        var loc = AppLocalizer.Instance;
        if (_settingsService is not null)
        {
            var prefs = _settingsService.GetPreferences();
            _isPositionLocked = prefs.PositionLocked;
            bool isTopmost = prefs.PlacementMode == AppPlacementMode.Topmost;
            bool isBottom = prefs.PlacementMode == AppPlacementMode.EmbeddedDesktop;
            bool isNormal = prefs.PlacementMode == AppPlacementMode.Normal;

            string topText = loc.T("📌  Always on Top", "📌  悬浮在所有应用最上层 (置顶)");
            string bottomText = loc.T("🖥️  Send to Desktop Back", "🖥️  置于所有应用最下层 (桌面底层)");
            string normalText = loc.T("🪟  Standard Window", "🪟  标准应用层级 (正常窗口)");

            if (CtxPlaceTopmost is not null)
            {
                CtxPlaceTopmost.Header = isTopmost ? $"✓ {topText}" : topText;
            }

            if (CtxPlaceBottom is not null)
            {
                CtxPlaceBottom.Header = isBottom ? $"✓ {bottomText}" : bottomText;
            }

            if (CtxPlaceNormal is not null)
            {
                CtxPlaceNormal.Header = isNormal ? $"✓ {normalText}" : normalText;
            }

            if (MenuPlaceTopmost is not null)
            {
                MenuPlaceTopmost.Header = isTopmost ? $"✓ {topText}" : topText;
            }

            if (MenuPlaceBottom is not null)
            {
                MenuPlaceBottom.Header = isBottom ? $"✓ {bottomText}" : bottomText;
            }

            if (MenuPlaceNormal is not null)
            {
                MenuPlaceNormal.Header = isNormal ? $"✓ {normalText}" : normalText;
            }
        }

        bool isZh = loc.IsChinese;
        if (CtxLangEn is not null)
        {
            CtxLangEn.Header = !isZh ? "✓ English (Default)" : "English (Default)";
        }
        if (CtxLangZh is not null)
        {
            CtxLangZh.Header = isZh ? "✓ 中文 (Chinese)" : "中文 (Chinese)";
        }
        if (MenuLangEn is not null)
        {
            MenuLangEn.Header = !isZh ? "✓ English (Default)" : "English (Default)";
        }
        if (MenuLangZh is not null)
        {
            MenuLangZh.Header = isZh ? "✓ 中文 (Chinese)" : "中文 (Chinese)";
        }

        if (CtxStyleRealistic is not null)

        {

            CtxStyleRealistic.Header = loc.IsRealisticStyle ? $"✓ {loc.MenuStyleRealistic}" : loc.MenuStyleRealistic;

        }

        if (CtxStyleMinimalist is not null)

        {

            CtxStyleMinimalist.Header = loc.IsMinimalistStyle ? $"✓ {loc.MenuStyleMinimalist}" : loc.MenuStyleMinimalist;

        }

        if (FlyoutStyleRealistic is not null)

        {

            FlyoutStyleRealistic.Header = loc.IsRealisticStyle ? $"✓ {loc.MenuStyleRealistic}" : loc.MenuStyleRealistic;

        }

        if (FlyoutStyleMinimalist is not null)

        {

            FlyoutStyleMinimalist.Header = loc.IsMinimalistStyle ? $"✓ {loc.MenuStyleMinimalist}" : loc.MenuStyleMinimalist;

        }



        if (loc.IsRealisticStyle)

        {

            if (PinNeedlePath is not null) PinNeedlePath.IsVisible = _isPositionLocked;

            if (PinShadowEllipse is not null) PinShadowEllipse.IsVisible = _isPositionLocked;

            if (PinHolePath is not null) PinHolePath.IsVisible = _isPositionLocked;

            if (DoodleLockBody is not null)

            {

                DoodleLockBody.Background = _isPositionLocked

                    ? new SolidColorBrush(Color.Parse("#FF758F"))

                    : new SolidColorBrush(Color.Parse("#A8E6CF"));

            }

            if (RootScenePanel is not null)

            {

                RootScenePanel.RenderTransform = new RotateTransform(_isPositionLocked ? 0 : -1.2);

            }

        }

        else

        {

            if (RootScenePanel is not null)

            {

                RootScenePanel.RenderTransform = new RotateTransform(0);

            }

        }



        string lockHeader = _isPositionLocked
            ? loc.T("✓ 🔒  Window Locked (Click to Unlock)", "✓ 🔒  已锁定窗口位置 (点击解锁)")
            : loc.T("🔒  Lock Window Position", "🔒  锁定窗口位置 (禁止拖动)");

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
                ? loc.T("Window position is locked · Click to unlock", "当前已锁定窗口位置 (不可拖动) · 点击解锁")
                : loc.T("Click to lock window position", "点击锁定窗口位置 (锁定时禁止拖动)"));
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

    private void OnWindowFramePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var resizeEdge = GetResizeEdge(e.GetPosition(this));
        if (resizeEdge.HasValue && !_isPositionLocked && CanResize)
        {
            BeginResizeDrag(resizeEdge.Value, e);
            e.Handled = true;
            return;
        }

        // Only trigger move drag if clicking on non-interactive regions (like background paper, headers, empty margins)
        if (e.Source is Visual visual)
        {
            if (visual.FindAncestorOfType<Button>() is not null ||
                visual.FindAncestorOfType<TextBox>() is not null ||
                visual.FindAncestorOfType<ListBoxItem>() is not null ||
                visual.FindAncestorOfType<ContextMenu>() is not null ||
                visual.FindAncestorOfType<MenuItem>() is not null ||
                visual.FindAncestorOfType<FlyoutPresenter>() is not null)
            {
                return;
            }
        }

        OnHeaderPointerPressed(sender, e);
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
                vm.SetTransientNotice(AppLocalizer.Instance.T(
                    "🔒 Window is locked (right-click top bar to unlock)",
                    "🔒 窗口已锁定，不可拖动 (右键顶端可设置解锁)"));
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
            try
            {
                if (!HeaderContextMenu.IsOpen)
                {
                    HeaderContextMenu.Open(HeaderDragRegion);
                }
            }
            catch
            {
            }
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
