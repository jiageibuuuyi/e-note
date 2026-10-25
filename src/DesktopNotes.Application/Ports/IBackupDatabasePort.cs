using DesktopNotes.Application.Contracts;

namespace DesktopNotes.Application.Ports;

public sealed record BackupManifest(
    int FormatVersion,
    int SchemaVersion,
    long CreatedUtcMs,
    string DeviceId,
    bool IncludeTrash,
    IReadOnlyList<BackupRootEntry> StorageRoots,
    IReadOnlyList<BackupFileEntry> Files,
    IReadOnlyList<BackupEmptyDirEntry> EmptyDirectories,
    IReadOnlyList<BackupJobSummary> JobsSummary);

public sealed record BackupRootEntry(
    Guid Id,
    string OriginalPath,
    string Platform);

public sealed record BackupFileEntry(
    Guid RootId,
    Guid AttachmentId,
    string ArchiveRelativePath,
    string TargetRelativePath,
    long SizeBytes,
    string Sha256Hex);

public sealed record BackupEmptyDirEntry(
    Guid RootId,
    Guid AttachmentId,
    string TargetRelativePath);

public sealed record BackupJobSummary(
    Guid JobId,
    Guid AttachmentId,
    string State,
    string SourceDeviceId);

public interface IBackupDatabasePort
{
    string DatabaseFilePath { get; }
    void BackupOnlineTo(string destinationDbPath);
    void PurgeTrashedDataInSnapshot(string snapshotDbPath);
    void VerifyDatabaseSnapshot(string snapshotDbPath);
    IReadOnlyList<StorageRootRecord> GetAllStorageRootsFromSnapshot(string snapshotDbPath);
    IReadOnlyList<AttachmentView> GetAttachmentsFromSnapshot(string snapshotDbPath, bool includeTrash);
    IReadOnlyList<BackupJobSummary> GetJobsSummaryFromSnapshot(string snapshotDbPath);
    void ReplaceCurrentDatabaseWith(
        string verifiedStagedDbPath,
        IReadOnlyDictionary<Guid, string> rootMappings,
        string currentDeviceId);
}

public interface IArchivePort
{
    Task WriteArchiveAsync(
        string archivePath,
        string snapshotDbPath,
        BackupManifest manifest,
        IReadOnlyList<(string SourceFilePath, string ArchiveEntryPath, string ExpectedSha256, long ExpectedSize)> filesToPack,
        CancellationToken ct);

    Task<BackupManifest> ExtractAndValidateArchiveAsync(
        string archivePath,
        string stagingDirectory,
        CancellationToken ct);
}
