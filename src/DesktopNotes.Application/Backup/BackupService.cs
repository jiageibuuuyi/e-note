using System.Security.Cryptography;
using System.Text.Json;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Ports;

namespace DesktopNotes.Application.Backup;

public enum RestoreFaultStage
{
    None = 0,
    FailDuringAttachmentPublish = 1,
    FailBeforeDatabaseSwitch = 2
}

public sealed record RestoreJournal(
    Guid RestoreId,
    string Stage,
    string PreRestoreDbBackupPath,
    string StagedDbPath,
    IReadOnlyDictionary<Guid, string> RootMappings,
    string CurrentDeviceId,
    long UpdatedUtcMs);

public sealed class BackupService
{
    public const int MaxRetainedDatabaseBackups = 7;

    private readonly IBackupDatabasePort _databasePort;
    private readonly IArchivePort _archivePort;
    private readonly string _currentDeviceId;
    private readonly string _journalDirectory;
    private readonly TimeProvider _timeProvider;

    public RestoreFaultStage FaultInjectionStage { get; set; } = RestoreFaultStage.None;
    public bool RejectOnCaseCollision { get; set; } = false;

    private readonly Dictionary<string, string> _lastRestorePathMappings = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> LastRestorePathMappings => _lastRestorePathMappings;

    public BackupService(
        IBackupDatabasePort databasePort,
        IArchivePort archivePort,
        string currentDeviceId = "local-device",
        string? journalDirectory = null,
        TimeProvider? timeProvider = null)
    {
        _databasePort = databasePort ?? throw new ArgumentNullException(nameof(databasePort));
        _archivePort = archivePort ?? throw new ArgumentNullException(nameof(archivePort));
        _currentDeviceId = string.IsNullOrWhiteSpace(currentDeviceId) ? "local-device" : currentDeviceId;
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (!string.IsNullOrWhiteSpace(journalDirectory))
        {
            _journalDirectory = Path.GetFullPath(journalDirectory);
        }
        else
        {
            string dbFile = _databasePort.DatabaseFilePath;
            string? dbDir = (!string.IsNullOrWhiteSpace(dbFile) && !dbFile.Contains("mode=memory", StringComparison.OrdinalIgnoreCase))
                ? Path.GetDirectoryName(Path.GetFullPath(dbFile))
                : null;
            _journalDirectory = dbDir ?? Path.Combine(Path.GetTempPath(), "DesktopNotesRestoreJournals");
        }
        Directory.CreateDirectory(_journalDirectory);
    }

    public string PerformDailyDatabaseBackup(string backupDirectory)
    {
        string fullBackupDir = Path.GetFullPath(backupDirectory);
        Directory.CreateDirectory(fullBackupDir);

        var now = _timeProvider.GetUtcNow();
        string fileName = $"app-backup-{now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.db";
        string destPath = Path.Combine(fullBackupDir, fileName);

        _databasePort.BackupOnlineTo(destPath);
        _databasePort.VerifyDatabaseSnapshot(destPath);

        // Rotate: keep latest 7 valid backup files
        var existingBackups = new DirectoryInfo(fullBackupDir)
            .GetFiles("app-backup-*.db")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .ToList();

        for (int i = MaxRetainedDatabaseBackups; i < existingBackups.Count; i++)
        {
            try
            {
                existingBackups[i].Delete();
            }
            catch
            {
            }
        }

        return destPath;
    }

    public async Task ExportAsync(string archivePath, bool includeTrash, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            throw new AppException("invalid_archive_path", "导出路径不能为空。");
        }

        string fullArchivePath = Path.GetFullPath(archivePath);
        string stagingDir = Path.Combine(Path.GetTempPath(), "DesktopNotesBackupExport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDir);

        try
        {
            string snapshotDbPath = Path.Combine(stagingDir, "app.db");
            _databasePort.BackupOnlineTo(snapshotDbPath);

            if (!includeTrash)
            {
                _databasePort.PurgeTrashedDataInSnapshot(snapshotDbPath);
            }

            _databasePort.VerifyDatabaseSnapshot(snapshotDbPath);

            var roots = _databasePort.GetAllStorageRootsFromSnapshot(snapshotDbPath);
            foreach (var root in roots)
            {
                if (IsPathInsideDirectory(fullArchivePath, root.AbsolutePath))
                {
                    throw new AppException(
                        "backup_inside_managed_root",
                        $"备份包路径不能位于受管理的附件根目录内部: {root.AbsolutePath}");
                }
            }

            var attachments = _databasePort.GetAttachmentsFromSnapshot(snapshotDbPath, includeTrash);
            var jobsSummary = _databasePort.GetJobsSummaryFromSnapshot(snapshotDbPath);
            var rootMap = roots.ToDictionary(r => r.Id);

            var manifestFiles = new List<BackupFileEntry>();
            var manifestEmptyDirs = new List<BackupEmptyDirEntry>();
            var filesToPack = new List<(string SourceFilePath, string ArchiveEntryPath, string ExpectedSha256, long ExpectedSize)>();

            foreach (var att in attachments)
            {
                ct.ThrowIfCancellationRequested();
                if (!rootMap.TryGetValue(att.RootId, out var root))
                {
                    throw new AppException("backup_missing_root", $"找不到附件对应的存储根目录: {att.RootId}");
                }

                string normalizedRel = att.RelativePath.Replace('\\', '/').TrimStart('/');
                string fullEntryPath = Path.Combine(root.AbsolutePath, normalizedRel.Replace('/', Path.DirectorySeparatorChar));

                if (string.Equals(att.EntryKind, "file", StringComparison.OrdinalIgnoreCase))
                {
                    if (!File.Exists(fullEntryPath))
                    {
                        throw new AppException("backup_attachment_missing", $"受管理附件文件不存在: {fullEntryPath}");
                    }

                    var (size, sha256) = await ComputeFileHashAndSizeAsync(fullEntryPath, ct);
                    string archiveRel = $"roots/{att.RootId:D}/{normalizedRel}";
                    manifestFiles.Add(new BackupFileEntry(
                        att.RootId,
                        att.Id,
                        archiveRel,
                        normalizedRel,
                        size,
                        sha256));
                    filesToPack.Add((fullEntryPath, archiveRel, sha256, size));
                }
                else if (string.Equals(att.EntryKind, "directory", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Directory.Exists(fullEntryPath))
                    {
                        throw new AppException("backup_attachment_missing", $"受管理附件目录不存在: {fullEntryPath}");
                    }

                    await WalkManagedDirectoryAsync(
                        root.Id,
                        att.Id,
                        root.AbsolutePath,
                        fullEntryPath,
                        manifestFiles,
                        manifestEmptyDirs,
                        filesToPack,
                        ct);
                }
            }

            var manifest = new BackupManifest(
                FormatVersion: 1,
                SchemaVersion: 1,
                CreatedUtcMs: _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                DeviceId: _currentDeviceId,
                IncludeTrash: includeTrash,
                StorageRoots: roots.Select(r => new BackupRootEntry(r.Id, r.AbsolutePath, r.Platform)).ToList(),
                Files: manifestFiles,
                EmptyDirectories: manifestEmptyDirs,
                JobsSummary: jobsSummary);

            await _archivePort.WriteArchiveAsync(fullArchivePath, snapshotDbPath, manifest, filesToPack, ct);
        }
        finally
        {
            TryDeleteDirectory(stagingDir);
        }
    }

    public async Task RestoreAsync(
        string archivePath,
        IReadOnlyDictionary<Guid, string> rootMappings,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rootMappings);
        _lastRestorePathMappings.Clear();

        string stagingDir = Path.Combine(_journalDirectory, "staging_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDir);

        // Step 1-3: Validate archive, extract to staging, verify file hashes and SQLite integrity
        BackupManifest manifest = await _archivePort.ExtractAndValidateArchiveAsync(archivePath, stagingDir, ct);
        string stagedDbPath = Path.Combine(stagingDir, "database", "app.db");
        _databasePort.VerifyDatabaseSnapshot(stagedDbPath);

        // Ensure every storage root in manifest has a valid target mapping
        var normalizedRootMappings = new Dictionary<Guid, string>();
        foreach (var root in manifest.StorageRoots)
        {
            if (!rootMappings.TryGetValue(root.Id, out string? mappedPath) || string.IsNullOrWhiteSpace(mappedPath))
            {
                throw new AppException("missing_root_mapping", $"未提供存储根目录 {root.Id} 的目标路径映射。");
            }
            normalizedRootMappings[root.Id] = Path.GetFullPath(mappedPath);
        }

        // Step 2b: Detect case-insensitive collisions & cross-platform illegal characters per root
        var resolvedFileTargets = ResolveNonCollidingTargets(manifest.Files);
        var resolvedDirTargets = ResolveDirectoryTargets(manifest.EmptyDirectories);

        // Step 4: Backup current database & write persistent restore journal before modifying user state
        Guid restoreId = Guid.NewGuid();
        string preRestoreDbPath = Path.Combine(_journalDirectory, $"pre-restore-{restoreId:N}.db");
        _databasePort.BackupOnlineTo(preRestoreDbPath);

        var journal = new RestoreJournal(
            RestoreId: restoreId,
            Stage: "PublishingAttachments",
            PreRestoreDbBackupPath: preRestoreDbPath,
            StagedDbPath: stagedDbPath,
            RootMappings: normalizedRootMappings,
            CurrentDeviceId: _currentDeviceId,
            UpdatedUtcMs: _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        WriteJournal(journal);

        // Step 5: Publish verified attachments and empty directories to mapped roots
        int publishedCount = 0;
        foreach (var fileItem in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            string targetRoot = normalizedRootMappings[fileItem.RootId];
            string mappedRel = resolvedFileTargets[(fileItem.RootId, fileItem.TargetRelativePath)];
            string destFilePath = Path.Combine(targetRoot, mappedRel.Replace('/', Path.DirectorySeparatorChar));
            string stagedFilePath = Path.Combine(stagingDir, fileItem.ArchiveRelativePath.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(destFilePath)!);
            File.Copy(stagedFilePath, destFilePath, overwrite: true);
            publishedCount++;

            if (FaultInjectionStage == RestoreFaultStage.FailDuringAttachmentPublish && publishedCount == 1)
            {
                throw new AppException("injected_restore_publish_failure", "模拟在发布恢复附件阶段发生中断。");
            }
        }

        foreach (var emptyDir in manifest.EmptyDirectories)
        {
            ct.ThrowIfCancellationRequested();
            string targetRoot = normalizedRootMappings[emptyDir.RootId];
            string mappedRel = resolvedDirTargets[(emptyDir.RootId, emptyDir.TargetRelativePath)];
            string destDirPath = Path.Combine(targetRoot, mappedRel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(destDirPath);
        }

        // Step 6: Update journal to SwitchingDatabase and perform database replacement + root remapping
        journal = journal with
        {
            Stage = "SwitchingDatabase",
            UpdatedUtcMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };
        WriteJournal(journal);

        if (FaultInjectionStage == RestoreFaultStage.FailBeforeDatabaseSwitch)
        {
            throw new AppException("injected_restore_db_switch_failure", "模拟在切换数据库阶段发生中断。");
        }

        _databasePort.ReplaceCurrentDatabaseWith(stagedDbPath, normalizedRootMappings, _currentDeviceId);

        // Step 7: Mark restore journal completed and clean up staging
        journal = journal with
        {
            Stage = "Completed",
            UpdatedUtcMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };
        WriteJournal(journal);
        TryDeleteDirectory(stagingDir);
    }

    public Task<string> RecoverInterruptedRestoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string journalPath = GetJournalFilePath();
        if (!File.Exists(journalPath))
        {
            return Task.FromResult("NoPendingRestore");
        }

        var journal = JsonSerializer.Deserialize<RestoreJournal>(File.ReadAllText(journalPath));
        if (journal is null || string.Equals(journal.Stage, "Completed", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult("Completed");
        }

        // If interrupted during attachment publish, roll back database to PreRestoreDbBackupPath
        // while keeping any already-published files for user inspection.
        if (string.Equals(journal.Stage, "PublishingAttachments", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(journal.PreRestoreDbBackupPath))
            {
                _databasePort.VerifyDatabaseSnapshot(journal.PreRestoreDbBackupPath);
                _databasePort.ReplaceCurrentDatabaseWith(
                    journal.PreRestoreDbBackupPath,
                    new Dictionary<Guid, string>(),
                    journal.CurrentDeviceId);
            }
            WriteJournal(journal with { Stage = "RolledBackToPreRestore" });
            return Task.FromResult("RolledBackToPreRestore");
        }

        // If interrupted at SwitchingDatabase, attachments were already 100% published and verified;
        // if StagedDbPath is intact, finish switching to the new database, otherwise fall back to PreRestoreDbBackupPath.
        if (string.Equals(journal.Stage, "SwitchingDatabase", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(journal.StagedDbPath))
            {
                _databasePort.VerifyDatabaseSnapshot(journal.StagedDbPath);
                _databasePort.ReplaceCurrentDatabaseWith(
                    journal.StagedDbPath,
                    journal.RootMappings,
                    journal.CurrentDeviceId);
                WriteJournal(journal with { Stage = "CompletedViaRecovery" });
                return Task.FromResult("CompletedViaRecovery");
            }

            if (File.Exists(journal.PreRestoreDbBackupPath))
            {
                _databasePort.VerifyDatabaseSnapshot(journal.PreRestoreDbBackupPath);
                _databasePort.ReplaceCurrentDatabaseWith(
                    journal.PreRestoreDbBackupPath,
                    new Dictionary<Guid, string>(),
                    journal.CurrentDeviceId);
                WriteJournal(journal with { Stage = "RolledBackToPreRestore" });
                return Task.FromResult("RolledBackToPreRestore");
            }
        }

        return Task.FromResult(journal.Stage);
    }

    private Dictionary<(Guid RootId, string OriginalRel), string> ResolveNonCollidingTargets(
        IReadOnlyList<BackupFileEntry> files)
    {
        var result = new Dictionary<(Guid RootId, string OriginalRel), string>();
        var usedPerRoot = new Dictionary<Guid, HashSet<string>>();

        foreach (var file in files)
        {
            if (!usedPerRoot.TryGetValue(file.RootId, out var usedSet))
            {
                usedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                usedPerRoot[file.RootId] = usedSet;
            }

            string sanitized = SanitizeCrossPlatformRelativePath(file.TargetRelativePath);
            string candidate = sanitized;

            if (!usedSet.Add(candidate))
            {
                if (RejectOnCaseCollision)
                {
                    throw new AppException("case_collision", $"检测到大小写冲突路径: {file.TargetRelativePath}");
                }

                string dir = Path.GetDirectoryName(sanitized.Replace('/', Path.DirectorySeparatorChar))?.Replace(Path.DirectorySeparatorChar, '/') ?? string.Empty;
                string fileNameNoExt = Path.GetFileNameWithoutExtension(sanitized);
                string ext = Path.GetExtension(sanitized);

                int suffix = 1;
                do
                {
                    string disambiguatedName = $"{fileNameNoExt}__case_{suffix}{ext}";
                    candidate = string.IsNullOrEmpty(dir) ? disambiguatedName : $"{dir}/{disambiguatedName}";
                    suffix++;
                } while (!usedSet.Add(candidate));
            }

            result[(file.RootId, file.TargetRelativePath)] = candidate;
            if (!string.Equals(file.TargetRelativePath, candidate, StringComparison.Ordinal))
            {
                _lastRestorePathMappings[file.TargetRelativePath] = candidate;
            }
        }

        return result;
    }

    private Dictionary<(Guid RootId, string OriginalRel), string> ResolveDirectoryTargets(
        IReadOnlyList<BackupEmptyDirEntry> dirs)
    {
        var result = new Dictionary<(Guid RootId, string OriginalRel), string>();
        foreach (var d in dirs)
        {
            string sanitized = SanitizeCrossPlatformRelativePath(d.TargetRelativePath);
            result[(d.RootId, d.TargetRelativePath)] = sanitized;
            if (!string.Equals(d.TargetRelativePath, sanitized, StringComparison.Ordinal))
            {
                _lastRestorePathMappings[d.TargetRelativePath] = sanitized;
            }
        }
        return result;
    }

    public static string SanitizeCrossPlatformRelativePath(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/').Trim('/');
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        char[] invalidChars = ['<', '>', ':', '"', '|', '?', '*'];

        for (int i = 0; i < segments.Length; i++)
        {
            string seg = segments[i];
            foreach (char c in invalidChars)
            {
                seg = seg.Replace(c, '_');
            }
            seg = seg.TrimEnd(' ', '.');
            if (string.IsNullOrEmpty(seg))
            {
                seg = "_";
            }
            segments[i] = seg;
        }

        return string.Join('/', segments);
    }

    private static async Task WalkManagedDirectoryAsync(
        Guid rootId,
        Guid attachmentId,
        string rootAbsolutePath,
        string currentDirFullPath,
        List<BackupFileEntry> manifestFiles,
        List<BackupEmptyDirEntry> manifestEmptyDirs,
        List<(string SourceFilePath, string ArchiveEntryPath, string ExpectedSha256, long ExpectedSize)> filesToPack,
        CancellationToken ct)
    {
        var dirInfo = new DirectoryInfo(currentDirFullPath);
        var entries = dirInfo.GetFileSystemInfos();

        if (entries.Length == 0)
        {
            string relDir = Path.GetRelativePath(rootAbsolutePath, currentDirFullPath).Replace('\\', '/');
            manifestEmptyDirs.Add(new BackupEmptyDirEntry(rootId, attachmentId, relDir));
            return;
        }

        foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || entry.LinkTarget is not null)
            {
                continue;
            }

            if (entry is FileInfo fi)
            {
                string relFile = Path.GetRelativePath(rootAbsolutePath, fi.FullName).Replace('\\', '/');
                string archiveRel = $"roots/{rootId:D}/{relFile}";
                var (size, sha256) = await ComputeFileHashAndSizeAsync(fi.FullName, ct);
                manifestFiles.Add(new BackupFileEntry(rootId, attachmentId, archiveRel, relFile, size, sha256));
                filesToPack.Add((fi.FullName, archiveRel, sha256, size));
            }
            else if (entry is DirectoryInfo subDir)
            {
                await WalkManagedDirectoryAsync(
                    rootId,
                    attachmentId,
                    rootAbsolutePath,
                    subDir.FullName,
                    manifestFiles,
                    manifestEmptyDirs,
                    filesToPack,
                    ct);
            }
        }
    }

    private static async Task<(long Size, string Sha256Hex)> ComputeFileHashAndSizeAsync(string filePath, CancellationToken ct)
    {
        var buffer = new byte[1024 * 1024];
        long total = 0;
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, useAsync: true);
        int read;
        while ((read = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            sha256.AppendData(buffer, 0, read);
            total += read;
        }
        return (total, Convert.ToHexStringLower(sha256.GetHashAndReset()));
    }

    private static bool IsPathInsideDirectory(string candidatePath, string directoryPath)
    {
        string fullCandidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string fullDir = Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullCandidate.StartsWith(fullDir, comparison);
    }

    private string GetJournalFilePath() => Path.Combine(_journalDirectory, "restore-journal.json");

    private void WriteJournal(RestoreJournal journal)
    {
        string path = GetJournalFilePath();
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(journal, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
