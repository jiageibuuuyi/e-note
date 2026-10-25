using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Ports;

namespace DesktopNotes.Application.Attachments;

public sealed class RecoveryService
{
    private readonly IAttachmentRepositoryPort _repository;
    private readonly IManagedFileStorePort _fileStore;
    private readonly ImportWorker _worker;
    private readonly TimeProvider _timeProvider;

    public RecoveryService(
        IAttachmentRepositoryPort repository,
        IManagedFileStorePort fileStore,
        ImportWorker worker,
        TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task ReconcileIncompleteJobsAsync(CancellationToken ct)
    {
        var incompleteJobs = _repository.GetIncompleteJobs();
        foreach (var job in incompleteJobs)
        {
            ct.ThrowIfCancellationRequested();
            await ReconcileSingleJobAsync(job.Id, ct);
        }
    }

    public async Task ReconcileSingleJobAsync(Guid jobId, CancellationToken ct)
    {
        var job = _repository.GetJobRecord(jobId);
        if (job is null || job.State is JobStates.Done or JobStates.Cancelled or JobStates.NeedsReview)
        {
            return;
        }

        // Rule: Never replay source cleanup from another device's restored backup!
        if (!string.Equals(job.SourceDeviceId, _fileStore.CurrentDeviceId, StringComparison.Ordinal))
        {
            _repository.UpdateJobState(jobId, JobStates.NeedsReview, ErrorCodes.SourceChanged, _timeProvider.GetUtcNow());
            return;
        }

        var root = _repository.GetStorageRoot(job.RootId);
        if (root is null)
        {
            _repository.UpdateJobState(jobId, JobStates.Failed, ErrorCodes.PathUnavailable, _timeProvider.GetUtcNow());
            return;
        }

        var items = _repository.GetJobItems(jobId);
        string stagingFull = _fileStore.ResolveManagedAttachmentFullPath(root.AbsolutePath, job.StagingRelativePath);
        string targetFull = _fileStore.ResolveManagedAttachmentFullPath(root.AbsolutePath, job.TargetRelativePath);

        bool targetExists = File.Exists(targetFull) || Directory.Exists(targetFull);
        bool stagingExists = File.Exists(stagingFull) || Directory.Exists(stagingFull);
        bool sourceExists = File.Exists(job.SourcePath) || Directory.Exists(job.SourcePath);

        // Case 1: Job is planned or copying (staging incomplete)
        if (job.State is JobStates.Planned or JobStates.Copying)
        {
            _fileStore.CleanupStaging(root.AbsolutePath, jobId);
            if (sourceExists)
            {
                _repository.UpdateJobState(jobId, JobStates.Failed, null, _timeProvider.GetUtcNow());
            }
            else
            {
                _repository.UpdateJobState(jobId, JobStates.NeedsReview, ErrorCodes.SourceChanged, _timeProvider.GetUtcNow());
            }
            return;
        }

        // Case 2: Job is verified, check whether payload is still in staging OR already moved to target (Crash B: published before DB update)
        if (job.State == JobStates.Verified)
        {
            if (targetExists)
            {
                if (_fileStore.VerifyExistingTreeMatchesManifest(targetFull, items, out _))
                {
                    _repository.UpdateJobState(jobId, JobStates.Published, null, _timeProvider.GetUtcNow());
                    await _worker.ExecuteJobAsync(jobId, ct);
                    return;
                }
                else
                {
                    // Target exists at published path but does not match manifest -> needs_review, never overwrite!
                    _repository.UpdateJobState(jobId, JobStates.NeedsReview, ErrorCodes.SourceChanged, _timeProvider.GetUtcNow());
                    return;
                }
            }

            if (stagingExists && _fileStore.VerifyExistingTreeMatchesManifest(stagingFull, items, out _))
            {
                // Crash A: verified in staging -> continue publish, register, and source cleanup
                await _worker.ExecuteJobAsync(jobId, ct);
                return;
            }

            // Staging missing/corrupted while source still exists -> mark failed (retryable)
            _fileStore.CleanupStaging(root.AbsolutePath, jobId);
            if (sourceExists)
            {
                _repository.UpdateJobState(jobId, JobStates.Failed, null, _timeProvider.GetUtcNow());
            }
            else
            {
                _repository.UpdateJobState(jobId, JobStates.NeedsReview, ErrorCodes.PathUnavailable, _timeProvider.GetUtcNow());
            }
            return;
        }

        // Case 3: Job is published -> verify target and register attachment with the same attachment_id
        if (job.State == JobStates.Published)
        {
            if (targetExists && _fileStore.VerifyExistingTreeMatchesManifest(targetFull, items, out _))
            {
                await _worker.ExecuteJobAsync(jobId, ct);
                return;
            }

            _repository.UpdateJobState(jobId, JobStates.NeedsReview, ErrorCodes.PathUnavailable, _timeProvider.GetUtcNow());
            return;
        }

        // Case 4: Job is registered, cleaning, or source_cleanup_pending
        if (job.State is JobStates.Registered or JobStates.Cleaning or JobStates.SourceCleanupPending)
        {
            if (!targetExists)
            {
                // Target missing after registration -> mark attachment missing!
                _repository.SetAttachmentState(job.AttachmentId, "missing");
                _repository.UpdateJobState(jobId, JobStates.NeedsReview, ErrorCodes.PathUnavailable, _timeProvider.GetUtcNow());
                return;
            }

            if (!sourceExists)
            {
                // Target verified and source already gone -> complete items and mark done
                foreach (var item in items.Where(i => !i.SourceRemoved))
                {
                    _repository.MarkJobItemSourceRemoved(jobId, item.RelativePath);
                }
                _repository.UpdateJobState(jobId, JobStates.Done, null, _timeProvider.GetUtcNow());
                return;
            }

            // Target exists and source still exists on same device -> resume verified source cleanup
            await _worker.ExecuteJobAsync(jobId, ct);
        }
    }
}
