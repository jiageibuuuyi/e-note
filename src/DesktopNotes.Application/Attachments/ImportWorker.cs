using System.Collections.Concurrent;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Ports;

namespace DesktopNotes.Application.Attachments;

public sealed class SimulatedCrashException : Exception
{
    public string Stage { get; }
    public SimulatedCrashException(string stage) : base($"Simulated process crash after stage '{stage}'.")
    {
        Stage = stage;
    }
}

public sealed class ImportWorker
{
    private readonly IAttachmentRepositoryPort _repository;
    private readonly IManagedFileStorePort _fileStore;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<Guid, long> _bytesCopiedMap = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _jobCancellationMap = new();

    public string? StopAfterStage { get; set; }
    public bool CrashAfterPublishBeforeDbUpdate { get; set; }
    public Action<Guid, int>? OnBeforeCleanItemIndex { get; set; }

    public ImportWorker(
        IAttachmentRepositoryPort repository,
        IManagedFileStorePort fileStore,
        TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public long GetBytesCopied(Guid jobId) =>
        _bytesCopiedMap.TryGetValue(jobId, out long val) ? val : 0L;

    public void RequestCancel(Guid jobId)
    {
        if (_jobCancellationMap.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
        }
    }

    public async Task ExecuteJobAsync(Guid jobId, CancellationToken externalCt)
    {
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _jobCancellationMap[jobId] = jobCts;
        var ct = jobCts.Token;

        try
        {
            var job = _repository.GetJobRecord(jobId)
                ?? throw new AppException(ErrorCodes.NotFound, "Attachment job not found.", new[] { jobId });

            if (job.State is JobStates.Done or JobStates.Cancelled or JobStates.NeedsReview)
            {
                return;
            }

            var root = _repository.GetStorageRoot(job.RootId)
                ?? throw new AppException(ErrorCodes.PathUnavailable, "Storage root not found.", new[] { job.RootId });

            var items = _repository.GetJobItems(jobId);

            // Stage 1: Planned -> Copying -> Verified
            if (job.State is JobStates.Planned or JobStates.Copying or JobStates.Failed)
            {
                try
                {
                    _repository.UpdateJobState(jobId, JobStates.Copying, null, _timeProvider.GetUtcNow());
                    var verifiedHashes = await _fileStore.CopyAndVerifyToStagingAsync(
                        job.SourcePath,
                        root.AbsolutePath,
                        job.StagingRelativePath,
                        items,
                        copied => _bytesCopiedMap[jobId] = copied,
                        ct);

                    _repository.MarkJobItemsVerified(jobId, verifiedHashes, JobStates.Verified, _timeProvider.GetUtcNow());
                    job = _repository.GetJobRecord(jobId)!;
                    items = _repository.GetJobItems(jobId);

                    if (StopAfterStage == JobStates.Verified)
                    {
                        throw new SimulatedCrashException(JobStates.Verified);
                    }
                }
                catch (OperationCanceledException)
                {
                    _fileStore.CleanupStaging(root.AbsolutePath, jobId);
                    _repository.UpdateJobState(jobId, JobStates.Cancelled, null, _timeProvider.GetUtcNow());
                    return;
                }
                catch (SimulatedCrashException)
                {
                    throw;
                }
                catch (AppException ex)
                {
                    _fileStore.CleanupStaging(root.AbsolutePath, jobId);
                    _repository.UpdateJobState(jobId, JobStates.Failed, ex.Code, _timeProvider.GetUtcNow());
                    throw;
                }
            }

            // Stage 2: Verified -> Published
            if (job.State == JobStates.Verified)
            {
                if (ct.IsCancellationRequested)
                {
                    _fileStore.CleanupStaging(root.AbsolutePath, jobId);
                    _repository.UpdateJobState(jobId, JobStates.Cancelled, null, _timeProvider.GetUtcNow());
                    return;
                }

                _fileStore.PublishFromStaging(root.AbsolutePath, job.StagingRelativePath, job.TargetRelativePath);
                _fileStore.CleanupStaging(root.AbsolutePath, jobId);

                if (CrashAfterPublishBeforeDbUpdate)
                {
                    throw new SimulatedCrashException("published_before_db");
                }

                _repository.UpdateJobState(jobId, JobStates.Published, null, _timeProvider.GetUtcNow());
                job = _repository.GetJobRecord(jobId)!;

                if (StopAfterStage == JobStates.Published)
                {
                    throw new SimulatedCrashException(JobStates.Published);
                }
            }

            // Stage 3: Published -> Registered
            if (job.State == JobStates.Published)
            {
                var rootItem = items.First(i => i.RelativePath == ".");
                long totalBytes = items.Where(i => i.EntryKind == "file").Sum(i => i.SourceSize);
                string originalName = Path.GetFileName(job.TargetRelativePath.Replace('/', Path.DirectorySeparatorChar));

                var attachment = new AttachmentView(
                    job.AttachmentId,
                    job.RecordId,
                    job.RootId,
                    job.TargetRelativePath,
                    originalName,
                    rootItem.EntryKind,
                    "ready",
                    totalBytes,
                    _timeProvider.GetUtcNow(),
                    null);

                string nextState = string.Equals(job.Operation, "move", StringComparison.OrdinalIgnoreCase)
                    ? JobStates.Registered
                    : JobStates.Done;

                _repository.RegisterPublishedAttachment(jobId, attachment, nextState, _timeProvider.GetUtcNow());
                job = _repository.GetJobRecord(jobId)!;
                _bytesCopiedMap[jobId] = totalBytes;

                if (StopAfterStage == JobStates.Registered)
                {
                    throw new SimulatedCrashException(JobStates.Registered);
                }
            }

            // Stage 4: Registered / Cleaning -> Source Cleanup (Move only)
            if (job.State is JobStates.Registered or JobStates.Cleaning or JobStates.SourceCleanupPending)
            {
                if (!string.Equals(job.Operation, "move", StringComparison.OrdinalIgnoreCase))
                {
                    _repository.UpdateJobState(jobId, JobStates.Done, null, _timeProvider.GetUtcNow());
                    return;
                }

                await ExecuteSourceCleanupAsync(job, items, ct);
            }
        }
        finally
        {
            _jobCancellationMap.TryRemove(jobId, out _);
        }
    }

    public Task ExecuteSourceCleanupAsync(
        AttachmentJobRecord job,
        IReadOnlyList<AttachmentJobItemRecord> items,
        CancellationToken ct)
    {
        if (!string.Equals(job.SourceDeviceId, _fileStore.CurrentDeviceId, StringComparison.Ordinal))
        {
            // Never replay source deletion on a different device!
            _repository.UpdateJobState(job.Id, JobStates.NeedsReview, ErrorCodes.SourceChanged, _timeProvider.GetUtcNow());
            return Task.CompletedTask;
        }

        _repository.UpdateJobState(job.Id, JobStates.Cleaning, null, _timeProvider.GetUtcNow());
        string fullSourceRoot = Path.GetFullPath(job.SourcePath);

        bool anyDeniedOrUncertain = false;
        bool anySourceChanged = false;
        bool anyAlreadyStartedCleanup = items.Any(i => i.SourceRemoved);
        int index = 0;

        // 1. Clean verified files first
        foreach (var fileItem in items.Where(i => i.EntryKind == "file"))
        {
            OnBeforeCleanItemIndex?.Invoke(job.Id, index++);

            if (ct.IsCancellationRequested)
            {
                // Registered already: stop remaining source cleanup and preserve target attachment!
                _repository.UpdateJobState(job.Id, JobStates.SourceCleanupPending, null, _timeProvider.GetUtcNow());
                return Task.CompletedTask;
            }

            if (fileItem.SourceRemoved)
            {
                continue;
            }

            if (!fileItem.Verified)
            {
                anyDeniedOrUncertain = true;
                continue;
            }

            string filePath = fileItem.RelativePath == "."
                ? fullSourceRoot
                : Path.Combine(fullSourceRoot, ManagedFileStorePathHelper.ToPlatformPath(fileItem.RelativePath));

            var outcome = _fileStore.SourceCleanup.RemoveVerifiedSource(filePath, fileItem.SourceIdentity, fileItem.Sha256);
            switch (outcome)
            {
                case SourceCleanupOutcome.Removed:
                    anyAlreadyStartedCleanup = true;
                    _repository.MarkJobItemSourceRemoved(job.Id, fileItem.RelativePath);
                    break;
                case SourceCleanupOutcome.Changed:
                    anySourceChanged = true;
                    break;
                case SourceCleanupOutcome.Denied:
                case SourceCleanupOutcome.Uncertain:
                    anyDeniedOrUncertain = true;
                    break;
            }

            if (anySourceChanged)
            {
                break;
            }
        }

        // 2. Clean verified empty directories bottom-up (longest relative path first)
        if (!anySourceChanged && !anyDeniedOrUncertain)
        {
            var dirItemsBottomUp = items
                .Where(i => i.EntryKind == "directory")
                .OrderByDescending(i => i.RelativePath == "." ? -1 : i.RelativePath.Length)
                .ToList();

            var emptySet = new HashSet<string>();
            foreach (var dirItem in dirItemsBottomUp)
            {
                OnBeforeCleanItemIndex?.Invoke(job.Id, index++);

                if (ct.IsCancellationRequested)
                {
                    _repository.UpdateJobState(job.Id, JobStates.SourceCleanupPending, null, _timeProvider.GetUtcNow());
                    return Task.CompletedTask;
                }

                if (dirItem.SourceRemoved)
                {
                    continue;
                }

                string dirPath = dirItem.RelativePath == "."
                    ? fullSourceRoot
                    : Path.Combine(fullSourceRoot, ManagedFileStorePathHelper.ToPlatformPath(dirItem.RelativePath));

                var outcome = _fileStore.SourceCleanup.RemoveEmptyVerifiedDirectory(dirPath, dirItem.SourceIdentity, emptySet);
                switch (outcome)
                {
                    case SourceCleanupOutcome.Removed:
                        anyAlreadyStartedCleanup = true;
                        _repository.MarkJobItemSourceRemoved(job.Id, dirItem.RelativePath);
                        break;
                    case SourceCleanupOutcome.Changed:
                        anySourceChanged = true;
                        break;
                    case SourceCleanupOutcome.Denied:
                    case SourceCleanupOutcome.Uncertain:
                        anyDeniedOrUncertain = true;
                        break;
                }

                if (anySourceChanged)
                {
                    break;
                }
            }
        }

        if (anySourceChanged)
        {
            // If source path was replaced before any removal occurred -> needs_review with SourceChanged;
            // if a new file was added inside a directory during copy -> source_cleanup_pending with SourceChanged.
            string finalState = anyAlreadyStartedCleanup ? JobStates.SourceCleanupPending : JobStates.NeedsReview;
            _repository.UpdateJobState(job.Id, finalState, ErrorCodes.SourceChanged, _timeProvider.GetUtcNow());
            return Task.CompletedTask;
        }

        if (anyDeniedOrUncertain)
        {
            _repository.UpdateJobState(job.Id, JobStates.SourceCleanupPending, ErrorCodes.PathUnavailable, _timeProvider.GetUtcNow());
            return Task.CompletedTask;
        }

        _repository.UpdateJobState(job.Id, JobStates.Done, null, _timeProvider.GetUtcNow());
        return Task.CompletedTask;
    }

    private static class ManagedFileStorePathHelper
    {
        public static string ToPlatformPath(string normalizedRel) =>
            normalizedRel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    }
}
