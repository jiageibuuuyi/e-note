using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Settings;

namespace DesktopNotes.Desktop.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private AppPreferences _preferences;
    private bool _loginStartupEnabled;
    private string _statusMessage = string.Empty;
    private CancellationTokenSource? _placementDebounceCts;

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

    public SettingsViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _preferences = _settingsService.GetPreferences();
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
    }

    public async Task SetLoginStartupAsync(bool enabled, CancellationToken ct)
    {
        await _settingsService.SetSystemLoginStartupAsync(enabled, ct);
        LoginStartupEnabled = await _settingsService.GetSystemLoginStartupAsync(ct);
        StatusMessage = LoginStartupEnabled ? "已开启登录自动启动" : "已关闭登录自动启动";
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
