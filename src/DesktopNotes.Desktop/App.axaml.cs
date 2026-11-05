using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Desktop.Localization;
using DesktopNotes.Desktop.Views;
using AppPlacementMode = DesktopNotes.Application.Contracts.PlacementMode;

namespace DesktopNotes.Desktop;

public partial class App : global::Avalonia.Application
{
    private Bootstrapper? _bootstrapper;
    private TrayIcon? _trayIcon;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _bootstrapper = new Bootstrapper();
            _ = _bootstrapper.InitializeAsync();
            _ = _bootstrapper.PanelViewModel.RefreshAsync(CancellationToken.None);

            var panelWindow = new PanelWindow(
                _bootstrapper.PanelViewModel,
                _bootstrapper.CreateDetailViewModel,
                _bootstrapper.CreateCalendarViewModel,
                _bootstrapper.CreateSettingsViewModel,
                _bootstrapper.SettingsService,
                _bootstrapper.DesktopIntegration,
                _bootstrapper.BackupService);

            desktop.MainWindow = panelWindow;
            SetupTrayIcon(panelWindow);

            panelWindow.Closed += (_, _) =>
            {
                if (_trayIcon is not null)
                {
                    _trayIcon.IsVisible = false;
                    _trayIcon.Dispose();
                    _trayIcon = null;
                }
            };

            desktop.Exit += (_, _) =>
            {
                if (_trayIcon is not null)
                {
                    _trayIcon.IsVisible = false;
                    _trayIcon.Dispose();
                    _trayIcon = null;
                }
                _bootstrapper?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTrayIcon(PanelWindow panelWindow)
    {
        try
        {
            var loc = AppLocalizer.Instance;
            var menu = new NativeMenu();

            var itemShow = new NativeMenuItem(loc.T("Show / Hide Main Window", "显示 / 隐藏主面板"));
            itemShow.Click += (_, _) =>
            {
                if (panelWindow.IsVisible)
                {
                    panelWindow.HideToTray();
                }
                else
                {
                    panelWindow.RestoreFromTray();
                }
            };
            menu.Items.Add(itemShow);

            var itemNew = new NativeMenuItem(loc.T("New Task / Note", "新建待办 / 笔记"));
            itemNew.Click += (_, _) => panelWindow.TriggerInlineAddFromOutside();
            menu.Items.Add(itemNew);

            var itemCalendar = new NativeMenuItem(loc.T("Calendar · Archive · Settings", "任务日历 · 历史归档 · 设置"));
            itemCalendar.Click += (_, _) => panelWindow.OpenManagerWindow();
            menu.Items.Add(itemCalendar);

            menu.Items.Add(new NativeMenuItemSeparator());

            var itemTopmost = new NativeMenuItem(loc.T("Always on Top", "置于所有应用最上层 (置顶)"));
            var itemBottom = new NativeMenuItem(loc.T("Send to Desktop Back", "置于所有应用最下层 (桌面层)"));
            var itemNormal = new NativeMenuItem(loc.T("Standard Window", "标准窗口层级 (正常应用)"));

            void UpdateTrayPlacementHeaders(AppPlacementMode mode)
            {
                string topTitle = loc.T("Always on Top", "置于所有应用最上层 (置顶)");
                string bottomTitle = loc.T("Send to Desktop Back", "置于所有应用最下层 (桌面层)");
                string normalTitle = loc.T("Standard Window", "标准窗口层级 (正常应用)");
                itemTopmost.Header = mode == AppPlacementMode.Topmost ? $"✓ {topTitle}" : topTitle;
                itemBottom.Header = mode == AppPlacementMode.EmbeddedDesktop ? $"✓ {bottomTitle}" : bottomTitle;
                itemNormal.Header = mode == AppPlacementMode.Normal ? $"✓ {normalTitle}" : normalTitle;
            }

            itemTopmost.Click += (_, _) =>
            {
                panelWindow.TogglePlacementMode(AppPlacementMode.Topmost);
                UpdateTrayPlacementHeaders(panelWindow.CurrentPlacementMode);
            };

            itemBottom.Click += (_, _) =>
            {
                panelWindow.TogglePlacementMode(AppPlacementMode.EmbeddedDesktop);
                UpdateTrayPlacementHeaders(panelWindow.CurrentPlacementMode);
            };

            itemNormal.Click += (_, _) =>
            {
                panelWindow.ApplyPlacementMode(AppPlacementMode.Normal);
                UpdateTrayPlacementHeaders(panelWindow.CurrentPlacementMode);
            };

            panelWindow.PlacementModeChanged += mode => UpdateTrayPlacementHeaders(mode);
            UpdateTrayPlacementHeaders(panelWindow.CurrentPlacementMode);

            menu.Items.Add(itemTopmost);
            menu.Items.Add(itemBottom);
            menu.Items.Add(itemNormal);

            menu.Items.Add(new NativeMenuItemSeparator());

            var itemExit = new NativeMenuItem(loc.T("Quit Sticky Notes", "退出电子便签"));
            itemExit.Click += (_, _) => panelWindow.ExitApplication();
            menu.Items.Add(itemExit);

            _trayIcon = new TrayIcon
            {
                ToolTipText = loc.T("Sticky Notes (Click to show/hide, right-click for menu)", "电子便签 (单击显示/隐藏，右键菜单)"),
                Icon = CreateAppWindowIcon(),
                Menu = menu,
                IsVisible = true
            };

            loc.LanguageChanged += () =>
            {
                itemShow.Header = loc.T("Show / Hide Main Window", "显示 / 隐藏主面板");
                itemNew.Header = loc.T("New Task / Note", "新建待办 / 笔记");
                itemCalendar.Header = loc.T("Calendar · Archive · Settings", "任务日历 · 历史归档 · 设置");
                UpdateTrayPlacementHeaders(panelWindow.CurrentPlacementMode);
                itemExit.Header = loc.T("Quit Sticky Notes", "退出电子便签");
                if (_trayIcon is not null)
                {
                    _trayIcon.ToolTipText = loc.T(
                        "Sticky Notes (Click to show/hide, right-click for menu)",
                        "电子便签 (单击显示/隐藏，右键菜单)");
                }
            };

            _trayIcon.Clicked += (_, _) =>
            {
                if (panelWindow.IsVisible && panelWindow.WindowState != WindowState.Minimized)
                {
                    panelWindow.HideToTray();
                }
                else
                {
                    panelWindow.RestoreFromTray();
                }
            };

            var icons = new TrayIcons { _trayIcon };
            TrayIcon.SetIcons(this, icons);
        }
        catch
        {
            // Ignore tray icon creation errors in headless/CI environments
        }
    }

    public static WindowIcon CreateAppWindowIcon()
    {
        try
        {
            var uri = new Uri("avares://DesktopNotes.Desktop/Assets/Icons/app-icon.ico");
            using var stream = AssetLoader.Open(uri);
            return new WindowIcon(stream);
        }
        catch
        {
        }

        try
        {
            string localIco = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "app-icon.ico");
            if (File.Exists(localIco))
            {
                using var fs = File.OpenRead(localIco);
                return new WindowIcon(fs);
            }
            string rootIco = Path.Combine(AppContext.BaseDirectory, "app-icon.ico");
            if (File.Exists(rootIco))
            {
                using var fs = File.OpenRead(rootIco);
                return new WindowIcon(fs);
            }
        }
        catch
        {
        }

        try
        {
            var pngUri = new Uri("avares://DesktopNotes.Desktop/Assets/Icons/tray-icon.png");
            using var stream = AssetLoader.Open(pngUri);
            return new WindowIcon(stream);
        }
        catch
        {
        }

        const int size = 32;
        var bitmap = new WriteableBitmap(
            new PixelSize(size, size),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var fb = bitmap.Lock())
        {
            int[] pixels = new int[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Warm golden yellow note paper with red pin head
                    bool inside = x >= 4 && x < size - 4 && y >= 4 && y < size - 4;
                    if (!inside)
                    {
                        pixels[y * size + x] = 0;
                        continue;
                    }

                    // Red pushpin at top center
                    bool isPin = (x >= 13 && x <= 18) && (y >= 4 && y <= 9);
                    if (isPin)
                    {
                        pixels[y * size + x] = unchecked((int)0xFF2333EA);
                        continue;
                    }

                    // Peel corner at bottom right
                    bool isPeel = (x >= size - 8) && (y >= size - 8) && (x + y >= size * 2 - 14);
                    if (isPeel)
                    {
                        pixels[y * size + x] = unchecked((int)0xFFEBFDFF);
                        continue;
                    }

                    // Golden yellow body (#FEE047)
                    pixels[y * size + x] = unchecked((int)0xFF47E0FE);
                }
            }

            Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
        }

        return new WindowIcon(bitmap);
    }
}
