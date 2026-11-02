using System.Diagnostics;
using System.Text.Json;
using DesktopNotes.Application.Attachments;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Ports;
using DesktopNotes.Application.Settings;
using DesktopNotes.Desktop.Localization;

namespace DesktopNotes.Desktop.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private const string GlobalAttachmentRootSettingKey = "global.default_attachment_root_path";
    private readonly SettingsService _settingsService;
    private readonly AttachmentService? _attachmentService;
    private readonly ISettingsRepositoryPort? _settingsRepository;
    private AppPreferences _preferences;
    private bool _loginStartupEnabled;
    private string _statusMessage = string.Empty;
    private CancellationTokenSource? _placementDebounceCts;

    public string DefaultAttachmentRootPath => _attachmentService?.DefaultRootPath ?? Bootstrapper.GetDefaultAttachmentRootPath();
    public AppLanguage Language => AppLocalizer.Instance.Language;
    public bool IsEnglish => AppLocalizer.Instance.IsEnglish;
    public bool IsChinese => AppLocalizer.Instance.IsChinese;
    public AppVisualStyle VisualStyle => AppLocalizer.Instance.VisualStyle;
    public bool IsRealisticStyle => AppLocalizer.Instance.IsRealisticStyle;
    public bool IsMinimalistStyle => AppLocalizer.Instance.IsMinimalistStyle;
    public string DetectedTimeZoneSummary => AppLocalizer.Instance.DetectedTimeZoneSummary;

    public bool NotificationsEnabled
    {
        get => _preferences.NotificationsEnabled;
        set
        {
            if (_preferences.NotificationsEnabled != value)
            {
                _preferences = _preferences with { NotificationsEnabled = value };
                _settingsService.SavePreferences(_preferences);
                RaisePropertyChanged();
            }
        }
    }

    public PlacementMode PlacementMode
    {
        get => _preferences.PlacementMode;
        set
        {
            if (_preferences.PlacementMode != value)
            {
                _preferences = _preferences with { PlacementMode = value };
                _settingsService.SavePreferences(_preferences);
                RaisePropertyChanged();
            }
        }
    }

    public bool PositionLocked
    {
        get => _preferences.PositionLocked;
        set
        {
            if (_preferences.PositionLocked != value)
            {
                _preferences = _preferences with { PositionLocked = value };
                _settingsService.SavePreferences(_preferences);
                RaisePropertyChanged();
            }
        }
    }

    public string ThemeVariant
    {
        get => _preferences.ThemeVariant;
        set
        {
            if (_preferences.ThemeVariant != value)
            {
                _preferences = _preferences with { ThemeVariant = value };
                _settingsService.SavePreferences(_preferences);
                RaisePropertyChanged();
            }
        }
    }

    public double FontSizeDip
    {
        get => _preferences.FontSizeDip;
        set
        {
            double clamped = Math.Clamp(value, 12.0, 20.0);
            if (Math.Abs(_preferences.FontSizeDip - clamped) > 0.01)
            {
                _preferences = _preferences with { FontSizeDip = clamped };
                _settingsService.SavePreferences(_preferences);
                RaisePropertyChanged();
            }
        }
    }

    public bool LoginStartupEnabled
    {
        get => _loginStartupEnabled;
        private set => SetProperty(ref _loginStartupEnabled, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public SettingsViewModel(
        SettingsService settingsService,
        AttachmentService? attachmentService = null,
        ISettingsRepositoryPort? settingsRepository = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _attachmentService = attachmentService;
        _settingsRepository = settingsRepository;
        _preferences = _settingsService.GetPreferences();
        AppLocalizer.Instance.LanguageChanged += () =>
        {
            RaisePropertyChanged(nameof(Loc));
            RaisePropertyChanged(nameof(Language));
            RaisePropertyChanged(nameof(IsEnglish));
            RaisePropertyChanged(nameof(IsChinese));
            RaisePropertyChanged(nameof(DetectedTimeZoneSummary));
        };
        AppLocalizer.Instance.VisualStyleChanged += () =>
        {
            RaisePropertyChanged(nameof(Loc));
            RaisePropertyChanged(nameof(VisualStyle));
            RaisePropertyChanged(nameof(IsRealisticStyle));
            RaisePropertyChanged(nameof(IsMinimalistStyle));
        };
        Services.UndoService.Instance.StateChanged += (_, _) =>
        {
            RaisePropertyChanged(nameof(CanUndo));
            RaisePropertyChanged(nameof(LastUndoSummary));
            RaisePropertyChanged(nameof(UndoStatusText));
            RaisePropertyChanged(nameof(RecentUndoEntries));
        };
    }

    public void SetLanguage(AppLanguage language)
    {
        AppLocalizer.Instance.SetLanguage(language, _settingsRepository);
        StatusMessage = language == AppLanguage.Chinese
            ? "🌐 已切换为中文"
            : "🌐 Language switched to English";
    }

    public void SetVisualStyle(AppVisualStyle style)
    {
        AppLocalizer.Instance.SetVisualStyle(style, _settingsRepository);
        StatusMessage = style == AppVisualStyle.Realistic
            ? Loc.T("📌 Switched to Realistic Sticky Note style", "📌 已切换为「写实便利贴（沐瑶软笔体）」样式")
            : Loc.T("✨ Switched to Minimalist style", "✨ 已切换为「简约现代」样式");
    }

    public void SetDefaultAttachmentRootPath(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        try
        {
            string fullPath = Path.GetFullPath(folderPath);
            Directory.CreateDirectory(fullPath);
            _attachmentService?.SetDefaultStorageRoot(fullPath);
            _settingsRepository?.SetSettingJson(GlobalAttachmentRootSettingKey, JsonSerializer.Serialize(fullPath));
            RaisePropertyChanged(nameof(DefaultAttachmentRootPath));
            StatusMessage = Loc.T(
                $"Updated default folder: {fullPath}",
                $"已热更新全局默认附件路径: {fullPath}");
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.T($"Failed to update folder: {ex.Message}", $"更新全局目录失败: {ex.Message}");
        }
    }

    public void OpenDefaultAttachmentRootDirectory()
    {
        try
        {
            string targetDir = DefaultAttachmentRootPath;
            if (!string.IsNullOrWhiteSpace(targetDir))
            {
                Directory.CreateDirectory(targetDir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetDir,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.T($"Failed to open folder: {ex.Message}", $"打开目录失败: {ex.Message}");
        }
    }

    public async Task RefreshSystemStateAsync(CancellationToken ct)
    {
        _preferences = _settingsService.GetPreferences();
        LoginStartupEnabled = await _settingsService.GetSystemLoginStartupAsync(ct);
        RaisePropertyChanged(nameof(NotificationsEnabled));
        RaisePropertyChanged(nameof(PlacementMode));
        RaisePropertyChanged(nameof(PositionLocked));
        RaisePropertyChanged(nameof(ThemeVariant));
        RaisePropertyChanged(nameof(FontSizeDip));
        RaisePropertyChanged(nameof(Language));
        RaisePropertyChanged(nameof(IsEnglish));
        RaisePropertyChanged(nameof(IsChinese));
        RaisePropertyChanged(nameof(DetectedTimeZoneSummary));
    }

    public async Task SetLoginStartupAsync(bool enabled, CancellationToken ct)
    {
        await _settingsService.SetSystemLoginStartupAsync(enabled, ct);
        LoginStartupEnabled = await _settingsService.GetSystemLoginStartupAsync(ct);
        StatusMessage = LoginStartupEnabled
            ? Loc.T("Launch at login enabled", "已开启登录自动启动")
            : Loc.T("Launch at login disabled", "已关闭登录自动启动");
    }

    public bool CanUndo => Services.UndoService.Instance.CanUndo;
    public string? LastUndoSummary => Services.UndoService.Instance.LastActionDescription;
    public string UndoStatusText => Services.UndoService.Instance.CanUndo
        ? Loc.T($"Ready to undo: {Services.UndoService.Instance.LastActionDescription}", $"可撤销操作: {Services.UndoService.Instance.LastActionDescription}")
        : Loc.T("No recent operations to undo", "当前暂无可撤销的近期操作");

    public IReadOnlyList<Services.UndoEntry> RecentUndoEntries => Services.UndoService.Instance.RecentEntries;

    public async Task<bool> UndoLastActionAsync(CancellationToken ct)
    {
        if (!Services.UndoService.Instance.CanUndo)
        {
            StatusMessage = Loc.T("No recent operations to undo", "当前暂无可撤销的近期操作");
            return false;
        }

        string desc = Services.UndoService.Instance.LastActionDescription ?? Loc.T("operation", "操作");
        bool success = await Services.UndoService.Instance.UndoAsync(ct);
        if (success)
        {
            StatusMessage = Loc.T($"↩️ Successfully undone: {desc}", $"↩️ 已成功撤销: {desc}");
            RaisePropertyChanged(nameof(CanUndo));
            RaisePropertyChanged(nameof(LastUndoSummary));
            RaisePropertyChanged(nameof(UndoStatusText));
            RaisePropertyChanged(nameof(RecentUndoEntries));
            return true;
        }
        else
        {
            StatusMessage = Loc.T("Failed to undo operation", "撤销操作失败");
            return false;
        }
    }

    public void ScheduleWindowPlacementSave(WindowPlacement placement)
    {
        _placementDebounceCts?.Cancel();
        _placementDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _placementDebounceCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, cts.Token);
                _settingsService.SaveWindowPlacement(placement);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }
}
