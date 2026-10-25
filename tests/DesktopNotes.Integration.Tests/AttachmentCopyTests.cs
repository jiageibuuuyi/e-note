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

public sealed class TempDirectoryFixture : IDisposable
{
    public string RootDir { get; }
    public string SourceDir { get; }
    public string StorageRootDir { get; }

    public TempDirectoryFixture()
    {
        RootDir = Path.Combine(Path.GetTempPath(), "desktopnotes-test-" + Guid.NewGuid().ToString("N"));
        SourceDir = Path.Combine(RootDir, "source");
        StorageRootDir = Path.Combine(RootDir, "storage");
        Directory.CreateDirectory(SourceDir);
        Directory.CreateDirectory(StorageRootDir);
    }

    public void Dispose()
    {
        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        string fullRoot = Path.GetFullPath(RootDir);
        if (fullRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullRoot))
        {
            try
            {
                Directory.Delete(fullRoot, recursive: true);
            }
            catch
            {
            }
        }
    }
}

public class AttachmentCopyTests
{
    private static readonly TimeZoneInfo TestZone = TimeZoneInfo.CreateCustomTimeZone(
        "TestUTC8", TimeSpan.FromHours(8), "TestUTC8", "TestUTC8");

    [Fact]
    public async Task Directory_copy_preserves_nested_files_empty_folders_and_deduplicates_child_paths()
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
            taskId, 0, RecordKind.Task, null, "附件测试任务", "",
            new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null),
            InheritField.None), CancellationToken.None);

        // Prepare fixture tree matching T06 specification
        string folderToImport = Path.Combine(fixture.SourceDir, "bundle");
        string emptyDir = Path.Combine(folderToImport, "empty");
        string nestedDir = Path.Combine(folderToImport, "nested");
        Directory.CreateDirectory(emptyDir);
        Directory.CreateDirectory(nestedDir);

        string contractFile = Path.Combine(nestedDir, "contract.txt");
        File.WriteAllText(contractFile, "合同内容", Encoding.UTF8);
        string otherFile = Path.Combine(folderToImport, "other.txt");
        File.WriteAllBytes(otherFile, Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("abc", 1000))));

        // Pass both parent folder and a child file inside it -> child path must be deduplicated out
        var jobIds = await attachmentService.EnqueueAsync(new ImportRequest(
            Guid.NewGuid(),
            taskId,
            new[] { folderToImport, contractFile },
            ImportIntent.Copy,
            ImportOrigin.FilePicker), CancellationToken.None);

        Assert.Single(jobIds);
        var jobView = await attachmentService.GetJobAsync(jobIds[0], CancellationToken.None);
        Assert.Equal(JobStates.Done, jobView.State);

        var attachments = await attachmentService.ListForRecordAsync(taskId, includeTrashed: false, CancellationToken.None);
        Assert.Single(attachments);
        var att = attachments[0];
        Assert.Equal("directory", att.EntryKind);
        Assert.Equal("bundle", att.OriginalName);

        // Verify target files and empty folder exist in managed storage
        string targetRoot = fileStore.ResolveManagedAttachmentFullPath(fixture.StorageRootDir, att.RelativePath);
        Assert.True(Directory.Exists(Path.Combine(targetRoot, "empty")));
        Assert.Equal("合同内容", File.ReadAllText(Path.Combine(targetRoot, "nested", "contract.txt"), Encoding.UTF8));
        Assert.Equal(3000, new FileInfo(Path.Combine(targetRoot, "other.txt")).Length);

        // Source remains completely intact for Copy
        Assert.True(File.Exists(contractFile));
        Assert.True(File.Exists(otherFile));
    }

    [Fact]
    public async Task Same_named_files_get_distinct_attachment_ids_and_retry_is_idempotent()
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
            taskId, 0, RecordKind.Task, null, "重名测试", "",
            new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null),
            InheritField.None), CancellationToken.None);

        string dir1 = Path.Combine(fixture.SourceDir, "d1");
        string dir2 = Path.Combine(fixture.SourceDir, "d2");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);

        string f1 = Path.Combine(dir1, "same.txt");
        string f2 = Path.Combine(dir2, "same.txt");
        File.WriteAllText(f1, "第一份内容", Encoding.UTF8);
        File.WriteAllText(f2, "第二份内容", Encoding.UTF8);

        var jobs = await attachmentService.EnqueueAsync(new ImportRequest(
            Guid.NewGuid(), taskId, new[] { f1, f2 }, ImportIntent.Copy, ImportOrigin.FilePicker), CancellationToken.None);

        Assert.Equal(2, jobs.Count);

        // Retrying an already done job does not duplicate the attachment
        await attachmentService.RetryAsync(jobs[0], CancellationToken.None);

        var list = await attachmentService.ListForRecordAsync(taskId, includeTrashed: false, CancellationToken.None);
        Assert.Equal(2, list.Count);
        Assert.NotEqual(list[0].Id, list[1].Id);

        string p1 = fileStore.ResolveManagedAttachmentFullPath(fixture.StorageRootDir, list[0].RelativePath);
        string p2 = fileStore.ResolveManagedAttachmentFullPath(fixture.StorageRootDir, list[1].RelativePath);
        Assert.Equal("第一份内容", File.ReadAllText(p1, Encoding.UTF8));
        Assert.Equal("第二份内容", File.ReadAllText(p2, Encoding.UTF8));
    }

    [Fact]
    public async Task Rejects_insufficient_space_readonly_self_import_and_supports_pre_register_cancel()
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
            taskId, 0, RecordKind.Task, null, "边界测试", "",
            new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null),
            InheritField.None), CancellationToken.None);

        string srcFile = Path.Combine(fixture.SourceDir, "doc.txt");
        File.WriteAllText(srcFile, "测试文本", Encoding.UTF8);

        // 1. Insufficient space
        fileStore.SimulateInsufficientSpace = true;
        var spaceEx = await Assert.ThrowsAsync<AppException>(() =>
            attachmentService.EnqueueAsync(new ImportRequest(
                Guid.NewGuid(), taskId, new[] { srcFile }, ImportIntent.Copy, ImportOrigin.FilePicker), CancellationToken.None));
        Assert.Equal(ErrorCodes.InsufficientSpace, spaceEx.Code);
        fileStore.SimulateInsufficientSpace = false;

        // 2. Read-only target
        fileStore.SimulateTargetReadOnly = true;
        var roEx = await Assert.ThrowsAsync<AppException>(() =>
            attachmentService.EnqueueAsync(new ImportRequest(
                Guid.NewGuid(), taskId, new[] { srcFile }, ImportIntent.Copy, ImportOrigin.FilePicker), CancellationToken.None));
        Assert.Equal(ErrorCodes.PathUnavailable, roEx.Code);
        fileStore.SimulateTargetReadOnly = false;

        // 3. Self-import / already-managed path rejection
        var selfEx = await Assert.ThrowsAsync<AppException>(() =>
            attachmentService.EnqueueAsync(new ImportRequest(
                Guid.NewGuid(), taskId, new[] { fixture.StorageRootDir }, ImportIntent.Copy, ImportOrigin.FilePicker), CancellationToken.None));
        Assert.Equal(ErrorCodes.Validation, selfEx.Code);

        // 4. Cancel before registration cleans staging, keeps source intact, and creates 0 attachments
        attachmentService.AutoExecuteJobsOnEnqueue = false;
        var plannedJobs = await attachmentService.EnqueueAsync(new ImportRequest(
            Guid.NewGuid(), taskId, new[] { srcFile }, ImportIntent.Copy, ImportOrigin.FilePicker), CancellationToken.None);
        await attachmentService.CancelAsync(plannedJobs[0], CancellationToken.None);

        var cancelledView = await attachmentService.GetJobAsync(plannedJobs[0], CancellationToken.None);
        Assert.Equal(JobStates.Cancelled, cancelledView.State);
        Assert.True(File.Exists(srcFile));
        Assert.Empty(await attachmentService.ListForRecordAsync(taskId, includeTrashed: true, CancellationToken.None));
    }
}
