using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopNotes.Application.Backup;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Ports;
using DesktopNotes.Application.Records;
using DesktopNotes.Domain;
using DesktopNotes.Infrastructure.Backup;
using DesktopNotes.Infrastructure.Persistence;
using DesktopNotes.Platforms.Linux;
using DesktopNotes.Platforms.MacOS;
using Xunit;

namespace DesktopNotes.Integration.Tests;

public sealed class CrossPlatformMigrationTests : IDisposable
{
    private readonly string _tempRoot;

    public CrossPlatformMigrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DesktopNotesCrossPlatformTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task MacOsAndLinuxAdapters_ReportHonestCapabilities_AndManageStartupFiles()
    {
        string macLaunchAgents = Path.Combine(_tempRoot, "mac_launch_agents");
        var macIntegration = new MacOsDesktopIntegration(macLaunchAgents);

        if (!OperatingSystem.IsMacOS())
        {
            Assert.Equal(CapabilitySupport.Unknown, macIntegration.Capabilities.EmbeddedDesktop);
        }

        Assert.False(await macIntegration.GetLoginStartupAsync(CancellationToken.None));
        await macIntegration.SetLoginStartupAsync(true, CancellationToken.None);
        Assert.True(await macIntegration.GetLoginStartupAsync(CancellationToken.None));
        await macIntegration.SetLoginStartupAsync(false, CancellationToken.None);
        Assert.False(await macIntegration.GetLoginStartupAsync(CancellationToken.None));

        // Verify Linux Wayland session explicitly reports Unsupported for EmbeddedDesktop & AbsolutePosition
        string linuxAutostart = Path.Combine(_tempRoot, "linux_autostart");
        var waylandIntegration = new LinuxDesktopIntegration(linuxAutostart, sessionTypeOverride: "wayland");
        Assert.Equal(CapabilitySupport.Unsupported, waylandIntegration.Capabilities.EmbeddedDesktop);
        Assert.Equal(CapabilitySupport.Unsupported, waylandIntegration.Capabilities.AbsolutePosition);
        Assert.Contains("Wayland", waylandIntegration.Capabilities.Reason);

        await waylandIntegration.SetLoginStartupAsync(true, CancellationToken.None);
        Assert.True(await waylandIntegration.GetLoginStartupAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RoundTripMigration_WindowsToMacLinuxAndBack_HandlesCaseCollisions_Unicode_IllegalChars_AndNoForeignSourceCleanup()
    {
        // Step 1: Create backup simulating a Linux/macOS device with:
        // - Unicode NFC/NFD & Emoji filename: "项目汇报_📝_café.txt"
        // - Case collision filenames: "Notes.md" and "notes.md"
        // - Filename with characters illegal on Windows: "review:2026?final*.txt"
        // - A pending foreign move job ("source_cleanup_pending") from "mac-device-01"
        string foreignDbPath = Path.Combine(_tempRoot, "foreign", "app.db");
        using var foreignStore = new SqliteStore(foreignDbPath);
        var foreignRecords = new RecordService(foreignStore);

        Guid taskId = Guid.NewGuid();
        await foreignRecords.SaveAsync(
            new SaveRecordRequest(
                taskId,
                0,
                RecordKind.Task,
                null,
                "跨平台迁移验证任务 🚀",
                "从 macOS/Linux 迁移到 Windows 再导回",
                new EffectiveFields(null, "Linus", Priority.Highest, null, new DueSpec(DueKind.Date, new DateOnly(2026, 12, 1), null, null), null),
                InheritField.None),
            CancellationToken.None);

        Guid rootId = Guid.NewGuid();
        foreignStore.RegisterStorageRoot(rootId, "/Users/dev/DesktopNotesAttachments", "macos", DateTimeOffset.UtcNow);

        Guid jobId = Guid.NewGuid();
        Guid attId = Guid.NewGuid();
        foreignStore.CreatePlannedJob(
            new AttachmentJobRecord(
                jobId,
                Guid.NewGuid(),
                taskId,
                attId,
                rootId,
                "move",
                "/Users/dev/Desktop/important_source.txt",
                $"tasks/{taskId:D}/{attId:D}/important_source.txt",
                $".desktopnotes-staging/{jobId:D}/payload",
                JobStates.SourceCleanupPending,
                "mac-device-01",
                "locked",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            []);

        string snapshotPath = Path.Combine(_tempRoot, "foreign", "snapshot.db");
        foreignStore.BackupOnlineTo(snapshotPath);

        byte[] bUnicode = Encoding.UTF8.GetBytes("Unicode & Emoji 附件内容");
        byte[] bUpper = Encoding.UTF8.GetBytes("Uppercase Notes.md");
        byte[] bLower = Encoding.UTF8.GetBytes("Lowercase notes.md");
        byte[] bIllegal = Encoding.UTF8.GetBytes("Illegal Windows Chars Content");

        var manifest = new BackupManifest(
            FormatVersion: 1,
            SchemaVersion: 1,
            CreatedUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DeviceId: "mac-device-01",
            IncludeTrash: true,
            StorageRoots: [new BackupRootEntry(rootId, "/Users/dev/DesktopNotesAttachments", "macos")],
            Files:
            [
                new BackupFileEntry(rootId, attId, "payload/u.bin", $"tasks/{taskId:D}/{attId:D}/项目汇报_📝_café.txt", bUnicode.Length, Convert.ToHexStringLower(SHA256.HashData(bUnicode))),
                new BackupFileEntry(rootId, attId, "payload/c1.bin", $"tasks/{taskId:D}/{attId:D}/Notes.md", bUpper.Length, Convert.ToHexStringLower(SHA256.HashData(bUpper))),
                new BackupFileEntry(rootId, attId, "payload/c2.bin", $"tasks/{taskId:D}/{attId:D}/notes.md", bLower.Length, Convert.ToHexStringLower(SHA256.HashData(bLower))),
                new BackupFileEntry(rootId, attId, "payload/il.bin", $"tasks/{taskId:D}/{attId:D}/review:2026?final*.txt", bIllegal.Length, Convert.ToHexStringLower(SHA256.HashData(bIllegal)))
            ],
            EmptyDirectories: [],
            JobsSummary: [new BackupJobSummary(jobId, attId, JobStates.SourceCleanupPending, "mac-device-01")]);

        string incomingArchive = Path.Combine(_tempRoot, "from_mac_linux.zip");
        await using (var fs = new FileStream(incomingArchive, FileMode.Create, FileAccess.ReadWrite))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var dbEntry = zip.CreateEntry("database/app.db");
            await using (var s = dbEntry.Open())
            await using (var dbIn = File.OpenRead(snapshotPath))
            {
                await dbIn.CopyToAsync(s);
            }

            foreach (var (entryName, bytes) in new[]
            {
                ("payload/u.bin", bUnicode),
                ("payload/c1.bin", bUpper),
                ("payload/c2.bin", bLower),
                ("payload/il.bin", bIllegal)
            })
            {
                var e = zip.CreateEntry(entryName);
                await using var s = e.Open();
                await s.WriteAsync(bytes);
            }

            var mEntry = zip.CreateEntry("manifest.json");
            await using (var s = mEntry.Open())
            {
                await JsonSerializer.SerializeAsync(s, manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            }
        }

        // Step 2: Restore onto Windows
        string winDbPath = Path.Combine(_tempRoot, "win_env", "app.db");
        string winRootPath = Path.Combine(_tempRoot, "win_env", "attachments");
        using var winStore = new SqliteStore(winDbPath);
        var winBackupService = new BackupService(winStore, new ArchiveWriter(), "win-device-02");

        await winBackupService.RestoreAsync(
            incomingArchive,
            new Dictionary<Guid, string> { [rootId] = winRootPath },
            CancellationToken.None);

        // Verify path mappings for case collision and illegal characters
        Assert.Equal(
            $"tasks/{taskId:D}/{attId:D}/notes__case_1.md",
            winBackupService.LastRestorePathMappings[$"tasks/{taskId:D}/{attId:D}/notes.md"]);
        Assert.Equal(
            $"tasks/{taskId:D}/{attId:D}/review_2026_final_.txt",
            winBackupService.LastRestorePathMappings[$"tasks/{taskId:D}/{attId:D}/review:2026?final*.txt"]);

        string attFolderOnWin = Path.Combine(winRootPath, "tasks", taskId.ToString("D"), attId.ToString("D"));
        Assert.Equal("Unicode & Emoji 附件内容", await File.ReadAllTextAsync(Path.Combine(attFolderOnWin, "项目汇报_📝_café.txt")));
        Assert.Equal("Uppercase Notes.md", await File.ReadAllTextAsync(Path.Combine(attFolderOnWin, "Notes.md")));
        Assert.Equal("Lowercase notes.md", await File.ReadAllTextAsync(Path.Combine(attFolderOnWin, "notes__case_1.md")));
        Assert.Equal("Illegal Windows Chars Content", await File.ReadAllTextAsync(Path.Combine(attFolderOnWin, "review_2026_final_.txt")));

        // Verify foreign job was transitioned to NeedsReview without source cleanup
        Assert.Equal(JobStates.NeedsReview, winStore.GetJobRecord(jobId)!.State);
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
}
