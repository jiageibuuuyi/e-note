using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopNotes.Application.Backup;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Ports;
using DesktopNotes.Infrastructure.Backup;
using DesktopNotes.Infrastructure.Persistence;
using Xunit;

namespace DesktopNotes.Integration.Tests;

public sealed class RestorePathTests : IDisposable
{
    private readonly string _tempRoot;

    public RestorePathTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DesktopNotesRestorePathTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("tasks/../../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    public async Task Restore_RejectsPathTraversalAndAbsolutePaths_InZipEntriesAndManifest(string maliciousPath)
    {
        string archivePath = Path.Combine(_tempRoot, "malicious_" + Guid.NewGuid().ToString("N") + ".zip");
        Guid rootId = Guid.NewGuid();

        await using (var fs = new FileStream(archivePath, FileMode.Create, FileAccess.ReadWrite))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry(maliciousPath);
            await using (var s = entry.Open())
            {
                await s.WriteAsync(Encoding.UTF8.GetBytes("evil"));
            }
        }

        using var targetStore = new SqliteStore(Path.Combine(_tempRoot, "target.db"));
        var backupService = new BackupService(targetStore, new ArchiveWriter());

        var ex = await Assert.ThrowsAsync<AppException>(() => backupService.RestoreAsync(
            archivePath,
            new Dictionary<Guid, string> { [rootId] = Path.Combine(_tempRoot, "root") },
            CancellationToken.None));

        Assert.Equal("invalid_archive_path", ex.Code);
    }

    [Fact]
    public async Task Restore_HandlesCaseInsensitiveFilenameCollisions_WithExplicitMappingOrRejection_WithoutOverwriting()
    {
        // Build a valid backup archive containing two files from a case-sensitive OS:
        // "tasks/r1/a1/Report.txt" ("Upper Content") and "tasks/r1/a1/report.txt" ("Lower Content")
        string sourceDbPath = Path.Combine(_tempRoot, "case_source", "app.db");
        using var sourceStore = new SqliteStore(sourceDbPath);
        Guid rootId = Guid.NewGuid();
        sourceStore.RegisterStorageRoot(rootId, "/home/user/attachments", "linux", DateTimeOffset.UtcNow);

        string snapshotPath = Path.Combine(_tempRoot, "case_source", "snapshot.db");
        sourceStore.BackupOnlineTo(snapshotPath);

        byte[] upperBytes = Encoding.UTF8.GetBytes("Upper Content");
        byte[] lowerBytes = Encoding.UTF8.GetBytes("Lower Content");
        string upperHash = Convert.ToHexStringLower(SHA256.HashData(upperBytes));
        string lowerHash = Convert.ToHexStringLower(SHA256.HashData(lowerBytes));

        Guid attId = Guid.NewGuid();
        var manifest = new BackupManifest(
            FormatVersion: 1,
            SchemaVersion: 1,
            CreatedUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DeviceId: "linux-dev",
            IncludeTrash: true,
            StorageRoots: [new BackupRootEntry(rootId, "/home/user/attachments", "linux")],
            Files:
            [
                new BackupFileEntry(rootId, attId, "payload/f1.bin", $"tasks/{attId:D}/Report.txt", upperBytes.Length, upperHash),
                new BackupFileEntry(rootId, attId, "payload/f2.bin", $"tasks/{attId:D}/report.txt", lowerBytes.Length, lowerHash)
            ],
            EmptyDirectories: [],
            JobsSummary: []);

        string archivePath = Path.Combine(_tempRoot, "case_collision.zip");
        await using (var fs = new FileStream(archivePath, FileMode.Create, FileAccess.ReadWrite))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var dbEntry = zip.CreateEntry("database/app.db");
            await using (var s = dbEntry.Open())
            await using (var dbIn = File.OpenRead(snapshotPath))
            {
                await dbIn.CopyToAsync(s);
            }

            var f1 = zip.CreateEntry("payload/f1.bin");
            await using (var s = f1.Open())
            {
                await s.WriteAsync(upperBytes);
            }

            var f2 = zip.CreateEntry("payload/f2.bin");
            await using (var s = f2.Open())
            {
                await s.WriteAsync(lowerBytes);
            }

            var mEntry = zip.CreateEntry("manifest.json");
            await using (var s = mEntry.Open())
            {
                await JsonSerializer.SerializeAsync(s, manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            }
        }

        // Mode 1: RejectOnCaseCollision = true -> throws case_collision
        using var targetStore1 = new SqliteStore(Path.Combine(_tempRoot, "target1", "app.db"));
        var rejectService = new BackupService(targetStore1, new ArchiveWriter())
        {
            RejectOnCaseCollision = true
        };
        var rejectEx = await Assert.ThrowsAsync<AppException>(() => rejectService.RestoreAsync(
            archivePath,
            new Dictionary<Guid, string> { [rootId] = Path.Combine(_tempRoot, "target1_root") },
            CancellationToken.None));
        Assert.Equal("case_collision", rejectEx.Code);

        // Mode 2: Default (RejectOnCaseCollision = false) -> generates explicit disambiguated mapping without overwriting either file
        string targetRoot2 = Path.Combine(_tempRoot, "target2_root");
        using var targetStore2 = new SqliteStore(Path.Combine(_tempRoot, "target2", "app.db"));
        var mapService = new BackupService(targetStore2, new ArchiveWriter());
        await mapService.RestoreAsync(
            archivePath,
            new Dictionary<Guid, string> { [rootId] = targetRoot2 },
            CancellationToken.None);

        Assert.Single(mapService.LastRestorePathMappings);
        string mappedRel = mapService.LastRestorePathMappings[$"tasks/{attId:D}/report.txt"];
        Assert.Equal($"tasks/{attId:D}/report__case_1.txt", mappedRel);

        string file1OnDisk = Path.Combine(targetRoot2, "tasks", attId.ToString("D"), "Report.txt");
        string file2OnDisk = Path.Combine(targetRoot2, "tasks", attId.ToString("D"), "report__case_1.txt");
        Assert.Equal("Upper Content", await File.ReadAllTextAsync(file1OnDisk));
        Assert.Equal("Lower Content", await File.ReadAllTextAsync(file2OnDisk));
    }

    [Fact]
    public async Task Export_RejectsArchivePathInsideManagedAttachmentRoot()
    {
        string dbPath = Path.Combine(_tempRoot, "self_rec", "app.db");
        string managedRoot = Path.Combine(_tempRoot, "self_rec", "attachments");
        Directory.CreateDirectory(managedRoot);

        using var store = new SqliteStore(dbPath);
        store.EnsureDefaultStorageRoot(managedRoot, "windows", DateTimeOffset.UtcNow);

        var backupService = new BackupService(store, new ArchiveWriter());
        string invalidArchivePath = Path.Combine(managedRoot, "sub", "backup.zip");

        var ex = await Assert.ThrowsAsync<AppException>(() => backupService.ExportAsync(
            invalidArchivePath,
            includeTrash: true,
            CancellationToken.None));

        Assert.Equal("backup_inside_managed_root", ex.Code);
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
