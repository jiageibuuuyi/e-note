using DesktopNotes.Application.Contracts;

namespace DesktopNotes.Application.Ports;

public sealed record SourceManifestResult(
    string EntryKind,
    string OriginalName,
    long TotalBytes,
    IReadOnlyList<AttachmentJobItemRecord> Items);

public interface IManagedFileStorePort
{
    string CurrentDeviceId { get; }
    ISourceCleanupPort SourceCleanup { get; }
    IReadOnlyList<string> NormalizeAndDeduplicateSources(IReadOnlyList<string> rawSourcePaths);
    void ValidateSourceAndRoot(string sourcePath, string rootPath, IReadOnlyList<string> allManagedRootPaths);
    SourceManifestResult BuildSourceManifest(Guid jobId, string sourcePath);
    Task<IReadOnlyDictionary<string, string?>> CopyAndVerifyToStagingAsync(
        string sourcePath,
        string rootPath,
        string stagingRelativePath,
        IReadOnlyList<AttachmentJobItemRecord> manifest,
        Action<long>? onBytesProgress,
        CancellationToken ct);
    bool VerifyExistingTreeMatchesManifest(
        string baseDirectoryPath,
        IReadOnlyList<AttachmentJobItemRecord> manifest,
        out Dictionary<string, string?> verifiedHashes);
    void PublishFromStaging(string rootPath, string stagingRelativePath, string targetRelativePath);
    void CleanupStaging(string rootPath, Guid jobId);
    string ResolveManagedAttachmentFullPath(string rootPath, string relativePath);
}
