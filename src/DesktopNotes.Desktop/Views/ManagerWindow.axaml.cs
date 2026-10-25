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
    }
}
