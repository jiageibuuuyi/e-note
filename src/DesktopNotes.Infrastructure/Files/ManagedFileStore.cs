using System.Security.Cryptography;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Ports;

namespace DesktopNotes.Infrastructure.Files;

public sealed class ManagedFileStore : IManagedFileStorePort
{
    private const int BufferSize = 1024 * 1024; // 1 MiB streaming buffer

    public ISourceCleanupPort SourceCleanup { get; }
    public string CurrentDeviceId { get; set; }

    public bool SimulateInsufficientSpace { get; set; }
    public bool SimulateTargetReadOnly { get; set; }
    public Action<string>? OnDuringCopyHook { get; set; }

    public ManagedFileStore(ISourceCleanupPort sourceCleanup, string? deviceIdOverride = null)
    {
        SourceCleanup = sourceCleanup ?? throw new ArgumentNullException(nameof(sourceCleanup));
        CurrentDeviceId = deviceIdOverride ?? sourceCleanup.GetDeviceId(AppContext.BaseDirectory);
    }

    public IReadOnlyList<string> NormalizeAndDeduplicateSources(IReadOnlyList<string> rawSourcePaths)
    {
        if (rawSourcePaths is null || rawSourcePaths.Count == 0)
        {
            return Array.Empty<string>();
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        var fullPaths = rawSourcePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => TrimTrailingSeparator(Path.GetFullPath(p)))
            .Distinct(comparer)
            .OrderBy(p => p.Length)
            .ToList();

        var kept = new List<string>();
        foreach (string candidate in fullPaths)
        {
            bool containedInExistingParent = kept.Any(parent =>
                Directory.Exists(parent) && IsSubPathOf(candidate, parent, comparison));

            if (!containedInExistingParent)
            {
                kept.Add(candidate);
            }
        }

        return kept;
    }

    public void ValidateSourceAndRoot(string sourcePath, string rootPath, IReadOnlyList<string> allManagedRootPaths)
    {
        if (SimulateInsufficientSpace)
        {
            throw new AppException(ErrorCodes.InsufficientSpace, "Simulated insufficient disk space.");
        }

        if (SimulateTargetReadOnly)
        {
            throw new AppException(ErrorCodes.PathUnavailable, "Simulated read-only target storage root.");
        }

        string fullSource = TrimTrailingSeparator(Path.GetFullPath(sourcePath));
        string fullRoot = TrimTrailingSeparator(Path.GetFullPath(rootPath));

        bool isFile = File.Exists(fullSource);
        bool isDir = Directory.Exists(fullSource);
        if (!isFile && !isDir)
        {
            throw new AppException(ErrorCodes.PathUnavailable, $"Source path does not exist: {fullSource}");
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // Reject self-import, target inside source, or source inside any managed attachment root
        if (string.Equals(fullSource, fullRoot, comparison) ||
            IsSubPathOf(fullRoot, fullSource, comparison) ||
            IsSubPathOf(fullSource, fullRoot, comparison))
        {
            throw new AppException(ErrorCodes.Validation, "Source and managed storage root cannot overlap or contain each other.");
        }

        foreach (string managedRoot in allManagedRootPaths)
        {
            string normManaged = TrimTrailingSeparator(Path.GetFullPath(managedRoot));
            if (string.Equals(fullSource, normManaged, comparison) ||
                IsSubPathOf(fullSource, normManaged, comparison) ||
                IsSubPathOf(normManaged, fullSource, comparison))
            {
                throw new AppException(ErrorCodes.Validation, "Cannot import an already-managed attachment path.");
            }
        }

        // Check for reparse points / symbolic links on source and all its parent/child entries
        EnsureNotReparsePoint(fullSource);
        if (isDir)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(fullSource, "*", SearchOption.AllDirectories))
            {
                EnsureNotReparsePoint(entry);
            }
        }

        try
        {
            Directory.CreateDirectory(fullRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AppException(ErrorCodes.PathUnavailable, $"Target root is not writable: {fullRoot}", innerException: ex);
        }
    }

    public SourceManifestResult BuildSourceManifest(Guid jobId, string sourcePath)
    {
        string fullSource = TrimTrailingSeparator(Path.GetFullPath(sourcePath));
        EnsureNotReparsePoint(fullSource);

        if (File.Exists(fullSource))
        {
            var fi = new FileInfo(fullSource);
            string identity = SourceCleanup.GetFileIdentity(fullSource);
            long mtimeNs = fi.LastWriteTimeUtc.Ticks * 100;
            var item = new AttachmentJobItemRecord(
                jobId,
                ".",
                "file",
                identity,
                fi.Length,
                mtimeNs,
                Sha256: null,
                Verified: false,
                SourceRemoved: false);

            return new SourceManifestResult("file", fi.Name, fi.Length, new[] { item });
        }

        if (Directory.Exists(fullSource))
        {
            var rootDi = new DirectoryInfo(fullSource);
            var items = new List<AttachmentJobItemRecord>
            {
                new(
                    jobId,
                    ".",
                    "directory",
                    SourceCleanup.GetFileIdentity(fullSource),
                    0,
                    rootDi.LastWriteTimeUtc.Ticks * 100,
                    Sha256: null,
                    Verified: false,
                    SourceRemoved: false)
            };

            long totalBytes = 0;
            foreach (string dir in Directory.EnumerateDirectories(fullSource, "*", SearchOption.AllDirectories))
            {
                EnsureNotReparsePoint(dir);
                var di = new DirectoryInfo(dir);
                string rel = NormalizeRelativePath(Path.GetRelativePath(fullSource, dir));
                items.Add(new AttachmentJobItemRecord(
                    jobId,
                    rel,
                    "directory",
                    SourceCleanup.GetFileIdentity(dir),
                    0,
                    di.LastWriteTimeUtc.Ticks * 100,
                    Sha256: null,
                    Verified: false,
                    SourceRemoved: false));
            }

            foreach (string file in Directory.EnumerateFiles(fullSource, "*", SearchOption.AllDirectories))
            {
                EnsureNotReparsePoint(file);
                var fi = new FileInfo(file);
                string rel = NormalizeRelativePath(Path.GetRelativePath(fullSource, file));
                totalBytes += fi.Length;
                items.Add(new AttachmentJobItemRecord(
                    jobId,
                    rel,
                    "file",
                    SourceCleanup.GetFileIdentity(file),
                    fi.Length,
                    fi.LastWriteTimeUtc.Ticks * 100,
                    Sha256: null,
                    Verified: false,
                    SourceRemoved: false));
            }

            var sorted = items.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray();
            return new SourceManifestResult("directory", rootDi.Name, totalBytes, sorted);
        }

        throw new AppException(ErrorCodes.PathUnavailable, $"Source path not found: {fullSource}");
    }

    public async Task<IReadOnlyDictionary<string, string?>> CopyAndVerifyToStagingAsync(
        string sourcePath,
        string rootPath,
        string stagingRelativePath,
        IReadOnlyList<AttachmentJobItemRecord> manifest,
        Action<long>? onBytesProgress,
        CancellationToken ct)
    {
        if (SimulateInsufficientSpace)
        {
            throw new AppException(ErrorCodes.InsufficientSpace, "Simulated insufficient space during copy.");
        }

        if (SimulateTargetReadOnly)
        {
            throw new AppException(ErrorCodes.PathUnavailable, "Simulated target read-only during copy.");
        }

        string fullSource = TrimTrailingSeparator(Path.GetFullPath(sourcePath));
        string fullStagingPayload = ResolveManagedAttachmentFullPath(rootPath, stagingRelativePath);

        if (File.Exists(fullStagingPayload))
        {
            File.Delete(fullStagingPayload);
        }
        else if (Directory.Exists(fullStagingPayload))
        {
            Directory.Delete(fullStagingPayload, recursive: true);
        }

        string? stagingParent = Path.GetDirectoryName(fullStagingPayload);
        if (!string.IsNullOrWhiteSpace(stagingParent))
        {
            Directory.CreateDirectory(stagingParent);
        }

        var verifiedHashes = new Dictionary<string, string?>(StringComparer.Ordinal);
        long bytesCopied = 0;
        byte[] buffer = new byte[BufferSize];

        // 1. Create all directories first (preserving empty directories)
        foreach (var dirItem in manifest.Where(i => i.EntryKind == "directory"))
        {
            ct.ThrowIfCancellationRequested();
            string targetDir = dirItem.RelativePath == "."
                ? fullStagingPayload
                : Path.Combine(fullStagingPayload, ToPlatformRelativePath(dirItem.RelativePath));
            Directory.CreateDirectory(targetDir);
            verifiedHashes[dirItem.RelativePath] = null;
        }

        // 2. Stream copy each file with 1 MiB buffer
        foreach (var fileItem in manifest.Where(i => i.EntryKind == "file"))
        {
            ct.ThrowIfCancellationRequested();

            string srcFile = fileItem.RelativePath == "."
                ? fullSource
                : Path.Combine(fullSource, ToPlatformRelativePath(fileItem.RelativePath));
            string dstFile = fileItem.RelativePath == "."
                ? fullStagingPayload
                : Path.Combine(fullStagingPayload, ToPlatformRelativePath(fileItem.RelativePath));

            string? dstDir = Path.GetDirectoryName(dstFile);
            if (!string.IsNullOrWhiteSpace(dstDir))
            {
                Directory.CreateDirectory(dstDir);
            }

            if (!File.Exists(srcFile))
            {
                throw new AppException(ErrorCodes.SourceChanged, $"Source file disappeared during copy: {srcFile}");
            }

            string sourceHashDuringCopy;
            try
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var srcStream = new FileStream(srcFile, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var dstStream = new FileStream(dstFile, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
                {
                    int read;
                    while ((read = await srcStream.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
                    {
                        sha.AppendData(buffer, 0, read);
                        await dstStream.WriteAsync(buffer.AsMemory(0, read), ct);
                        bytesCopied += read;
                        onBytesProgress?.Invoke(bytesCopied);
                    }
                    await dstStream.FlushAsync(ct);
                }

                sourceHashDuringCopy = Convert.ToHexStringLower(sha.GetHashAndReset());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AppException(ErrorCodes.SourceChanged, $"Failed to read or copy source file: {srcFile}", innerException: ex);
            }

            OnDuringCopyHook?.Invoke(fullSource);

            // Re-check source identity, size, and mtime after copy
            var currentFi = new FileInfo(srcFile);
            string currentIdent = SourceCleanup.GetFileIdentity(srcFile);
            if (!string.Equals(currentIdent, fileItem.SourceIdentity, StringComparison.Ordinal) ||
                currentFi.Length != fileItem.SourceSize)
            {
                throw new AppException(ErrorCodes.SourceChanged, $"Source file changed during copy: {srcFile}");
            }

            // Secondary read of the target file to verify SHA-256 on disk
            await using var verifyStream = new FileStream(dstFile, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] targetHashBytes = await SHA256.HashDataAsync(verifyStream, ct);
            string targetHash = Convert.ToHexStringLower(targetHashBytes);

            if (!string.Equals(sourceHashDuringCopy, targetHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new AppException(ErrorCodes.SourceChanged, $"Staging verification hash mismatch for {dstFile}");
            }

            verifiedHashes[fileItem.RelativePath] = targetHash;
        }

        return verifiedHashes;
    }

    public bool VerifyExistingTreeMatchesManifest(
        string baseDirectoryPath,
        IReadOnlyList<AttachmentJobItemRecord> manifest,
        out Dictionary<string, string?> verifiedHashes)
    {
        verifiedHashes = new Dictionary<string, string?>(StringComparer.Ordinal);
        string fullBase = TrimTrailingSeparator(Path.GetFullPath(baseDirectoryPath));

        if (!File.Exists(fullBase) && !Directory.Exists(fullBase))
        {
            return false;
        }

        foreach (var item in manifest)
        {
            string targetPath = item.RelativePath == "."
                ? fullBase
                : Path.Combine(fullBase, ToPlatformRelativePath(item.RelativePath));

            if (item.EntryKind == "directory")
            {
                if (!Directory.Exists(targetPath))
                {
                    return false;
                }
                verifiedHashes[item.RelativePath] = null;
            }
            else
            {
                if (!File.Exists(targetPath))
                {
                    return false;
                }

                var fi = new FileInfo(targetPath);
                if (fi.Length != item.SourceSize)
                {
                    return false;
                }

                using var fs = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
                string actualSha = Convert.ToHexStringLower(SHA256.HashData(fs));
                if (!string.IsNullOrEmpty(item.Sha256) &&
                    !string.Equals(actualSha, item.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                verifiedHashes[item.RelativePath] = actualSha;
            }
        }

        return true;
    }

    public void PublishFromStaging(string rootPath, string stagingRelativePath, string targetRelativePath)
    {
        string stagingFull = ResolveManagedAttachmentFullPath(rootPath, stagingRelativePath);
        string targetFull = ResolveManagedAttachmentFullPath(rootPath, targetRelativePath);

        string? targetParent = Path.GetDirectoryName(targetFull);
        if (!string.IsNullOrWhiteSpace(targetParent))
        {
            Directory.CreateDirectory(targetParent);
        }

        if (File.Exists(stagingFull))
        {
            if (File.Exists(targetFull))
            {
                File.Delete(targetFull);
            }
            File.Move(stagingFull, targetFull);
            return;
        }

        if (Directory.Exists(stagingFull))
        {
            if (Directory.Exists(targetFull))
            {
                Directory.Delete(targetFull, recursive: true);
            }
            Directory.Move(stagingFull, targetFull);
            return;
        }

        throw new AppException(ErrorCodes.PathUnavailable, $"Staging payload missing before publish: {stagingFull}");
    }

    public void CleanupStaging(string rootPath, Guid jobId)
    {
        string jobStagingDir = Path.Combine(Path.GetFullPath(rootPath), ".desktopnotes-staging", jobId.ToString("D"));
        if (Directory.Exists(jobStagingDir))
        {
            try
            {
                Directory.Delete(jobStagingDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of job's own staging folder
            }
        }
    }

    public string ResolveManagedAttachmentFullPath(string rootPath, string relativePath)
    {
        string fullRoot = TrimTrailingSeparator(Path.GetFullPath(rootPath));
        string platformRel = ToPlatformRelativePath(relativePath);
        string combined = Path.GetFullPath(Path.Combine(fullRoot, platformRel));

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!IsSubPathOf(combined, fullRoot, comparison))
        {
            throw new AppException(ErrorCodes.Validation, $"Relative path escapes storage root: {relativePath}");
        }

        return combined;
    }

    private static void EnsureNotReparsePoint(string path)
    {
        var attr = File.GetAttributes(path);
        if ((attr & FileAttributes.ReparsePoint) != 0)
        {
            throw new AppException(ErrorCodes.Validation, $"Symbolic link, junction, or reparse point is not allowed: {path}");
        }

        FileSystemInfo fsi = (attr & FileAttributes.Directory) != 0
            ? new DirectoryInfo(path)
            : new FileInfo(path);

        if (fsi.LinkTarget is not null)
        {
            throw new AppException(ErrorCodes.Validation, $"Symbolic link target detected: {path}");
        }
    }

    public static bool IsSubPathOf(string candidatePath, string parentDirPath, StringComparison comparison)
    {
        string normCandidate = TrimTrailingSeparator(Path.GetFullPath(candidatePath));
        string normParent = TrimTrailingSeparator(Path.GetFullPath(parentDirPath)) + Path.DirectorySeparatorChar;
        return normCandidate.StartsWith(normParent, comparison);
    }

    private static string TrimTrailingSeparator(string path)
    {
        string root = Path.GetPathRoot(path) ?? string.Empty;
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static string NormalizeRelativePath(string relativePath)
    {
        return relativePath.Replace('\\', '/').Trim('/');
    }

    public static string ToPlatformRelativePath(string normalizedRelativePath)
    {
        return normalizedRelativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
    }
}
