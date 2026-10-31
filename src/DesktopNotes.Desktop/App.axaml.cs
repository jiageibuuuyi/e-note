using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DesktopNotes.Application.Contracts;
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
            var menu = new NativeMenu();

            var itemShow = new NativeMenuItem("显示 / 隐藏主面板");
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

            var itemNew = new NativeMenuItem("新建待办 / 笔记");
            itemNew.Click += (_, _) => panelWindow.TriggerInlineAddFromOutside();
            menu.Items.Add(itemNew);

            var itemCalendar = new NativeMenuItem("任务日历 · 历史归档 · 设置");
            itemCalendar.Click += (_, _) => panelWindow.OpenManagerWindow();
            menu.Items.Add(itemCalendar);

            menu.Items.Add(new NativeMenuItemSeparator());

            var itemTopmost = new NativeMenuItem("置于所有应用最上层 (置顶)");
            itemTopmost.Click += (_, _) => panelWindow.ApplyPlacementMode(AppPlacementMode.Topmost);
            menu.Items.Add(itemTopmost);

            var itemBottom = new NativeMenuItem("置于所有应用最下层 (桌面层)");
            itemBottom.Click += (_, _) => panelWindow.ApplyPlacementMode(AppPlacementMode.EmbeddedDesktop);
            menu.Items.Add(itemBottom);

            menu.Items.Add(new NativeMenuItemSeparator());

            var itemExit = new NativeMenuItem("退出桌面笔记");
            itemExit.Click += (_, _) => panelWindow.ExitApplication();
            menu.Items.Add(itemExit);

            _trayIcon = new TrayIcon
            {
                ToolTipText = "桌面笔记 (单击显示/隐藏，右键更多选项)",
                Icon = CreateAppWindowIcon(),
                Menu = menu,
                IsVisible = true
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

    private static WindowIcon CreateAppWindowIcon()
    {
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
                    // Rounded square background in electric blue (#2563EB -> BGRA: B=0xEB, G=0x63, R=0x25, A=0xFF)
                    bool inside = x >= 2 && x < size - 2 && y >= 2 && y < size - 2;
                    if (!inside)
                    {
                        pixels[y * size + x] = 0;
                        continue;
                    }

                    // White checkmark in center
                    bool isCheck =
                        (x >= 8 && x <= 13 && Math.Abs((y - 16) - (x - 8)) <= 2) ||
                        (x >= 13 && x <= 23 && Math.Abs((y - 21) + (x - 13)) <= 2);

                    pixels[y * size + x] = isCheck
                        ? unchecked((int)0xFFFFFFFF)
                        : unchecked((int)0xFF2563EB);
                }
            }

            Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
        }

        return new WindowIcon(bitmap);
    }
}
