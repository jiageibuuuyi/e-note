using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Queries;
using DesktopNotes.Application.Records;
using DesktopNotes.Desktop.ViewModels;
using DesktopNotes.Infrastructure.Persistence;
using DesktopNotes.Platforms.Windows;

namespace DesktopNotes.Desktop;

public sealed class Bootstrapper : IDisposable
{
    public SqliteStore Store { get; }
    public IRecordService RecordService { get; }
    public IRecordQueryService QueryService { get; }
    public IDesktopIntegration DesktopIntegration { get; }
    public PanelViewModel PanelViewModel { get; }

    public Bootstrapper(string? databasePath = null)
    {
        string dbPath = databasePath ?? GetDefaultDatabasePath();
        Store = new SqliteStore(dbPath);
        RecordService = new RecordService(Store);
        QueryService = new RecordQueryService(Store);
        DesktopIntegration = new DesktopHostProbe();
        PanelViewModel = new PanelViewModel(RecordService, QueryService);
    }

    public DetailViewModel CreateDetailViewModel()
    {
        return new DetailViewModel(RecordService, QueryService);
    }

    public static string GetDefaultDatabasePath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = AppContext.BaseDirectory;
        }
        return Path.Combine(appData, "DesktopNotes", "data", "app.db");
    }

    public void Dispose()
    {
        Store.Dispose();
    }
}
