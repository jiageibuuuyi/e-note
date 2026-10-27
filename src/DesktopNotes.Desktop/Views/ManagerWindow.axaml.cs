using Avalonia.Controls;
using DesktopNotes.Desktop.ViewModels;

namespace DesktopNotes.Desktop.Views;

public partial class ManagerWindow : Window
{
    public CalendarViewModel? CalendarViewModel { get; }
    public SettingsViewModel? SettingsViewModel { get; }

    public ManagerWindow()
    {
        InitializeComponent();
    }

    public ManagerWindow(CalendarViewModel? calendarViewModel, SettingsViewModel? settingsViewModel) : this()
    {
        CalendarViewModel = calendarViewModel;
        SettingsViewModel = settingsViewModel;

        if (SettingsViewModel is not null)
        {
            if (CheckLockPosition is not null)
            {
                CheckLockPosition.IsChecked = SettingsViewModel.PositionLocked;
                CheckLockPosition.Click += (_, _) =>
                {
                    SettingsViewModel.PositionLocked = CheckLockPosition.IsChecked ?? false;
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
        }
    }
}
