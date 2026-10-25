using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DesktopNotes.Desktop.Views;

namespace DesktopNotes.Desktop;

public partial class App : global::Avalonia.Application
{
    private Bootstrapper? _bootstrapper;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _bootstrapper = new Bootstrapper();
            _ = _bootstrapper.InitializeAsync();
            _ = _bootstrapper.PanelViewModel.RefreshAsync(CancellationToken.None);

            var panelWindow = new PanelWindow(
                _bootstrapper.PanelViewModel,
                _bootstrapper.CreateDetailViewModel,
                _bootstrapper.CreateCalendarViewModel,
                _bootstrapper.CreateSettingsViewModel);

            desktop.MainWindow = panelWindow;
            desktop.Exit += (_, _) => _bootstrapper?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
