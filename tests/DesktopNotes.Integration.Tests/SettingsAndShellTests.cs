using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Settings;
using DesktopNotes.Desktop.Localization;
using DesktopNotes.Desktop.ViewModels;
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
