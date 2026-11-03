using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Settings;
using DesktopNotes.Desktop;
using DesktopNotes.Desktop.Localization;
using DesktopNotes.Desktop.ViewModels;
using DesktopNotes.Domain;
using DesktopNotes.Infrastructure.Persistence;
using DesktopNotes.Platforms.Windows;
using Xunit;

namespace DesktopNotes.Integration.Tests;

public sealed class SettingsAndShellTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteStore _store;

    public SettingsAndShellTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DesktopNotesTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new SqliteStore(Path.Combine(_tempDir, "test.db"));
    }

    [Fact]
    public void SettingsService_InitializesDefaults_AndPersistsPreferences()
    {
        var fakeDesktop = new FakeDesktopIntegration();
        var service = new SettingsService(_store, fakeDesktop);

        AppPreferences initial = service.GetPreferences();
        Assert.False(initial.NotificationsEnabled);
        Assert.Equal(30, initial.ReminderLeadMinutes);
        Assert.True(initial.QuietHoursEnabled);
        Assert.Equal(PlacementMode.Topmost, initial.PlacementMode);
        Assert.False(initial.PositionLocked);
        Assert.Equal("Default", initial.ThemeVariant);
        Assert.Equal(14.0, initial.FontSizeDip);

        service.SavePreferences(initial with
        {
            NotificationsEnabled = true,
            PlacementMode = PlacementMode.EmbeddedDesktop,
            PositionLocked = true,
            ThemeVariant = "Dark",
            FontSizeDip = 16.0
        });

        AppPreferences reloaded = service.GetPreferences();
        Assert.True(reloaded.NotificationsEnabled);
        Assert.Equal(PlacementMode.EmbeddedDesktop, reloaded.PlacementMode);
        Assert.True(reloaded.PositionLocked);
        Assert.Equal("Dark", reloaded.ThemeVariant);
        Assert.Equal(16.0, reloaded.FontSizeDip);
    }

    [Fact]
    public void GetWindowPlacement_RecoversOffScreenCoordinatesViaDesktopIntegration()
    {
        var fakeDesktop = new FakeDesktopIntegration();
        var service = new SettingsService(_store, fakeDesktop);

        // Save off-screen coordinates (e.g., disconnected secondary monitor at X=3200)
        service.SaveWindowPlacement(new WindowPlacement("DISPLAY2", 3200, -500, 350, 520, 1.0));

        WindowPlacement recovered = service.GetWindowPlacement();
        Assert.Equal("primary", recovered.MonitorId);
        Assert.InRange(recovered.X, 0, 1920 - 350);
        Assert.InRange(recovered.Y, 0, 1080 - 520);
    }

    [Fact]
    public void SingleInstanceGuard_BlocksSecondInstanceAndReleasesOnDispose()
    {
        string instanceName = @"Local\DesktopNotes_Test_" + Guid.NewGuid().ToString("N");

        using (var first = new SingleInstanceGuard(instanceName))
        {
            Assert.True(first.IsFirstInstance);

            using var second = new SingleInstanceGuard(instanceName);
            Assert.False(second.IsFirstInstance);
        }

        using var afterDispose = new SingleInstanceGuard(instanceName);
        Assert.True(afterDispose.IsFirstInstance);
    }

    [Fact]
    public async Task SettingsViewModel_UpdatesSettingsAndDebouncesPlacement()
    {
        var fakeDesktop = new FakeDesktopIntegration();
        var service = new SettingsService(_store, fakeDesktop);
        AppLocalizer.Instance.InitializeFromEnvironmentAndStore(_store);
        Assert.Equal(AppLanguage.English, AppLocalizer.Instance.Language);

        var vm = new SettingsViewModel(service, settingsRepository: _store);

        await vm.RefreshSystemStateAsync(CancellationToken.None);
        Assert.False(vm.LoginStartupEnabled);
        Assert.True(vm.IsEnglish);

        vm.NotificationsEnabled = true;
        vm.PositionLocked = true;
        vm.FontSizeDip = 18.0;
        await vm.SetLoginStartupAsync(true, CancellationToken.None);

        Assert.True(vm.LoginStartupEnabled);
        Assert.Equal("Launch at login enabled", vm.StatusMessage);

        vm.SetLanguage(AppLanguage.Chinese);
        Assert.True(vm.IsChinese);
        await vm.SetLoginStartupAsync(true, CancellationToken.None);
        Assert.Equal("已开启登录自动启动", vm.StatusMessage);

        vm.SetLanguage(AppLanguage.English);
        Assert.True(vm.IsEnglish);

        // Verify visual style switching (Realistic <-> Minimalist) and persistence
        Assert.True(vm.IsRealisticStyle);
        Assert.False(vm.IsMinimalistStyle);

        vm.SetVisualStyle(AppVisualStyle.Minimalist);
        Assert.True(vm.IsMinimalistStyle);
        Assert.False(vm.IsRealisticStyle);

        AppLocalizer.Instance.InitializeFromEnvironmentAndStore(_store);
        Assert.Equal(AppVisualStyle.Minimalist, AppLocalizer.Instance.VisualStyle);

        vm.SetVisualStyle(AppVisualStyle.Realistic);
        Assert.True(vm.IsRealisticStyle);
        AppLocalizer.Instance.InitializeFromEnvironmentAndStore(_store);
        Assert.Equal(AppVisualStyle.Realistic, AppLocalizer.Instance.VisualStyle);

        // Schedule rapid debounced window placement updates
        vm.ScheduleWindowPlacementSave(new WindowPlacement("primary", 100, 100, 350, 520, 1.0));
        vm.ScheduleWindowPlacementSave(new WindowPlacement("primary", 240, 180, 360, 540, 1.0));

        await Task.Delay(650);

        WindowPlacement finalPlacement = service.GetWindowPlacement();
        Assert.Equal(240, finalPlacement.X);
        Assert.Equal(180, finalPlacement.Y);
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public void AppLocalizer_AndAssets_ReflectFormalStickyNotesBranding()
    {
        var loc = AppLocalizer.Instance;
        loc.SetLanguage(AppLanguage.Chinese);
        Assert.Equal("电子便签", loc.AppTitle);
        Assert.Contains("电子便签", loc.MenuExitApp);

        loc.SetLanguage(AppLanguage.English);
        Assert.Equal("Sticky Notes", loc.AppTitle);
        Assert.Contains("Sticky Notes", loc.MenuExitApp);

        // Reset to default
        loc.SetLanguage(AppLanguage.Chinese);

        string baseDir = AppContext.BaseDirectory;
        // Search upwards for src/DesktopNotes.Desktop/Assets/Icons
        string? projectRoot = null;
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DesktopNotes.sln")))
            {
                projectRoot = dir.FullName;
                break;
            }
            dir = dir.Parent;
        }

        Assert.NotNull(projectRoot);
        string iconsDir = Path.Combine(projectRoot, "src", "DesktopNotes.Desktop", "Assets", "Icons");
        Assert.True(File.Exists(Path.Combine(iconsDir, "app-icon.ico")), "app-icon.ico exists");
        Assert.True(File.Exists(Path.Combine(iconsDir, "app-icon.png")), "app-icon.png exists");
        Assert.True(File.Exists(Path.Combine(iconsDir, "tray-icon.png")), "tray-icon.png exists");
    }

    [Fact]
    public void PanelWindow_AndTaskRow_DrawerLayoutAndTaskbarProperties()
    {
        // 1. Verify Window XAMLs have ShowInTaskbar="False" to ensure app exists ONLY in system tray
        string baseDir = AppContext.BaseDirectory;
        string? projectRoot = null;
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DesktopNotes.sln")))
            {
                projectRoot = dir.FullName;
                break;
            }
            dir = dir.Parent;
        }

        Assert.NotNull(projectRoot);
        string viewsDir = Path.Combine(projectRoot, "src", "DesktopNotes.Desktop", "Views");
        string panelXaml = File.ReadAllText(Path.Combine(viewsDir, "PanelWindow.axaml"));
        string detailXaml = File.ReadAllText(Path.Combine(viewsDir, "DetailWindow.axaml"));
        string managerXaml = File.ReadAllText(Path.Combine(viewsDir, "ManagerWindow.axaml"));

        Assert.Contains("ShowInTaskbar=\"False\"", panelXaml);
        Assert.Contains("ShowInTaskbar=\"False\"", detailXaml);
        Assert.Contains("ShowInTaskbar=\"False\"", managerXaml);

        // 2. Verify TaskRow drawer meta logic preventing overlap
        var zone = TimeZoneInfo.Local;
        var now = DateTimeOffset.UtcNow;
        var metaFields = new EffectiveFields(
            null,
            "李工",
            Priority.Normal,
            null,
            new DueSpec(DueKind.Date, new DateOnly(2026, 11, 3), null, null),
            null);

        var subtaskView = new RecordView(
            Guid.NewGuid(),
            Guid.NewGuid(),
            RecordKind.Task,
            "子任务待办",
            "笔记详情",
            RecordStatus.Active,
            metaFields,
            metaFields,
            InheritField.None,
            1,
            now,
            null,
            null);

        var rowVm = new TaskRowViewModel(subtaskView, zone, now, false, depth: 1, isLastSubtask: true);
        Assert.True(rowVm.HasDue);
        Assert.True(rowVm.HasAssignee);
        Assert.True(rowVm.HasDrawerMeta);

        // When collapsed, inline assignee shows in realistic mode
        AppLocalizer.Instance.SetVisualStyle(AppVisualStyle.Realistic);
        rowVm.IsExpanded = false;
        Assert.True(rowVm.ShowSubtaskAssigneeInline);

        // When expanded (user clicked), inline assignee hides from top row to avoid crowding, and drawer shows meta on its own line
        rowVm.IsExpanded = true;
        Assert.False(rowVm.ShowSubtaskAssigneeInline);
        Assert.True(rowVm.HasDrawerMeta);
    }

    [Fact]
    public async Task Bootstrapper_LoadsFirstRunIntent_AndSyncsAttachmentRoot()
    {
        string testFolder = Path.Combine(Path.GetTempPath(), "desktopnotes_intent_test_" + Guid.NewGuid().ToString("N"));
        string dbPath = Path.Combine(testFolder, "test.db");
        string customAttachmentPath = Path.Combine(testFolder, "CustomAttachments");
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string intentPath = Path.Combine(localAppData, "DesktopNotes", "first-run-intent.json");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(intentPath)!);
            Directory.CreateDirectory(customAttachmentPath);

            string json = $$"""
            {
              "defaultAttachmentDirectory": "{{customAttachmentPath.Replace("\\", "\\\\")}}",
              "launchAtLogin": false
            }
            """;
            await File.WriteAllTextAsync(intentPath, json);

            using (var bootstrapper = new Bootstrapper(dbPath))
            {
                await bootstrapper.InitializeAsync();
                Assert.Equal(Path.GetFullPath(customAttachmentPath), bootstrapper.AttachmentService.DefaultRootPath);

                var settingsVm = bootstrapper.CreateSettingsViewModel();
                Assert.Equal(Path.GetFullPath(customAttachmentPath), settingsVm.DefaultAttachmentRootPath);

                var detailVm = bootstrapper.CreateDetailViewModel();
                Assert.Contains(Path.GetFullPath(customAttachmentPath), detailVm.AttachmentRootPathText);
            }

            Assert.False(File.Exists(intentPath));

            // Verify persistence on second run without intent file
            using (var rebootstrapper = new Bootstrapper(dbPath))
            {
                await rebootstrapper.InitializeAsync();
                Assert.Equal(Path.GetFullPath(customAttachmentPath), rebootstrapper.AttachmentService.DefaultRootPath);
            }
        }
        finally
        {
            if (File.Exists(intentPath))
            {
                try { File.Delete(intentPath); } catch { }
            }
            if (Directory.Exists(testFolder))
            {
                try { Directory.Delete(testFolder, true); } catch { }
            }
        }
    }

    private sealed class FakeDesktopIntegration : IDesktopIntegration
    {
        private bool _loginStartup;

        public DesktopCapabilities Capabilities => new(
            CapabilitySupport.Supported,
            CapabilitySupport.Supported,
            CapabilitySupport.Supported,
            CapabilitySupport.Supported,
            "Fake Windows Host");

        public Task ApplyModeAsync(nint windowHandle, PlacementMode mode, CancellationToken ct) => Task.CompletedTask;

        public Task SetLoginStartupAsync(bool enabled, CancellationToken ct)
        {
            _loginStartup = enabled;
            return Task.CompletedTask;
        }

        public Task<bool> GetLoginStartupAsync(CancellationToken ct) => Task.FromResult(_loginStartup);

        public WindowPlacement FitToAvailableScreens(WindowPlacement saved)
        {
            const double screenW = 1920;
            const double screenH = 1080;
            if (saved.X < 0 || saved.Y < 0 || saved.X + saved.Width > screenW || saved.Y + saved.Height > screenH)
            {
                return new WindowPlacement("primary", screenW - saved.Width - 32, 32, saved.Width, saved.Height, saved.Scale);
            }
            return saved;
        }
    }
}
