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

        // Verify visual style initial default is Minimalist (per user request) and supports switching
        Assert.True(vm.IsMinimalistStyle);
        Assert.False(vm.IsRealisticStyle);

        vm.SetVisualStyle(AppVisualStyle.Realistic);
        Assert.True(vm.IsRealisticStyle);
        Assert.False(vm.IsMinimalistStyle);

        AppLocalizer.Instance.InitializeFromEnvironmentAndStore(_store);
        Assert.Equal(AppVisualStyle.Realistic, AppLocalizer.Instance.VisualStyle);

        vm.SetVisualStyle(AppVisualStyle.Minimalist);
        Assert.True(vm.IsMinimalistStyle);
        AppLocalizer.Instance.InitializeFromEnvironmentAndStore(_store);
        Assert.Equal(AppVisualStyle.Minimalist, AppLocalizer.Instance.VisualStyle);

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
        Assert.NotNull(rowVm.CompactDueText);
        Assert.NotNull(rowVm.AssigneeTooltip);

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

    [Fact]
    public void TaskProgress_DefaultsToZero_AndAllowsCustomProgressSetting()
    {
        var progressService = new DesktopNotes.Desktop.Services.TaskProgressService(_store);
        Guid taskId = Guid.NewGuid();
        var emptyFields = new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null);
        var record = new RecordView(
            taskId, null, RecordKind.Task, "测试任务", "", RecordStatus.Active,
            emptyFields, emptyFields, InheritField.None, 1, DateTimeOffset.UtcNow, null, null);

        // 1. Without subtasks and without custom setting -> defaults to 0
        var (prog0, isCustom0, hasSubs0) = progressService.ComputeProgress(record, Array.Empty<RecordView>());
        Assert.Equal(0, prog0);
        Assert.False(isCustom0);
        Assert.False(hasSubs0);

        // 2. User sets custom progress to 65%
        progressService.SetCustomProgress(taskId, 65);
        Assert.Equal(65, progressService.GetCustomProgress(taskId));

        var (progCustom, isCustom1, _) = progressService.ComputeProgress(record, Array.Empty<RecordView>());
        Assert.Equal(65, progCustom);
        Assert.True(isCustom1);

        // 3. User clears custom progress -> reverts to 0
        progressService.ClearCustomProgress(taskId);
        Assert.Null(progressService.GetCustomProgress(taskId));
        var (progReverted, isCustom2, _) = progressService.ComputeProgress(record, Array.Empty<RecordView>());
        Assert.Equal(0, progReverted);
        Assert.False(isCustom2);
    }

    [Fact]
    public void TaskProgress_ComputesSubtaskCompletionAutomatically_AndSupportsParentOverrideAndReset()
    {
        var progressService = new DesktopNotes.Desktop.Services.TaskProgressService(_store);
        Guid parentId = Guid.NewGuid();
        Guid sub1Id = Guid.NewGuid();
        Guid sub2Id = Guid.NewGuid();
        var emptyFields = new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null);

        var parent = new RecordView(parentId, null, RecordKind.Task, "父任务", "", RecordStatus.Active, emptyFields, emptyFields, InheritField.None, 1, DateTimeOffset.UtcNow, null, null);
        var sub1 = new RecordView(sub1Id, parentId, RecordKind.Task, "子任务1", "", RecordStatus.Active, emptyFields, emptyFields, InheritField.None, 1, DateTimeOffset.UtcNow, null, null);
        var sub2 = new RecordView(sub2Id, parentId, RecordKind.Task, "子任务2", "", RecordStatus.Active, emptyFields, emptyFields, InheritField.None, 1, DateTimeOffset.UtcNow, null, null);

        var subtasks = new List<RecordView> { sub1, sub2 };

        // 1. Both subtasks active (0%) -> parent is 0%
        var (p0, isC0, hasSubs) = progressService.ComputeProgress(parent, subtasks);
        Assert.Equal(0, p0);
        Assert.False(isC0);
        Assert.True(hasSubs);

        // 2. Subtask 1 marked completed (100%), Subtask 2 active (0%) -> parent is 50%
        var (p50, isC50, _) = progressService.ComputeProgress(parent, subtasks, id => id == sub1Id);
        Assert.Equal(50, p50);
        Assert.False(isC50);

        // 3. Subtask 2 set to 50% custom -> average = (100 + 50) / 2 = 75%
        progressService.SetCustomProgress(sub2Id, 50);
        var (p75, isC75, _) = progressService.ComputeProgress(parent, subtasks, id => id == sub1Id);
        Assert.Equal(75, p75);
        Assert.False(isC75);

        // 4. User manually overrides parent to 90%
        progressService.SetCustomProgress(parentId, 90);
        var (pParentOverride, isCParent, _) = progressService.ComputeProgress(parent, subtasks, id => id == sub1Id);
        Assert.Equal(90, pParentOverride);
        Assert.True(isCParent);

        // 5. User resets parent to follow subtasks -> restores 75%
        progressService.ClearCustomProgress(parentId);
        var (pRestored, isCRestored, _) = progressService.ComputeProgress(parent, subtasks, id => id == sub1Id);
        Assert.Equal(75, pRestored);
        Assert.False(isCRestored);

        // 6. When both subtasks completed -> 100%
        var (p100, _, _) = progressService.ComputeProgress(parent, subtasks, id => true);
        Assert.Equal(100, p100);
    }

    [Fact]
    public void TaskRowViewModel_HasDrawerMeta_IncludesProgress_AndFormatsSummary()
    {
        AppLocalizer.Instance.SetLanguage(AppLanguage.Chinese);
        Guid id = Guid.NewGuid();
        var emptyFields = new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null);
        var record = new RecordView(id, null, RecordKind.Task, "任务", "", RecordStatus.Active, emptyFields, emptyFields, InheritField.None, 1, DateTimeOffset.UtcNow, null, null);

        var rowVm = new TaskRowViewModel(
            record,
            TimeZoneInfo.Utc,
            DateTimeOffset.UtcNow,
            isExpanded: true,
            isMarkedCompleted: false,
            subtaskCount: 2,
            isTreeExpanded: true,
            isLastSubtask: false,
            depth: 0,
            progress: 50,
            isCustomProgress: false);

        Assert.True(rowVm.HasProgress);
        Assert.True(rowVm.HasDrawerMeta);
        Assert.Equal("50%", rowVm.ProgressText);
        Assert.Contains("50%", rowVm.ProgressSummary);
        Assert.Contains("子任务", rowVm.ProgressSummary);
        Assert.True(rowVm.ShowProgressBadge);

        // When custom
        var customRowVm = new TaskRowViewModel(
            record,
            TimeZoneInfo.Utc,
            DateTimeOffset.UtcNow,
            isExpanded: true,
            isMarkedCompleted: false,
            subtaskCount: 0,
            isTreeExpanded: true,
            isLastSubtask: false,
            depth: 0,
            progress: 80,
            isCustomProgress: true);

        Assert.Contains("80%", customRowVm.ProgressSummary);
        Assert.Contains("手动", customRowVm.ProgressSummary);
    }

    [Fact]
    public void TaskRowViewModel_TitleDisplayAndInlineEditState_EnforcesRightClickOnlyEdit()
    {
        AppLocalizer.Instance.SetLanguage(AppLanguage.Chinese);
        Guid id = Guid.NewGuid();
        var emptyFields = new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null);
        var record = new RecordView(id, null, RecordKind.Task, "任务标题", "任务详情正文", RecordStatus.Active, emptyFields, emptyFields, InheritField.None, 1, DateTimeOffset.UtcNow, null, null);

        var rowVm = new TaskRowViewModel(
            record,
            TimeZoneInfo.Utc,
            DateTimeOffset.UtcNow,
            isExpanded: false,
            isMarkedCompleted: false,
            subtaskCount: 0,
            isTreeExpanded: true,
            isLastSubtask: false,
            depth: 0,
            progress: 0,
            isCustomProgress: false);

        // 1. Initial Normal State: Read-only title block is visible, no blinking caret, no edit actions
        Assert.False(rowVm.IsEditingTitle);
        Assert.False(rowVm.IsEditingInline);
        Assert.True(rowVm.IsTitleDisplayVisible);
        Assert.True(rowVm.IsDisplayNormal);
        Assert.False(rowVm.ShowInlineEditActions);
        Assert.False(rowVm.IsTitleDirty);
        Assert.False(rowVm.IsExpanded);

        // 2. Simulating Right-Click Menu -> '修改内容'
        rowVm.IsEditingTitle = true;
        Assert.True(rowVm.IsEditingTitle);
        Assert.False(rowVm.IsTitleDisplayVisible);
        Assert.False(rowVm.IsDisplayNormal);
        Assert.True(rowVm.ShowInlineEditActions);

        // Modify title in edit mode
        rowVm.EditableTitle = "新修改的任务标题";
        Assert.True(rowVm.IsTitleDirty);

        // 3. Simulating Save or Esc cancel
        rowVm.IsEditingTitle = false;
        Assert.False(rowVm.IsEditingTitle);
        Assert.True(rowVm.IsTitleDisplayVisible);
        Assert.True(rowVm.IsDisplayNormal);
        Assert.False(rowVm.ShowInlineEditActions);

        // 4. Drawer & Meta Area
        rowVm.IsExpanded = true;
        Assert.True(rowVm.IsExpanded);
        Assert.True(rowVm.HasBody);
        Assert.True(rowVm.HasDrawerMeta);
        Assert.True(rowVm.CanAddSubtask);
        Assert.False(rowVm.IsSubtask);

        // 5. Completed Mode
        rowVm.IsMarkedCompleted = true;
        Assert.False(rowVm.IsTitleDisplayVisible);
        Assert.True(rowVm.IsDisplayCompleted);
        Assert.Equal(100, rowVm.Progress);
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
