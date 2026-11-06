using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Desktop;
using DesktopNotes.Desktop.Localization;
using DesktopNotes.Desktop.Views;
using DesktopNotes.Domain;
using DesktopNotes.Infrastructure.Persistence;

namespace DesktopNotes.ScreenshotCapture;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<CaptureApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

public class CaptureApp : DesktopNotes.Desktop.App
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await RunCaptureFlowAsync(desktop);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error during capture: {ex}");
                }
                finally
                {
                    desktop.Shutdown();
                }
            });
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task RunCaptureFlowAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        Console.WriteLine("Initializing custom database...");
        string tempDbPath = Path.GetFullPath("temp_screenshots_showcase.db");
        if (File.Exists(tempDbPath))
        {
            File.Delete(tempDbPath);
        }

        using var bootstrapper = new Bootstrapper(tempDbPath);
        await bootstrapper.InitializeAsync();
        AppLocalizer.Instance.SetLanguage(AppLanguage.Chinese);

        // Seed rich showcase data
        Console.WriteLine("Seeding showcase data...");
        var recService = bootstrapper.RecordService;
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.DateTime);

        // Save clean placement for panel window
        bootstrapper.SettingsService.SaveWindowPlacement(new WindowPlacement("", 120, 80, 420, 580, 1.0));

        // Task 1: Main Project Goal with Subtasks
        var t1Req = new SaveRecordRequest(
            Guid.NewGuid(), 0, RecordKind.Task, null,
            "🚀 发布 e-note v1.0 全平台正式版",
            "高质量完成跨平台构建、日历比例优化与 MCP Agent 接入。",
            new EffectiveFields(null, "xulijia", Priority.Highest, null, new DueSpec(DueKind.Date, today, null, null), null),
            InheritField.None);
        var t1 = (await recService.SaveAsync(t1Req, default)).Changed[0];

        // Subtasks for Task 1
        var st1 = new SaveRecordRequest(
            Guid.NewGuid(), 0, RecordKind.Task, t1.Id,
            "适配 Windows / macOS / Linux 平台架构", "",
            new EffectiveFields(null, "xulijia", Priority.Highest, null, DueSpec.Empty, null),
            InheritField.None);
        var sub1 = (await recService.SaveAsync(st1, default)).Changed[0];
        await recService.CloseAsync(sub1.Id, sub1.Version, false, default); // Completed

        var st2 = new SaveRecordRequest(
            Guid.NewGuid(), 0, RecordKind.Task, t1.Id,
            "优化任务日历方框为 1:1.1 黄金美学比例", "",
            new EffectiveFields(null, "xulijia", Priority.High, null, DueSpec.Empty, null),
            InheritField.None);
        var sub2 = (await recService.SaveAsync(st2, default)).Changed[0];
        await recService.CloseAsync(sub2.Id, sub2.Version, false, default); // Completed

        var st3 = new SaveRecordRequest(
            Guid.NewGuid(), 0, RecordKind.Task, t1.Id,
            "支持写实拟物曲面便利贴与现代极简双外观", "",
            new EffectiveFields(null, "xulijia", Priority.Normal, null, DueSpec.Empty, null),
            InheritField.None);
        var sub3 = (await recService.SaveAsync(st3, default)).Changed[0];
        await recService.CloseAsync(sub3.Id, sub3.Version, false, default); // Completed

        var st4 = new SaveRecordRequest(
            Guid.NewGuid(), 0, RecordKind.Task, t1.Id,
            "整理 GitHub 开源仓库结构与说明文档", "",
            new EffectiveFields(null, "xulijia", Priority.Highest, null, DueSpec.Empty, null),
            InheritField.None);
        await recService.SaveAsync(st4, default); // In progress

        // Task 2: AI MCP Integration
        var t2Req = new SaveRecordRequest(
            Guid.NewGuid(), 0, RecordKind.Task, null,
            "🤖 接入 AI Agent 智能生态 (MCP)",
            "标准 JSON-RPC 2.0 stdio 协议，支持只读安全事务查询与自动周报复盘生成。",
            new EffectiveFields(null, "xulijia", Priority.High, null, new DueSpec(DueKind.Date, today.AddDays(2), null, null), null),
            InheritField.None);
        var t2 = (await recService.SaveAsync(t2Req, default)).Changed[0];

        var t2_sub1 = new SaveRecordRequest(
            Guid.NewGuid(), 0, RecordKind.Task, t2.Id,
            "实现只读 SQLite 并发安全隔离", "",
            new EffectiveFields(null, "xulijia", Priority.High, null, DueSpec.Empty, null),
            InheritField.None);
        var t2_s1 = (await recService.SaveAsync(t2_sub1, default)).Changed[0];
        await recService.CloseAsync(t2_s1.Id, t2_s1.Version, false, default);

        var t2_sub2 = new SaveRecordRequest(
            Guid.NewGuid(), 0, RecordKind.Task, t2.Id,
            "支持自然语言周报与任务追踪", "",
            new EffectiveFields(null, "xulijia", Priority.High, null, DueSpec.Empty, null),
            InheritField.None);
        await recService.SaveAsync(t2_sub2, default);

        // Task 3: Documentation
        var t3Req = new SaveRecordRequest(
            Guid.NewGuid(), 0, RecordKind.Task, null,
            "📝 完善开源架构设计与 API 规范",
            "包含 ADR 架构决策、数据库数据字典与跨平台构建指南。",
            new EffectiveFields(null, "xulijia", Priority.Normal, null, new DueSpec(DueKind.Date, today.AddDays(5), null, null), null),
            InheritField.None);
        await recService.SaveAsync(t3Req, default);

        // Some completed tasks on other days for the calendar
        var tCal1 = new SaveRecordRequest(
            Guid.NewGuid(), 0, RecordKind.Task, null,
            "完成 SQLite 动态备份与还原模块", "",
            new EffectiveFields(null, "xulijia", Priority.Normal, null, new DueSpec(DueKind.Date, today.AddDays(-3), null, null), null),
            InheritField.None);
        var cal1 = (await recService.SaveAsync(tCal1, default)).Changed[0];
        await recService.CloseAsync(cal1.Id, cal1.Version, false, default);

        var tCal2 = new SaveRecordRequest(
            Guid.NewGuid(), 0, RecordKind.Task, null,
            "优化全局字体清晰度与高分屏适配", "",
            new EffectiveFields(null, "xulijia", Priority.Normal, null, new DueSpec(DueKind.Date, today.AddDays(-6), null, null), null),
            InheritField.None);
        var cal2 = (await recService.SaveAsync(tCal2, default)).Changed[0];
        await recService.CloseAsync(cal2.Id, cal2.Version, false, default);

        var tCal3 = new SaveRecordRequest(
            Guid.NewGuid(), 0, RecordKind.Task, null,
            "设计治愈系生机绿植嫩苗状态图标", "",
            new EffectiveFields(null, "xulijia", Priority.Normal, null, new DueSpec(DueKind.Date, today.AddDays(-8), null, null), null),
            InheritField.None);
        var cal3 = (await recService.SaveAsync(tCal3, default)).Changed[0];
        await recService.CloseAsync(cal3.Id, cal3.Version, false, default);

        await bootstrapper.PanelViewModel.RefreshAsync(default);

        // Ensure docs/images directory exists
        Directory.CreateDirectory(Path.Combine("docs", "images"));

        // 1. Capture Realistic Sticky Note Window
        Console.WriteLine("Capturing Realistic Sticky Note Window...");
        AppLocalizer.Instance.SetLanguage(AppLanguage.Chinese);
        AppLocalizer.Instance.SetVisualStyle(AppVisualStyle.Realistic);
        var panelWindow = new PanelWindow(
            bootstrapper.PanelViewModel,
            bootstrapper.CreateDetailViewModel,
            bootstrapper.CreateCalendarViewModel,
            bootstrapper.CreateSettingsViewModel,
            bootstrapper.SettingsService,
            bootstrapper.DesktopIntegration,
            bootstrapper.BackupService)
        {
            Width = 380,
            Height = 560
        };

        panelWindow.Show();
        await Task.Delay(1800); // Wait for render and animations
        CaptureWindowToFile(panelWindow, Path.Combine("docs", "images", "screenshot-realistic.png"));

        // 2. Capture Minimalist Sticky Note Window
        Console.WriteLine("Capturing Minimalist Sticky Note Window...");
        AppLocalizer.Instance.SetVisualStyle(AppVisualStyle.Minimalist);
        await Task.Delay(1000);
        CaptureWindowToFile(panelWindow, Path.Combine("docs", "images", "screenshot-minimalist.png"));
        panelWindow.Close();

        // Restore Realistic Style for other windows
        AppLocalizer.Instance.SetVisualStyle(AppVisualStyle.Realistic);

        // 3. Capture Task Calendar Window (ManagerWindow)
        Console.WriteLine("Capturing Task Calendar Window...");
        var calVm = bootstrapper.CreateCalendarViewModel();
        await calVm.LoadMonthAsync(today.Year, today.Month, CalendarAxis.Due, RecordKind.Task, null, default);
        var setVm = bootstrapper.CreateSettingsViewModel();
        var mgrWin = new ManagerWindow(
            calVm,
            setVm,
            bootstrapper.BackupService,
            _ => {},
            _ => {},
            initialTabIndex: 0)
        {
            Width = 820,
            Height = 700
        };

        mgrWin.Show();
        await Task.Delay(1800);
        CaptureWindowToFile(mgrWin, Path.Combine("docs", "images", "screenshot-calendar.png"));
        mgrWin.Close();

        // 4. Capture Task Detail Window
        Console.WriteLine("Capturing Task Detail Window...");
        var detailVm = bootstrapper.CreateDetailViewModel();
        await detailVm.LoadAsync(t1.Id, default);
        // Add nice markdown body for display
        detailVm.Body = """
        ### 🎯 核心功能交付清单
        - [x] **跨平台适配**：全平台编译通过与自包含安装包
        - [x] **视觉美学**：写实拟物曲面便利贴与现代极简双外观
        - [x] **日历重构**：固定 `1:1.1` 黄金美学方框比例
        - [ ] **开源发布**：精炼开源架构与图文并茂说明

        > [!NOTE]
        > 数据保存在纯本地 SQLite 数据库中，支持离线运行与零云端回传。
        """;
        var detailWin = new DetailWindow(detailVm)
        {
            Width = 560,
            Height = 720
        };
        detailWin.Show();
        await Task.Delay(1800);
        CaptureWindowToFile(detailWin, Path.Combine("docs", "images", "screenshot-detail.png"));
        detailWin.Close();

        Console.WriteLine("All screenshots captured successfully!");
    }

    private static void CaptureWindowToFile(Window window, string outputPath)
    {
        var handle = window.TryGetPlatformHandle()?.Handle;
        if (handle == null || handle == IntPtr.Zero)
        {
            Console.WriteLine($"Cannot get HWND for {window.Title}");
            return;
        }

        var hwnd = handle.Value;
        GetWindowRect(hwnd, out RECT rect);
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0)
        {
            Console.WriteLine($"Invalid window size {w}x{h}");
            return;
        }

        IntPtr hdcWindow = GetWindowDC(hwnd);
        IntPtr hdcMem = CreateCompatibleDC(hdcWindow);
        IntPtr hbm = CreateCompatibleBitmap(hdcWindow, w, h);
        SelectObject(hdcMem, hbm);

        // PW_RENDERFULLCONTENT = 2
        PrintWindow(hwnd, hdcMem, 2);

        using (var bmp = System.Drawing.Image.FromHbitmap(hbm))
        {
            bmp.Save(outputPath, ImageFormat.Png);
        }

        DeleteObject(hbm);
        DeleteDC(hdcMem);
        ReleaseDC(hwnd, hdcWindow);
        Console.WriteLine($"Saved {outputPath} ({w}x{h})");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBkgnd, uint nFlags);
}
