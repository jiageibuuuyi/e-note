using System.Security.Cryptography;
using DesktopNotes.Application.Attachments;
using DesktopNotes.Application.Backup;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Ports;
using DesktopNotes.Application.Queries;
using DesktopNotes.Application.Records;
using DesktopNotes.Domain;
using DesktopNotes.Infrastructure.Backup;
using DesktopNotes.Infrastructure.Files;
using DesktopNotes.Infrastructure.Persistence;
using Xunit;

namespace DesktopNotes.Integration.Tests;

public sealed class BackupRestoreTests : IDisposable
{
    private readonly string _tempRoot;

    public BackupRestoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DesktopNotesBackupTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task DailyDatabaseBackup_UsesOnlineBackupApi_AndRotatesToSevenCopies()
    {
        string dbPath = Path.Combine(_tempRoot, "daily", "app.db");
        using var store = new SqliteStore(dbPath);
        var recordService = new RecordService(store);

        await recordService.SaveAsync(
            new SaveRecordRequest(
                Guid.NewGuid(),
                0,
                RecordKind.Task,
                null,
                "每日备份验证任务",
                "正文",
                new EffectiveFields(null, null, Priority.High, null, DueSpec.Empty, null),
                InheritField.None),
            CancellationToken.None);

        var backupService = new BackupService(store, new ArchiveWriter(), "device-A");
        string backupDir = Path.Combine(_tempRoot, "daily_backups");

        for (int i = 0; i < 10; i++)
        {
            backupService.PerformDailyDatabaseBackup(backupDir);
            await Task.Delay(5);
        }

        var files = Directory.GetFiles(backupDir, "app-backup-*.db");
        Assert.Equal(BackupService.MaxRetainedDatabaseBackups, files.Length);
    }

    [Fact]
    public async Task ExportAndRestore_PreservesParentChildInheritance_EmptyDeadline_CompletedSnapshots_TwoRoots_EmptyFolders_AndTrashOption()
    {
        string sourceDbPath = Path.Combine(_tempRoot, "source_env", "app.db");
        string sourceRoot1Path = Path.Combine(_tempRoot, "source_env", "root1");
        string sourceRoot2Path = Path.Combine(_tempRoot, "source_env", "root2");
        string externalSourceDir = Path.Combine(_tempRoot, "external_sources");
        Directory.CreateDirectory(sourceRoot1Path);
        Directory.CreateDirectory(sourceRoot2Path);
        Directory.CreateDirectory(externalSourceDir);

        Guid parentId = Guid.NewGuid();
        Guid childInheritedId = Guid.NewGuid();
        Guid childExplicitNullDueId = Guid.NewGuid();
        Guid completedTaskId = Guid.NewGuid();
        Guid root1Id;
        Guid root2Id;

        using (var sourceStore = new SqliteStore(sourceDbPath))
        {
            var recordService = new RecordService(sourceStore);
            var cleanup = new NoOpSourceCleanup("device-source");
            var fileStore = new ManagedFileStore(cleanup, "device-source");
            var worker = new ImportWorker(sourceStore, fileStore);
            var recovery = new RecoveryService(sourceStore, fileStore, worker);
            var attachmentService = new AttachmentService(sourceStore, fileStore, worker, recovery, sourceRoot1Path);

            root1Id = sourceStore.ListStorageRoots().Single().Id;
            root2Id = Guid.NewGuid();
            sourceStore.RegisterStorageRoot(root2Id, sourceRoot2Path, "windows", DateTimeOffset.UtcNow);

            // 1. Parent task with priority High, assignee "Alice", due DateOnly 2026-11-10, custom attachment root2
            await recordService.SaveAsync(
                new SaveRecordRequest(
                    parentId,
                    0,
                    RecordKind.Task,
                    null,
                    "父项目计划",
                    "父级项目计划\n包含跨根目录附件与继承子项",
                    new EffectiveFields(null, "Alice", Priority.High, null, new DueSpec(DueKind.Date, new DateOnly(2026, 11, 10), null, null), root2Id),
                    InheritField.None),
                CancellationToken.None);

            // 2. Child 1: inherits all fields from parent
            await recordService.SaveAsync(
                new SaveRecordRequest(
                    childInheritedId,
                    0,
                    RecordKind.Task,
                    parentId,
                    string.Empty,
                    "继承父属性的子任务",
                    new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null),
                    InheritField.All),
                CancellationToken.None);

            // 3. Child 2: explicitly clears deadline (independent null deadline -> InheritField.All & ~InheritField.Due)
            await recordService.SaveAsync(
                new SaveRecordRequest(
                    childExplicitNullDueId,
                    0,
                    RecordKind.Task,
                    parentId,
                    string.Empty,
                    "显式清空截止期的子任务",
                    new EffectiveFields(null, "Bob", Priority.Normal, null, DueSpec.Empty, root1Id),
                    InheritField.None),
                CancellationToken.None);

            // 4. Completed task with frozen snapshot
            var createdComplete = (await recordService.SaveAsync(
                new SaveRecordRequest(
                    completedTaskId,
                    0,
                    RecordKind.Task,
                    parentId,
                    string.Empty,
                    "已完成并冻结快照的子任务",
                    new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null),
                    InheritField.All),
                CancellationToken.None)).Changed.Single(r => r.Id == completedTaskId);

            await recordService.CloseAsync(completedTaskId, createdComplete.Version, includeChildren: false, CancellationToken.None);

            // Mutate parent priority to Highest after child was completed -> completed snapshot stays High, active child1 becomes Highest
            var latestParent = (await recordService.GetAsync(parentId, CancellationToken.None))!;
            await recordService.SaveAsync(
                new SaveRecordRequest(
                    parentId,
                    latestParent.Version,
                    RecordKind.Task,
                    null,
                    latestParent.Title,
                    latestParent.Body,
                    latestParent.Own with { Priority = Priority.Highest },
                    InheritField.None),
                CancellationToken.None);

            // 5. Attachments in root1 (for child2) and root2 (nested directory with empty subfolder for parent)
            string fileForRoot1 = Path.Combine(externalSourceDir, "spec.txt");
            await File.WriteAllTextAsync(fileForRoot1, "Root1 独立附件内容");

            string folderForRoot2 = Path.Combine(externalSourceDir, "design_bundle");
            Directory.CreateDirectory(Path.Combine(folderForRoot2, "empty_sub"));
            Directory.CreateDirectory(Path.Combine(folderForRoot2, "docs"));
            await File.WriteAllTextAsync(Path.Combine(folderForRoot2, "docs", "architecture.md"), "# 架构说明\nV1 完整备份验证");

            string trashedFileSource = Path.Combine(externalSourceDir, "trashed_note.txt");
            await File.WriteAllTextAsync(trashedFileSource, "逻辑回收区中的附件");

            await attachmentService.EnqueueAsync(
                new ImportRequest(Guid.NewGuid(), childExplicitNullDueId, [fileForRoot1], ImportIntent.Copy, ImportOrigin.FilePicker),
                CancellationToken.None);

            await attachmentService.EnqueueAsync(
                new ImportRequest(Guid.NewGuid(), parentId, [folderForRoot2], ImportIntent.Copy, ImportOrigin.FilePicker),
                CancellationToken.None);

            await attachmentService.EnqueueAsync(
                new ImportRequest(Guid.NewGuid(), childExplicitNullDueId, [trashedFileSource], ImportIntent.Copy, ImportOrigin.FilePicker),
                CancellationToken.None);

            var child2Attachments = await attachmentService.ListForRecordAsync(childExplicitNullDueId, includeTrashed: true, CancellationToken.None);
            var trashedAtt = child2Attachments.Single(a => a.OriginalName == "trashed_note.txt");
            await attachmentService.TrashAttachmentAsync(trashedAtt.Id, CancellationToken.None);

            // Export with includeTrash = true
            string archiveWithTrash = Path.Combine(_tempRoot, "backups", "full_with_trash.zip");
            var backupService = new BackupService(sourceStore, new ArchiveWriter(), "device-source");
            await backupService.ExportAsync(archiveWithTrash, includeTrash: true, CancellationToken.None);

            // Delete old source roots before restoring to prove restore never reads old paths
            Directory.Delete(sourceRoot1Path, recursive: true);
            Directory.Delete(sourceRoot2Path, recursive: true);

            // Restore into a brand new target environment and new root directories
            string targetDbPath = Path.Combine(_tempRoot, "target_env", "app.db");
            string targetRoot1Path = Path.Combine(_tempRoot, "target_env", "mapped_root_1");
            string targetRoot2Path = Path.Combine(_tempRoot, "target_env", "mapped_root_2");

            using var targetStore = new SqliteStore(targetDbPath);
            var targetBackupService = new BackupService(targetStore, new ArchiveWriter(), "device-target");
            await targetBackupService.RestoreAsync(
                archiveWithTrash,
                new Dictionary<Guid, string>
                {
                    [root1Id] = targetRoot1Path,
                    [root2Id] = targetRoot2Path
                },
                CancellationToken.None);

            var targetRecordService = new RecordService(targetStore);
            var restoredParent = (await targetRecordService.GetAsync(parentId, CancellationToken.None))!;
            var restoredChild1 = (await targetRecordService.GetAsync(childInheritedId, CancellationToken.None))!;
            var restoredChild2 = (await targetRecordService.GetAsync(childExplicitNullDueId, CancellationToken.None))!;
            var restoredCompleted = (await targetRecordService.GetAsync(completedTaskId, CancellationToken.None))!;

            Assert.Equal(Priority.Highest, restoredParent.Effective.Priority);
            Assert.Equal(Priority.Highest, restoredChild1.Effective.Priority);
            Assert.Equal(new DateOnly(2026, 11, 10), restoredChild1.Effective.Due.Date);
            Assert.Equal(DueKind.None, restoredChild2.Effective.Due.Kind);
            Assert.Equal(Priority.High, restoredCompleted.Effective.Priority); // Frozen completion snapshot preserved!

            // Verify mapped storage roots & restored files + empty directories
            Assert.Equal(Path.GetFullPath(targetRoot1Path), targetStore.GetStorageRoot(root1Id)!.AbsolutePath);
            Assert.Equal(Path.GetFullPath(targetRoot2Path), targetStore.GetStorageRoot(root2Id)!.AbsolutePath);

            var parentAtts = targetStore.ListAttachmentsForRecord(parentId, includeTrashed: false);
            Assert.Single(parentAtts);
            string restoredBundleDir = Path.Combine(targetRoot2Path, parentAtts[0].RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(Directory.Exists(Path.Combine(restoredBundleDir, "empty_sub")));
            Assert.Equal("# 架构说明\nV1 完整备份验证", await File.ReadAllTextAsync(Path.Combine(restoredBundleDir, "docs", "architecture.md")));

            var child2AttsWithTrash = targetStore.ListAttachmentsForRecord(childExplicitNullDueId, includeTrashed: true);
            Assert.Equal(2, child2AttsWithTrash.Count);
        }
    }

    [Fact]
    public async Task Restore_WithForeignDeviceSourceCleanupPendingJob_NeverInvokesSourceCleanupAndTransitionsToNeedsReview()
    {
        string sourceDbPath = Path.Combine(_tempRoot, "foreign_source", "app.db");
        string sourceRootPath = Path.Combine(_tempRoot, "foreign_source", "root");
        Directory.CreateDirectory(sourceRootPath);

        Guid jobId = Guid.NewGuid();
        Guid recId = Guid.NewGuid();
        Guid rootId;

        using (var sourceStore = new SqliteStore(sourceDbPath))
        {
            var recordService = new RecordService(sourceStore);
            await recordService.SaveAsync(
                new SaveRecordRequest(
                    recId,
                    0,
                    RecordKind.Task,
                    null,
                    string.Empty,
                    "旧机器残留清理作业测试",
                    new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null),
                    InheritField.None),
                CancellationToken.None);

            rootId = sourceStore.EnsureDefaultStorageRoot(sourceRootPath, "windows", DateTimeOffset.UtcNow).Id;
            Guid attId = Guid.NewGuid();
            string fakeSourceFile = Path.Combine(_tempRoot, "do_not_touch_source.txt");
            await File.WriteAllTextAsync(fakeSourceFile, "此文件绝不能被恢复端删除");

            sourceStore.CreatePlannedJob(
                new AttachmentJobRecord(
                    jobId,
                    Guid.NewGuid(),
                    recId,
                    attId,
                    rootId,
                    "move",
                    fakeSourceFile,
                    $"tasks/{recId:D}/{attId:D}/do_not_touch_source.txt",
                    $".desktopnotes-staging/{jobId:D}/payload",
                    JobStates.SourceCleanupPending,
                    "foreign-device-001",
                    "source_locked",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                [
                    new AttachmentJobItemRecord(
                        jobId,
                        "do_not_touch_source.txt",
                        "file",
                        "id-1",
                        24,
                        1000,
                        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("此文件绝不能被恢复端删除"))),
                        Verified: true,
                        SourceRemoved: false)
                ]);

            string archivePath = Path.Combine(_tempRoot, "foreign_backup.zip");
            var exportService = new BackupService(sourceStore, new ArchiveWriter(), "foreign-device-001");
            await exportService.ExportAsync(archivePath, includeTrash: true, CancellationToken.None);

            // Restore on local-device-002 with a strict spy ISourceCleanupPort that throws if called
            string targetDbPath = Path.Combine(_tempRoot, "local_target", "app.db");
            string targetRootPath = Path.Combine(_tempRoot, "local_target", "root");
            using var targetStore = new SqliteStore(targetDbPath);

            var spyCleanup = new FailIfCalledSourceCleanup("local-device-002");
            var targetFileStore = new ManagedFileStore(spyCleanup, "local-device-002");
            var targetWorker = new ImportWorker(targetStore, targetFileStore);
            var targetRecovery = new RecoveryService(targetStore, targetFileStore, targetWorker);

            var restoreService = new BackupService(targetStore, new ArchiveWriter(), "local-device-002");
            await restoreService.RestoreAsync(
                archivePath,
                new Dictionary<Guid, string> { [rootId] = targetRootPath },
                CancellationToken.None);

            // Also run startup recovery service to prove it also never calls RemoveVerifiedSource
            await targetRecovery.ReconcileIncompleteJobsAsync(CancellationToken.None);

            Assert.Equal(0, spyCleanup.RemoveCallCount);
            Assert.True(File.Exists(fakeSourceFile));

            var restoredJob = targetStore.GetJobRecord(jobId)!;
            Assert.Equal(JobStates.NeedsReview, restoredJob.State);
        }
    }

    [Fact]
    public async Task Restore_FaultInjectionAtAttachmentPublishAndDbSwitch_RecoversConsistentStateViaJournal()
    {
        // Prepare a valid backup archive containing "来自备份的新任务"
        string exportDbPath = Path.Combine(_tempRoot, "fault_export", "app.db");
        string exportRootPath = Path.Combine(_tempRoot, "fault_export", "root");
        string extDir = Path.Combine(_tempRoot, "fault_ext");
        Directory.CreateDirectory(exportRootPath);
        Directory.CreateDirectory(extDir);

        string archivePath = Path.Combine(_tempRoot, "fault_backup.zip");
        Guid rootId;
        Guid exportedTaskId = Guid.NewGuid();

        using (var exportStore = new SqliteStore(exportDbPath))
        {
            var recordService = new RecordService(exportStore);
            var cleanup = new NoOpSourceCleanup("dev-1");
            var fileStore = new ManagedFileStore(cleanup, "dev-1");
            var worker = new ImportWorker(exportStore, fileStore);
            var recovery = new RecoveryService(exportStore, fileStore, worker);
            var attService = new AttachmentService(exportStore, fileStore, worker, recovery, exportRootPath);
            rootId = exportStore.ListStorageRoots().Single().Id;

            await recordService.SaveAsync(
                new SaveRecordRequest(
                    exportedTaskId,
                    0,
                    RecordKind.Task,
                    null,
                    "来自备份的新任务",
                    "备份内容",
                    new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null),
                    InheritField.None),
                CancellationToken.None);

            string f1 = Path.Combine(extDir, "a.txt");
            string f2 = Path.Combine(extDir, "b.txt");
            await File.WriteAllTextAsync(f1, "File A");
            await File.WriteAllTextAsync(f2, "File B");

            await attService.EnqueueAsync(
                new ImportRequest(Guid.NewGuid(), exportedTaskId, [f1, f2], ImportIntent.Copy, ImportOrigin.FilePicker),
                CancellationToken.None);

            var backupSvc = new BackupService(exportStore, new ArchiveWriter(), "dev-1");
            await backupSvc.ExportAsync(archivePath, includeTrash: true, CancellationToken.None);
        }

        // Target DB initially has "恢复前本地原有任务"
        string targetDbPath = Path.Combine(_tempRoot, "fault_target", "app.db");
        string journalDir = Path.Combine(_tempRoot, "fault_target", "journal");
        string targetRootPath = Path.Combine(_tempRoot, "fault_target", "root");
        Guid originalTaskId = Guid.NewGuid();

        using var targetStore = new SqliteStore(targetDbPath);
        var targetRecordSvc = new RecordService(targetStore);
        await targetRecordSvc.SaveAsync(
            new SaveRecordRequest(
                originalTaskId,
                0,
                RecordKind.Task,
                null,
                "恢复前本地原有任务",
                "本地内容",
                new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null),
                InheritField.None),
            CancellationToken.None);

        var targetBackupSvc = new BackupService(targetStore, new ArchiveWriter(), "dev-2", journalDir);

        // Fault 1: Fail during attachment publish -> recovery rolls back cleanly to pre-restore DB
        targetBackupSvc.FaultInjectionStage = RestoreFaultStage.FailDuringAttachmentPublish;
        await Assert.ThrowsAsync<AppException>(() => targetBackupSvc.RestoreAsync(
            archivePath,
            new Dictionary<Guid, string> { [rootId] = targetRootPath },
            CancellationToken.None));

        string outcome1 = await targetBackupSvc.RecoverInterruptedRestoreAsync(CancellationToken.None);
        Assert.Equal("RolledBackToPreRestore", outcome1);

        var stillOriginal = await targetRecordSvc.GetAsync(originalTaskId, CancellationToken.None);
        Assert.NotNull(stillOriginal);
        Assert.Equal("恢复前本地原有任务", stillOriginal.Title);

        // Fault 2: Fail right before database switch -> recovery completes switch to verified staged DB
        targetBackupSvc.FaultInjectionStage = RestoreFaultStage.FailBeforeDatabaseSwitch;
        await Assert.ThrowsAsync<AppException>(() => targetBackupSvc.RestoreAsync(
            archivePath,
            new Dictionary<Guid, string> { [rootId] = targetRootPath },
            CancellationToken.None));

        string outcome2 = await targetBackupSvc.RecoverInterruptedRestoreAsync(CancellationToken.None);
        Assert.Equal("CompletedViaRecovery", outcome2);

        var targetQuery = new RecordQueryService(targetStore);
        var restoredPage = await targetQuery.SearchAsync(
            new RecordQuery(RecordKind.Task, RecordStatus.Active, false, string.Empty, [], 0, 50),
            CancellationToken.None);
        Assert.Single(restoredPage.Items);
        Assert.Equal("来自备份的新任务", restoredPage.Items[0].DisplayTitle);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class NoOpSourceCleanup(string deviceId) : ISourceCleanupPort
    {
        public string GetDeviceId(string path) => deviceId;
        public string GetFileIdentity(string path) => path;
        public SourceCleanupOutcome RemoveVerifiedSource(string sourcePath, string expectedIdentity, string? expectedSha256)
            => SourceCleanupOutcome.Removed;
        public SourceCleanupOutcome RemoveEmptyVerifiedDirectory(string directoryPath, string expectedIdentity, IReadOnlySet<string> expectedChildRelativeNames)
            => SourceCleanupOutcome.Removed;
    }

    private sealed class FailIfCalledSourceCleanup(string deviceId) : ISourceCleanupPort
    {
        public int RemoveCallCount { get; private set; }

        public string GetDeviceId(string path) => deviceId;
        public string GetFileIdentity(string path) => path;

        public SourceCleanupOutcome RemoveVerifiedSource(string sourcePath, string expectedIdentity, string? expectedSha256)
        {
            RemoveCallCount++;
            throw new InvalidOperationException("禁止在恢复的旧设备作业上调用 RemoveVerifiedSource！");
        }

        public SourceCleanupOutcome RemoveEmptyVerifiedDirectory(string directoryPath, string expectedIdentity, IReadOnlySet<string> expectedChildRelativeNames)
        {
            RemoveCallCount++;
            throw new InvalidOperationException("禁止在恢复的旧设备作业上调用 RemoveEmptyVerifiedDirectory！");
        }
    }
}
