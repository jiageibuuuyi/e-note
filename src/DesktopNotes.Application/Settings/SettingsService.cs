using System.Text.Json;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Ports;

namespace DesktopNotes.Application.Settings;

public sealed record FirstRunInstallerIntent(
    string? DefaultAttachmentDirectory,
    bool LaunchAtLogin);

public sealed record AppPreferences(
    Guid? DefaultStorageRootId,
    bool NotificationsEnabled,
    int ReminderLeadMinutes,
    bool QuietHoursEnabled,
    PlacementMode PlacementMode,
    bool PositionLocked,
    string ThemeVariant,
    double FontSizeDip,
    string ToggleShortcut,
    string QuickNewShortcut)
{
    public static AppPreferences Default => new(
        DefaultStorageRootId: null,
        NotificationsEnabled: false,
        ReminderLeadMinutes: 30,
        QuietHoursEnabled: true,
        PlacementMode: PlacementMode.Topmost,
        PositionLocked: false,
        ThemeVariant: "Default",
        FontSizeDip: 14.0,
        ToggleShortcut: "Ctrl+Alt+D",
        QuickNewShortcut: "Ctrl+Alt+N");
}

public sealed class SettingsService
{
    private const string PreferencesKey = "app_preferences";
    private const string WindowPlacementKey = "window_placement";

    private readonly ISettingsRepositoryPort _repository;
    private readonly IDesktopIntegration _desktopIntegration;

    public SettingsService(ISettingsRepositoryPort repository, IDesktopIntegration desktopIntegration)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _desktopIntegration = desktopIntegration ?? throw new ArgumentNullException(nameof(desktopIntegration));
    }

    public AppPreferences GetPreferences()
    {
        string? json = _repository.GetSettingJson(PreferencesKey);
        if (string.IsNullOrWhiteSpace(json))
        {
            return AppPreferences.Default;
        }

        return JsonSerializer.Deserialize<AppPreferences>(json) ?? AppPreferences.Default;
    }

    public void SavePreferences(AppPreferences preferences)
    {
        string json = JsonSerializer.Serialize(preferences);
        _repository.SetSettingJson(PreferencesKey, json);
    }

    public WindowPlacement GetWindowPlacement()
    {
        string? json = _repository.GetSettingJson(WindowPlacementKey);
        WindowPlacement saved = string.IsNullOrWhiteSpace(json)
            ? new WindowPlacement("primary", 80, 80, 350, 520, 1.0)
            : JsonSerializer.Deserialize<WindowPlacement>(json) ?? new WindowPlacement("primary", 80, 80, 350, 520, 1.0);

        return _desktopIntegration.FitToAvailableScreens(saved);
    }

    public void SaveWindowPlacement(WindowPlacement placement)
    {
        var fitted = _desktopIntegration.FitToAvailableScreens(placement);
        string json = JsonSerializer.Serialize(fitted);
        _repository.SetSettingJson(WindowPlacementKey, json);
    }

    public Task<bool> GetSystemLoginStartupAsync(CancellationToken ct)
    {
        return _desktopIntegration.GetLoginStartupAsync(ct);
    }

    public Task SetSystemLoginStartupAsync(bool enabled, CancellationToken ct)
    {
        return _desktopIntegration.SetLoginStartupAsync(enabled, ct);
    }

    public async Task<bool> ImportFirstRunIntentIfPresentAsync(
        string intentFilePath,
        IAttachmentRepositoryPort attachmentRepository,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(intentFilePath) || !File.Exists(intentFilePath))
        {
            return false;
        }

        string rawJson = await File.ReadAllTextAsync(intentFilePath, ct);
        var intent = JsonSerializer.Deserialize<FirstRunInstallerIntent>(
            rawJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (intent is null)
        {
            return false;
        }

        var prefs = GetPreferences();
        if (!string.IsNullOrWhiteSpace(intent.DefaultAttachmentDirectory) &&
            Path.IsPathRooted(intent.DefaultAttachmentDirectory))
        {
            string fullRootPath = Path.GetFullPath(intent.DefaultAttachmentDirectory);
            Directory.CreateDirectory(fullRootPath);
            string platform = OperatingSystem.IsWindows()
                ? "windows"
                : OperatingSystem.IsMacOS() ? "macos" : "linux";

            var rootRecord = attachmentRepository.EnsureDefaultStorageRoot(fullRootPath, platform, DateTimeOffset.UtcNow);
            prefs = prefs with { DefaultStorageRootId = rootRecord.Id };
            SavePreferences(prefs);
        }

        await _desktopIntegration.SetLoginStartupAsync(intent.LaunchAtLogin, ct);

        try
        {
            File.Delete(intentFilePath);
        }
        catch
        {
        }

        return true;
    }
}
