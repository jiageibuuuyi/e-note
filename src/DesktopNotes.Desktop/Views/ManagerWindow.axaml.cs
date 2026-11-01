using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DesktopNotes.Application.Backup;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Desktop.Localization;
using DesktopNotes.Desktop.ViewModels;
using AppPlacementMode = DesktopNotes.Application.Contracts.PlacementMode;

namespace DesktopNotes.Desktop.Views;

public partial class ManagerWindow : Window
{
    public CalendarViewModel? CalendarViewModel { get; }
    public SettingsViewModel? SettingsViewModel { get; }
    private readonly BackupService? _backupService;
    private readonly Action<AppPlacementMode>? _onPlacementModeChanged;
    private readonly Action<bool>? _onLockStateChanged;

    public ManagerWindow()
    {
        InitializeComponent();
    }

    public ManagerWindow(
        CalendarViewModel? calendarViewModel,
        SettingsViewModel? settingsViewModel,
        BackupService? backupService = null,
        Action<AppPlacementMode>? onPlacementModeChanged = null,
        Action<bool>? onLockStateChanged = null,
        int initialTabIndex = 0) : this()
    {
        CalendarViewModel = calendarViewModel;
        SettingsViewModel = settingsViewModel;
        _backupService = backupService;
        _onPlacementModeChanged = onPlacementModeChanged;
        _onLockStateChanged = onLockStateChanged;

        DataContext = calendarViewModel;

        if (MainTabControl is not null && initialTabIndex >= 0 && initialTabIndex < MainTabControl.ItemCount)
        {
            MainTabControl.SelectedIndex = initialTabIndex;
        }

        if (CalendarViewModel is not null)
        {
            _ = CalendarViewModel.GoToTodayAsync(CancellationToken.None);
            _ = CalendarViewModel.LoadHistoricalAsync(CancellationToken.None);

            if (BtnPrevMonth is not null)
            {
                BtnPrevMonth.Click += async (_, _) => await CalendarViewModel.PreviousMonthAsync(CancellationToken.None);
            }

            if (BtnTodayMonth is not null)
            {
                BtnTodayMonth.Click += async (_, _) => await CalendarViewModel.GoToTodayAsync(CancellationToken.None);
            }

            if (BtnNextMonth is not null)
            {
                BtnNextMonth.Click += async (_, _) => await CalendarViewModel.NextMonthAsync(CancellationToken.None);
            }

            if (BtnAxisDue is not null)
            {
                BtnAxisDue.Click += async (_, _) => await CalendarViewModel.ChangeAxisAsync(CalendarAxis.Due, CancellationToken.None);
            }

            if (BtnAxisStart is not null)
            {
                BtnAxisStart.Click += async (_, _) => await CalendarViewModel.ChangeAxisAsync(CalendarAxis.Start, CancellationToken.None);
            }

            if (BtnAxisCreated is not null)
            {
                BtnAxisCreated.Click += async (_, _) => await CalendarViewModel.ChangeAxisAsync(CalendarAxis.Created, CancellationToken.None);
            }

            if (BtnAxisClosed is not null)
            {
                BtnAxisClosed.Click += async (_, _) => await CalendarViewModel.ChangeAxisAsync(CalendarAxis.Closed, CancellationToken.None);
            }

            if (BtnShowArchived is not null)
            {
                BtnShowArchived.Click += async (_, _) => await CalendarViewModel.SetHistoryModeAsync(showTrash: false, CancellationToken.None);
            }

            if (BtnShowTrash is not null)
            {
                BtnShowTrash.Click += async (_, _) => await CalendarViewModel.SetHistoryModeAsync(showTrash: true, CancellationToken.None);
            }
        }

        if (SettingsViewModel is not null)
        {
            _ = SettingsViewModel.RefreshSystemStateAsync(CancellationToken.None);
            UpdatePlacementButtonsVisual();
            UpdateLanguageButtonsVisual();

            if (BtnLangEn is not null)
            {
                BtnLangEn.Click += (_, _) =>
                {
                    SettingsViewModel.SetLanguage(AppLanguage.English);
                    UpdateLanguageButtonsVisual();
                    if (SettingsStatusText is not null)
                    {
                        SettingsStatusText.Text = "🌐 Language switched to English";
                    }
                };
            }

            if (BtnLangZh is not null)
            {
                BtnLangZh.Click += (_, _) =>
                {
                    SettingsViewModel.SetLanguage(AppLanguage.Chinese);
                    UpdateLanguageButtonsVisual();
                    if (SettingsStatusText is not null)
                    {
                        SettingsStatusText.Text = "🌐 已切换为中文";
                    }
                };
            }

            if (BtnModeTopmost is not null)
            {
                BtnModeTopmost.Click += (_, _) =>
                {
                    SettingsViewModel.PlacementMode = AppPlacementMode.Topmost;
                    UpdatePlacementButtonsVisual();
                    _onPlacementModeChanged?.Invoke(AppPlacementMode.Topmost);
                };
            }

            if (BtnModeDesktop is not null)
            {
                BtnModeDesktop.Click += (_, _) =>
                {
                    SettingsViewModel.PlacementMode = AppPlacementMode.EmbeddedDesktop;
                    UpdatePlacementButtonsVisual();
                    _onPlacementModeChanged?.Invoke(AppPlacementMode.EmbeddedDesktop);
                };
            }

            if (CheckLockPosition is not null)
            {
                CheckLockPosition.IsChecked = SettingsViewModel.PositionLocked;
                CheckLockPosition.Click += (_, _) =>
                {
                    bool locked = CheckLockPosition.IsChecked ?? false;
                    SettingsViewModel.PositionLocked = locked;
                    _onLockStateChanged?.Invoke(locked);
                    if (SettingsStatusText is not null)
                    {
                        SettingsStatusText.Text = locked
                            ? AppLocalizer.Instance.T("🔒 Window position locked", "🔒 已锁定窗口位置（禁止拖动）")
                            : AppLocalizer.Instance.T("🔓 Window position unlocked", "🔓 已解锁窗口位置（允许拖动）");
                    }
                };
            }

            if (CheckLaunchAtLogin is not null)
            {
                CheckLaunchAtLogin.IsChecked = SettingsViewModel.LoginStartupEnabled;
                CheckLaunchAtLogin.Click += async (_, _) =>
                {
                    await SettingsViewModel.SetLoginStartupAsync(CheckLaunchAtLogin.IsChecked ?? false, CancellationToken.None);
                };
            }

            if (GlobalAttachmentPathText is not null)
            {
                GlobalAttachmentPathText.Text = SettingsViewModel.DefaultAttachmentRootPath;
                ToolTip.SetTip(GlobalAttachmentPathText, SettingsViewModel.DefaultAttachmentRootPath);
            }

            if (BtnChangeGlobalAttachmentPath is not null)
            {
                BtnChangeGlobalAttachmentPath.Click += async (_, _) =>
                {
                    var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                    {
                        Title = AppLocalizer.Instance.T("Select default file folder", "选择全局默认附件存储目录"),
                        AllowMultiple = false
                    });
                    if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } localPath)
                    {
                        SettingsViewModel.SetDefaultAttachmentRootPath(localPath);
                        if (GlobalAttachmentPathText is not null)
                        {
                            GlobalAttachmentPathText.Text = SettingsViewModel.DefaultAttachmentRootPath;
                            ToolTip.SetTip(GlobalAttachmentPathText, SettingsViewModel.DefaultAttachmentRootPath);
                        }
                        if (SettingsStatusText is not null)
                        {
                            SettingsStatusText.Text = AppLocalizer.Instance.T(
                                "✅ Default file folder updated",
                                "✅ 已更新全局默认附件路径");
                        }
                    }
                };
            }

            if (BtnOpenGlobalAttachmentPath is not null)
            {
                BtnOpenGlobalAttachmentPath.Click += (_, _) =>
                {
                    SettingsViewModel.OpenDefaultAttachmentRootDirectory();
                };
            }
        }

        if (BtnExportBackup is not null)
        {
            BtnExportBackup.Click += OnExportBackupClick;
        }

        if (BtnRestoreBackup is not null)
        {
            BtnRestoreBackup.Click += OnRestoreBackupClick;
        }
    }

    private void UpdatePlacementButtonsVisual()
    {
        if (SettingsViewModel is null) return;
        bool isTopmost = SettingsViewModel.PlacementMode == AppPlacementMode.Topmost;
        BtnModeTopmost?.Classes.Set("active", isTopmost);
        BtnModeDesktop?.Classes.Set("active", !isTopmost);
    }

    private void UpdateLanguageButtonsVisual()
    {
        bool isZh = AppLocalizer.Instance.IsChinese;
        BtnLangEn?.Classes.Set("active", !isZh);
        BtnLangZh?.Classes.Set("active", isZh);
    }

    private async void OnReopenRecordClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid id } && CalendarViewModel is not null)
        {
            await CalendarViewModel.ReopenRecordAsync(id, CancellationToken.None);
        }
    }

    private async void OnTrashRecordClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid id } && CalendarViewModel is not null)
        {
            await CalendarViewModel.TrashRecordAsync(id, CancellationToken.None);
        }
    }

    private async void OnRestoreRecordClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid id } && CalendarViewModel is not null)
        {
            await CalendarViewModel.RestoreRecordAsync(id, CancellationToken.None);
        }
    }

    private async void OnExportBackupClick(object? sender, RoutedEventArgs e)
    {
        if (_backupService is null) return;
        var loc = AppLocalizer.Instance;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = loc.T("Export Backup (.zip)", "导出完整备份包 (.zip)"),
            SuggestedFileName = $"DesktopNotes-Backup-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            DefaultExtension = "zip"
        });

        if (file?.TryGetLocalPath() is { } localPath)
        {
            try
            {
                if (SettingsStatusText is not null) SettingsStatusText.Text = loc.T("Exporting backup...", "正在导出备份...");
                await _backupService.ExportAsync(localPath, includeTrash: true, CancellationToken.None);
                if (SettingsStatusText is not null) SettingsStatusText.Text = loc.T("✅ Backup exported", "✅ 备份导出成功");
            }
            catch (Exception ex)
            {
                if (SettingsStatusText is not null) SettingsStatusText.Text = loc.T($"Export failed: {ex.Message}", $"导出失败: {ex.Message}");
            }
        }
    }

    private async void OnRestoreBackupClick(object? sender, RoutedEventArgs e)
    {
        if (_backupService is null) return;
        var loc = AppLocalizer.Instance;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = loc.T("Select backup file (.zip)", "选择要恢复的备份包 (.zip)"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(loc.T("ZIP Backup", "ZIP 备份包")) { Patterns = new[] { "*.zip" } }
            }
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } localPath)
        {
            try
            {
                if (SettingsStatusText is not null) SettingsStatusText.Text = loc.T("Restoring backup...", "正在恢复备份...");
                await _backupService.RestoreAsync(
                    localPath,
                    new Dictionary<Guid, string>(),
                    CancellationToken.None);
                if (SettingsStatusText is not null) SettingsStatusText.Text = loc.T("✅ Backup restored", "✅ 备份已成功恢复");
                if (CalendarViewModel is not null)
                {
                    await CalendarViewModel.GoToTodayAsync(CancellationToken.None);
                    await CalendarViewModel.LoadHistoricalAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                if (SettingsStatusText is not null) SettingsStatusText.Text = loc.T($"Restore failed: {ex.Message}", $"恢复失败: {ex.Message}");
            }
        }
    }
}
