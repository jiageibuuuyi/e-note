using System.Text;
using DesktopNotes.Application.Attachments;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Records;
using DesktopNotes.Domain;
using DesktopNotes.Infrastructure.Files;
using DesktopNotes.Infrastructure.Persistence;
using DesktopNotes.Platforms.Windows;
using Xunit;

namespace DesktopNotes.Integration.Tests;

public class AttachmentRecoveryTests
{
    private static readonly TimeZoneInfo TestZone = TimeZoneInfo.CreateCustomTimeZone(
        "TestUTC8", TimeSpan.FromHours(8), "TestUTC8", "TestUTC8");

    [Fact]
    public async Task Fault_matrix_A_through_F_preserves_data_and_reconciles_correctly()
    {
        using var fixture = new TempDirectoryFixture();
        using var store = new SqliteStore(":memory:");
        var recordService = new RecordService(store, zoneProvider: () => TestZone);
        var cleanup = new WindowsSourceCleanup();
        var fileStore = new ManagedFileStore(cleanup);
        var worker = new ImportWorker(store, fileStore);
        var recovery = new RecoveryService(store, fileStore, worker);
        var attachmentService = new AttachmentService(store, fileStore, worker, recovery, fixture.StorageRootDir);

        Guid taskId = Guid.NewGuid();
        await recordService.SaveAsync(new SaveRecordRequest(
            taskId, 0, RecordKind.Task, null, "故障恢复矩阵任务", "",
            new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null),
            InheritField.None), CancellationToken.None);

        // --- 故障A：verified后强退 -> 重启继续发布；强退时源未提前删除 ---
        string fileA = Path.Combine(fixture.SourceDir, "faultA.txt");
        File.WriteAllText(fileA, "Fault A Content", Encoding.UTF8);
        worker.StopAfterStage = JobStates.Verified;

        await Assert.ThrowsAsync<SimulatedCrashException>(() =>
            attachmentService.EnqueueAsync(new ImportRequest(
                Guid.NewGuid(), taskId, new[] { fileA }, ImportIntent.Move, ImportOrigin.Drag), CancellationToken.None));

        Assert.True(File.Exists(fileA)); // Source NOT deleted early!
        worker.StopAfterStage = null;
        await recovery.ReconcileIncompleteJobsAsync(CancellationToken.None);
        Assert.False(File.Exists(fileA)); // Cleaned after recovery completed publish & registration

        // --- 故障B：published后、数据库登记前强退 -> 找回目标并登记同一 attachment_id ---
        string fileB = Path.Combine(fixture.SourceDir, "faultB.txt");
        File.WriteAllText(fileB, "Fault B Content", Encoding.UTF8);
        worker.CrashAfterPublishBeforeDbUpdate = true;

        await Assert.ThrowsAsync<SimulatedCrashException>(() =>
            attachmentService.EnqueueAsync(new ImportRequest(
                Guid.NewGuid(), taskId, new[] { fileB }, ImportIntent.Move, ImportOrigin.Drag), CancellationToken.None));

        var jobB = store.GetIncompleteJobs().Single(j => j.SourcePath == fileB);
        Guid expectedAttIdB = jobB.AttachmentId;
        Assert.Null(store.GetAttachment(expectedAttIdB)); // Not registered in DB yet

        worker.CrashAfterPublishBeforeDbUpdate = false;
        await recovery.ReconcileIncompleteJobsAsync(CancellationToken.None);

        var registeredB = store.GetAttachment(expectedAttIdB);
        Assert.NotNull(registeredB);
        Assert.Equal("done", store.GetJobRecord(jobB.Id)!.State);
        Assert.False(File.Exists(fileB));

        // --- 故障C：registered后删源被拒绝 -> 正式附件可打开，源仍在，状态为 source_cleanup_pending ---
        string fileC = Path.Combine(fixture.SourceDir, "faultC.txt");
        File.WriteAllText(fileC, "Fault C Content", Encoding.UTF8);
        cleanup.SimulateDeleteDenied = true;

        var jobsC = await attachmentService.EnqueueAsync(new ImportRequest(
            Guid.NewGuid(), taskId, new[] { fileC }, ImportIntent.Move, ImportOrigin.Drag), CancellationToken.None);
        cleanup.SimulateDeleteDenied = false;

        var jobViewC = await attachmentService.GetJobAsync(jobsC[0], CancellationToken.None);
        Assert.Equal(JobStates.SourceCleanupPending, jobViewC.State);
        Assert.True(File.Exists(fileC));
        var jobRecordC = store.GetJobRecord(jobsC[0])!;
        Assert.NotNull(store.GetAttachment(jobRecordC.AttachmentId));

        // --- 故障D：目录复制期间新增文件 X -> X 保留，不递归强删源树，任务报告差异 ---
        string dirD = Path.Combine(fixture.SourceDir, "dirD");
        Directory.CreateDirectory(dirD);
        string knownFileD = Path.Combine(dirD, "known.txt");
        File.WriteAllText(knownFileD, "Known File D", Encoding.UTF8);
        string addedDuringCopyD = Path.Combine(dirD, "new_during_copy_X.txt");

        fileStore.OnDuringCopyHook = _ =>
        {
            if (!File.Exists(addedDuringCopyD))
            {
                File.WriteAllText(addedDuringCopyD, "New file X added during copy", Encoding.UTF8);
            }
        };

        var jobsD = await attachmentService.EnqueueAsync(new ImportRequest(
            Guid.NewGuid(), taskId, new[] { dirD }, ImportIntent.Move, ImportOrigin.Drag), CancellationToken.None);
        fileStore.OnDuringCopyHook = null;

        var jobViewD = await attachmentService.GetJobAsync(jobsD[0], CancellationToken.None);
        Assert.Equal(JobStates.SourceCleanupPending, jobViewD.State);
        Assert.Equal(ErrorCodes.SourceChanged, jobViewD.ErrorCode);
        Assert.False(File.Exists(knownFileD)); // Verified manifest item was cleaned
        Assert.True(File.Exists(addedDuringCopyD)); // Newly added file X was preserved!

        // --- 故障E：清理前源路径被替换 -> SourceChanged / needs_review，替换文件绝不删除 ---
        string fileE = Path.Combine(fixture.SourceDir, "faultE.txt");
        File.WriteAllText(fileE, "Original E Content", Encoding.UTF8);
        worker.StopAfterStage = JobStates.Registered;

        await Assert.ThrowsAsync<SimulatedCrashException>(() =>
            attachmentService.EnqueueAsync(new ImportRequest(
                Guid.NewGuid(), taskId, new[] { fileE }, ImportIntent.Move, ImportOrigin.Drag), CancellationToken.None));
        worker.StopAfterStage = null;

        // Replace source file E before cleanup runs
        File.WriteAllText(fileE, "Replaced E Content With Different Hash And Size!", Encoding.UTF8);
        await recovery.ReconcileIncompleteJobsAsync(CancellationToken.None);

        var jobE = store.GetIncompleteJobs().Single(j => j.SourcePath == fileE);
        Assert.Equal(JobStates.NeedsReview, jobE.State);
        Assert.Equal(ErrorCodes.SourceChanged, jobE.ErrorCode);
        Assert.True(File.Exists(fileE)); // Replacement file preserved!

        // --- 故障F：清理一部分后取消 -> 目标正式附件保留，显示 source_cleanup_pending，不声称整体回滚 ---
        string dirF = Path.Combine(fixture.SourceDir, "dirF");
        Directory.CreateDirectory(dirF);
        string f1 = Path.Combine(dirF, "1.txt");
        string f2 = Path.Combine(dirF, "2.txt");
        File.WriteAllText(f1, "File 1", Encoding.UTF8);
        File.WriteAllText(f2, "File 2", Encoding.UTF8);

        worker.OnBeforeCleanItemIndex = (jid, idx) =>
        {
            if (idx == 1)
            {
                worker.RequestCancel(jid);
            }
        };

        var jobsF = await attachmentService.EnqueueAsync(new ImportRequest(
            Guid.NewGuid(), taskId, new[] { dirF }, ImportIntent.Move, ImportOrigin.Drag), CancellationToken.None);
        worker.OnBeforeCleanItemIndex = null;

        var jobViewF = await attachmentService.GetJobAsync(jobsF[0], CancellationToken.None);
        Assert.Equal(JobStates.SourceCleanupPending, jobViewF.State);
        var jobRecF = store.GetJobRecord(jobsF[0])!;
        Assert.NotNull(store.GetAttachment(jobRecF.AttachmentId)); // Target attachment preserved!
        Assert.True(File.Exists(f2)); // Remaining source file was not deleted after cancel
    }
}
