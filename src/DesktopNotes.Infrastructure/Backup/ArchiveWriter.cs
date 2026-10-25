using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Ports;

namespace DesktopNotes.Infrastructure.Backup;

public sealed class ArchiveWriter : IArchivePort
{
    private const int BufferSize = 1024 * 1024; // 1 MiB streaming buffer

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task WriteArchiveAsync(
        string archivePath,
        string snapshotDbPath,
        BackupManifest manifest,
        IReadOnlyList<(string SourceFilePath, string ArchiveEntryPath, string ExpectedSha256, long ExpectedSize)> filesToPack,
        CancellationToken ct)
    {
        string fullArchivePath = Path.GetFullPath(archivePath);
        string? parentDir = Path.GetDirectoryName(fullArchivePath);
        if (!string.IsNullOrWhiteSpace(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        string tempArchivePath = fullArchivePath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var archiveStream = new FileStream(
                tempArchivePath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                BufferSize,
                useAsync: true))
            using (var zip = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                // 1. Write database snapshot
                var dbEntry = zip.CreateEntry("database/app.db", CompressionLevel.Optimal);
                await using (var dbEntryStream = dbEntry.Open())
                await using (var dbFileStream = new FileStream(snapshotDbPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true))
                {
                    await dbFileStream.CopyToAsync(dbEntryStream, BufferSize, ct);
                }

                // 2. Stream and re-verify each attachment file during packaging
                var buffer = new byte[BufferSize];
                foreach (var item in filesToPack)
                {
                    ct.ThrowIfCancellationRequested();
                    ValidateSafeRelativePath(item.ArchiveEntryPath);

                    if (!File.Exists(item.SourceFilePath))
                    {
                        throw new AppException("backup_source_missing", $"备份过程中附件文件丢失: {item.SourceFilePath}");
                    }

                    var entry = zip.CreateEntry(NormalizeArchiveEntryPath(item.ArchiveEntryPath), CompressionLevel.Optimal);
                    long totalRead = 0;
                    using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                    await using (var entryStream = entry.Open())
                    await using (var sourceStream = new FileStream(item.SourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true))
                    {
                        int bytesRead;
                        while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                        {
                            sha256.AppendData(buffer, 0, bytesRead);
                            await entryStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                            totalRead += bytesRead;
                        }
                    }

                    string actualHash = Convert.ToHexStringLower(sha256.GetHashAndReset());
                    if (totalRead != item.ExpectedSize || !string.Equals(actualHash, item.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new AppException(
                            "backup_source_changed",
                            $"备份期间附件发生变动，拒绝生成不完整备份: {item.SourceFilePath}");
                    }
                }

                // 3. Write manifest.json last
                var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using (var manifestStream = manifestEntry.Open())
                {
                    await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonOptions, ct);
                }
            }

            // Atomically publish completed archive
            File.Move(tempArchivePath, fullArchivePath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(tempArchivePath);
            throw;
        }
    }

    public async Task<BackupManifest> ExtractAndValidateArchiveAsync(
        string archivePath,
        string stagingDirectory,
        CancellationToken ct)
    {
        if (!File.Exists(archivePath))
        {
            throw new AppException("backup_not_found", $"备份文件不存在: {archivePath}");
        }

        Directory.CreateDirectory(stagingDirectory);
        string fullStagingRoot = Path.GetFullPath(stagingDirectory);
        if (!fullStagingRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            fullStagingRoot += Path.DirectorySeparatorChar;
        }

        await using var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);

        // Step 1: Validate every zip entry path before extracting anything
        foreach (var entry in zip.Entries)
        {
            ValidateSafeRelativePath(entry.FullName);
        }

        // Step 2: Read and validate manifest.json
        var manifestEntry = zip.GetEntry("manifest.json")
            ?? throw new AppException("manifest_missing", "备份包缺少 manifest.json。");

        BackupManifest manifest;
        await using (var manifestStream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(manifestStream, JsonOptions, ct)
                ?? throw new AppException("manifest_invalid", "manifest.json 格式无效。");
        }

        if (manifest.FormatVersion != 1)
        {
            throw new AppException("unsupported_format_version", $"不支持的备份包格式版本: {manifest.FormatVersion}");
        }

        if (manifest.SchemaVersion > 1)
        {
            throw new AppException("unsupported_schema_version", $"不支持的数据结构版本: {manifest.SchemaVersion}");
        }

        foreach (var file in manifest.Files)
        {
            ValidateSafeRelativePath(file.ArchiveRelativePath);
            ValidateSafeRelativePath(file.TargetRelativePath);
        }

        foreach (var emptyDir in manifest.EmptyDirectories)
        {
            ValidateSafeRelativePath(emptyDir.TargetRelativePath);
        }

        // Step 3: Extract database/app.db
        var dbEntry = zip.GetEntry("database/app.db")
            ?? throw new AppException("backup_db_missing", "备份包缺少 database/app.db。");

        string stagedDbPath = Path.GetFullPath(Path.Combine(stagingDirectory, "database", "app.db"));
        EnsureInsideStagingRoot(stagedDbPath, fullStagingRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(stagedDbPath)!);

        await using (var dbIn = dbEntry.Open())
        await using (var dbOut = new FileStream(stagedDbPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            await dbIn.CopyToAsync(dbOut, BufferSize, ct);
        }

        // Step 4: Extract and verify each payload file against manifest SHA-256 and size
        var buffer = new byte[BufferSize];
        foreach (var fileItem in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            string normalizedEntryName = NormalizeArchiveEntryPath(fileItem.ArchiveRelativePath);
            var zipEntry = zip.GetEntry(normalizedEntryName)
                ?? throw new AppException("backup_file_missing", $"备份包中缺少清单所列文件: {normalizedEntryName}");

            string stagedFilePath = Path.GetFullPath(Path.Combine(stagingDirectory, fileItem.ArchiveRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            EnsureInsideStagingRoot(stagedFilePath, fullStagingRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(stagedFilePath)!);

            long totalWritten = 0;
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            await using (var entryIn = zipEntry.Open())
            await using (var fileOut = new FileStream(stagedFilePath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                int read;
                while ((read = await entryIn.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    sha256.AppendData(buffer, 0, read);
                    await fileOut.WriteAsync(buffer.AsMemory(0, read), ct);
                    totalWritten += read;
                }
            }

            string actualHash = Convert.ToHexStringLower(sha256.GetHashAndReset());
            if (totalWritten != fileItem.SizeBytes ||
                !string.Equals(actualHash, fileItem.Sha256Hex, StringComparison.OrdinalIgnoreCase))
            {
                throw new AppException(
                    "backup_hash_mismatch",
                    $"备份文件校验失败 ({fileItem.TargetRelativePath}): 期望 {fileItem.Sha256Hex} ({fileItem.SizeBytes}B)，实际 {actualHash} ({totalWritten}B)");
            }
        }

        return manifest;
    }

    public static void ValidateSafeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new AppException("invalid_archive_path", "备份路径不能为空。");
        }

        if (relativePath.Contains('\0'))
        {
            throw new AppException("invalid_archive_path", "备份路径包含非法空字符。");
        }

        if (relativePath.StartsWith('/') || relativePath.StartsWith('\\') || Path.IsPathRooted(relativePath))
        {
            throw new AppException("invalid_archive_path", $"拒绝绝对路径: {relativePath}");
        }

        if (relativePath.Contains(':'))
        {
            throw new AppException("invalid_archive_path", $"拒绝带盘符或协议的路径: {relativePath}");
        }

        string[] segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new AppException("invalid_archive_path", $"无效的相对路径: {relativePath}");
        }

        foreach (string seg in segments)
        {
            if (seg is "." or "..")
            {
                throw new AppException("invalid_archive_path", $"拒绝路径穿越片段: {relativePath}");
            }
        }
    }

    private static void EnsureInsideStagingRoot(string candidateFullPath, string stagingRootWithSeparator)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidateFullPath.StartsWith(stagingRootWithSeparator, comparison))
        {
            throw new AppException("invalid_archive_path", $"检测到越界解压路径: {candidateFullPath}");
        }
    }

    private static string NormalizeArchiveEntryPath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
