using DesktopNotes.Application.Attachments;
using DesktopNotes.Application.Backup;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Queries;
using DesktopNotes.Application.Records;
using DesktopNotes.Application.Reminders;
using DesktopNotes.Application.Settings;
using DesktopNotes.Desktop.Localization;
using DesktopNotes.Desktop.ViewModels;
using DesktopNotes.Infrastructure.Backup;
using DesktopNotes.Infrastructure.Files;
using DesktopNotes.Infrastructure.Persistence;
using DesktopNotes.Platforms.Windows;

namespace DesktopNotes.Desktop;

public sealed class Bootstrapper : IDisposable
{
    private readonly string _attachmentRootPath;

    public SqliteStore Store { get; }
    public IRecordService RecordService { get; }
    public IRecordQueryService QueryService { get; }
    public IDesktopIntegration DesktopIntegration { get; }
    public WindowsSourceCleanup SourceCleanup { get; }
    public ManagedFileStore FileStore { get; }
    public WindowsFileClipboard FileClipboard { get; }
    public ImportWorker ImportWorker { get; }
    public RecoveryService RecoveryService { get; }
    public AttachmentService AttachmentService { get; }
    public BackupService BackupService { get; }
    public SettingsService SettingsService { get; }
    public ReminderScheduler ReminderScheduler { get; }
    public PanelViewModel PanelViewModel { get; }

    public Bootstrapper(string? databasePath = null, string? defaultAttachmentRoot = null)
    {
        string dbPath = databasePath ?? GetDefaultDatabasePath();

        Store = new SqliteStore(dbPath);
        AppLocalizer.Instance.InitializeFromEnvironmentAndStore(Store);

        string? savedCustomRoot = ReadSavedAttachmentRootPath(Store);
        _attachmentRootPath = !string.IsNullOrWhiteSpace(defaultAttachmentRoot)
            ? defaultAttachmentRoot
            : (!string.IsNullOrWhiteSpace(savedCustomRoot) ? savedCustomRoot : GetDefaultAttachmentRootPath());

        RecordService = new RecordService(Store, zoneProvider: () => AppLocalizer.Instance.ActiveTimeZone);
        QueryService = new RecordQueryService(Store, () => AppLocalizer.Instance.ActiveTimeZone);
        DesktopIntegration = new DesktopHostProbe();
        SourceCleanup = new WindowsSourceCleanup();
        FileStore = new ManagedFileStore(SourceCleanup);
        FileClipboard = new WindowsFileClipboard();
        ImportWorker = new ImportWorker(Store, FileStore);
        RecoveryService = new RecoveryService(Store, FileStore, ImportWorker);
        AttachmentService = new AttachmentService(Store, FileStore, ImportWorker, RecoveryService, _attachmentRootPath, FileClipboard);
        if (!string.IsNullOrWhiteSpace(savedCustomRoot) && string.IsNullOrWhiteSpace(defaultAttachmentRoot))
        {
            AttachmentService.SetDefaultStorageRoot(savedCustomRoot);
        }
        BackupService = new BackupService(Store, new ArchiveWriter(), FileStore.CurrentDeviceId);
        SettingsService = new SettingsService(Store, DesktopIntegration);
        ReminderScheduler = new ReminderScheduler(Store, zoneProvider: () => AppLocalizer.Instance.ActiveTimeZone);
        PanelViewModel = new PanelViewModel(RecordService, QueryService, zoneProvider: () => AppLocalizer.Instance.ActiveTimeZone)
        {
            AttachmentService = AttachmentService,
            SettingsRepository = Store
        };
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _ = SettingsService.GetPreferences();
        string intentPath = Path.Combine(AppContext.BaseDirectory, "install-intent.json");
        await SettingsService.ImportFirstRunIntentIfPresentAsync(intentPath, Store, ct);
        await RecoveryService.ReconcileIncompleteJobsAsync(ct);
    }

    public DetailViewModel CreateDetailViewModel()
    {
        return new DetailViewModel(
            RecordService,
            QueryService,
            AttachmentService,
            Store,
            AttachmentService.DefaultRootPath);
    }

    public CalendarViewModel CreateCalendarViewModel()
    {
        return new CalendarViewModel(QueryService, RecordService);
    }

    public SettingsViewModel CreateSettingsViewModel()
    {
        return new SettingsViewModel(SettingsService, AttachmentService, Store);
    }

    private static string? ReadSavedAttachmentRootPath(SqliteStore store)
    {
        try
        {
            string? raw = store.GetSettingJson("global.default_attachment_root_path");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }
            if (raw.StartsWith('"'))
            {
                return System.Text.Json.JsonSerializer.Deserialize<string>(raw);
            }
            return raw;
        }
        catch
        {
            return null;
        }
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

    public static string GetDefaultAttachmentRootPath()
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(docs))
        {
            docs = AppContext.BaseDirectory;
        }
        return Path.Combine(docs, "DesktopNotesAttachments");
    }

    public void Dispose()
    {
        Store.Dispose();
    }
}
