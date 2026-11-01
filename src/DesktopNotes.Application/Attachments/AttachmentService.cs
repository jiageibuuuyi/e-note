using System.Diagnostics;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Ports;

namespace DesktopNotes.Application.Attachments;

public sealed class AttachmentService : IAttachmentService
{
    private readonly IAttachmentRepositoryPort _repository;
    private readonly IManagedFileStorePort _fileStore;
    private readonly ImportWorker _worker;
    private readonly RecoveryService _recoveryService;
    private readonly IFileClipboard? _clipboard;
    private readonly TimeProvider _timeProvider;
    private Guid _defaultRootId;
    private string _defaultRootPath;

    public bool AutoExecuteJobsOnEnqueue { get; set; } = true;

    public AttachmentService(
        IAttachmentRepositoryPort repository,
        IManagedFileStorePort fileStore,
        ImportWorker worker,
        RecoveryService recoveryService,
        string defaultStorageRootPath,
        IFileClipboard? clipboard = null,
        TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _recoveryService = recoveryService ?? throw new ArgumentNullException(nameof(recoveryService));
        _clipboard = clipboard;
        _timeProvider = timeProvider ?? TimeProvider.System;

        string platform = OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsMacOS() ? "macos" : "linux";

        var root = _repository.EnsureDefaultStorageRoot(
            Path.GetFullPath(defaultStorageRootPath),
            platform,
            _timeProvider.GetUtcNow());
        _defaultRootId = root.Id;
        _defaultRootPath = root.AbsolutePath;
    }

    public Guid DefaultRootId => _defaultRootId;
    public string DefaultRootPath => _defaultRootPath;

    public void SetDefaultStorageRoot(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        string fullPath = Path.GetFullPath(folderPath);
        Directory.CreateDirectory(fullPath);

        string platform = OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsMacOS() ? "macos" : "linux";

        var existing = _repository.ListStorageRoots()
            .FirstOrDefault(r => string.Equals(r.AbsolutePath, fullPath, StringComparison.OrdinalIgnoreCase));
        var root = existing ?? _repository.RegisterStorageRoot(
            Guid.NewGuid(),
            fullPath,
            platform,
            _timeProvider.GetUtcNow());

        _defaultRootId = root.Id;
        _defaultRootPath = root.AbsolutePath;
    }

    public async Task<IReadOnlyList<Guid>> EnqueueAsync(ImportRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var normalizedSources = _fileStore.NormalizeAndDeduplicateSources(request.SourcePaths);
        if (normalizedSources.Count == 0)
        {
            throw new AppException(ErrorCodes.Validation, "No valid source paths provided.");
        }

        // Drag always forces Move regardless of modifier keys!
        ImportIntent effectiveIntent = request.Origin switch
        {
            ImportOrigin.Drag => ImportIntent.Move,
            ImportOrigin.FilePicker => ImportIntent.Copy,
            _ => request.Intent
        };

        Guid rootId = _repository.ResolveAttachmentRootForRecord(request.RecordId, _defaultRootId);
        var rootRecord = _repository.GetStorageRoot(rootId)
            ?? throw new AppException(ErrorCodes.PathUnavailable, "Target storage root not found.", new[] { rootId });

        var allManagedRootPaths = _repository.ListStorageRoots().Select(r => r.AbsolutePath).ToArray();

        // Validate all sources up front before creating any job
        foreach (string src in normalizedSources)
        {
            _fileStore.ValidateSourceAndRoot(src, rootRecord.AbsolutePath, allManagedRootPaths);
        }

        long nowMs = _timeProvider.GetUtcNow().ToUniversalTime().ToUnixTimeMilliseconds();
        string operation = effectiveIntent == ImportIntent.Move ? "move" : "copy";
        var createdJobIds = new List<Guid>();

        foreach (string src in normalizedSources)
        {
            Guid jobId = Guid.NewGuid();
            Guid attachmentId = Guid.NewGuid();
            var manifest = _fileStore.BuildSourceManifest(jobId, src);

            string targetRelativePath = $"tasks/{request.RecordId:D}/{attachmentId:D}/{manifest.OriginalName}";
            string stagingRelativePath = $".desktopnotes-staging/{jobId:D}/payload/{manifest.OriginalName}";

            var jobRecord = new AttachmentJobRecord(
                jobId,
                request.BatchId,
                request.RecordId,
                attachmentId,
                rootId,
                operation,
                src,
                targetRelativePath,
                stagingRelativePath,
                JobStates.Planned,
                _fileStore.CurrentDeviceId,
                ErrorCode: null,
                nowMs,
                nowMs);

            _repository.CreatePlannedJob(jobRecord, manifest.Items);
            createdJobIds.Add(jobId);
        }

        if (AutoExecuteJobsOnEnqueue)
        {
            foreach (Guid jobId in createdJobIds)
            {
                await _worker.ExecuteJobAsync(jobId, ct);
            }
        }

        return createdJobIds;
    }

    public async Task<IReadOnlyList<Guid>> PasteFromClipboardAsync(Guid recordId, CancellationToken ct)
    {
        if (_clipboard is null)
        {
            return Array.Empty<Guid>();
        }

        var clip = await _clipboard.ReadFilesAsync(ct);
        if (clip is null || clip.Paths.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        var req = new ImportRequest(
            Guid.NewGuid(),
            recordId,
            clip.Paths,
            clip.Intent,
            ImportOrigin.Paste);

        var jobIds = await EnqueueAsync(req, ct);

        if (clip.Intent == ImportIntent.Move)
        {
            bool allMoved = true;
            foreach (Guid jobId in jobIds)
            {
                var job = _repository.GetJobRecord(jobId);
                if (job?.State != JobStates.Done)
                {
                    allMoved = false;
                    break;
                }
            }

            if (allMoved)
            {
                await _clipboard.AcknowledgeMoveAsync(clip.ClipboardToken, ct);
            }
        }

        return jobIds;
    }

    public Task<JobView> GetJobAsync(Guid jobId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var job = _repository.GetJobRecord(jobId)
            ?? throw new AppException(ErrorCodes.NotFound, "Job not found.", new[] { jobId });
        var items = _repository.GetJobItems(jobId);
        long bytesTotal = items.Where(i => i.EntryKind == "file").Sum(i => i.SourceSize);
        long bytesCopied = job.State is JobStates.Verified or JobStates.Published or JobStates.Registered or JobStates.Cleaning or JobStates.Done or JobStates.SourceCleanupPending
            ? bytesTotal
            : _worker.GetBytesCopied(jobId);

        return Task.FromResult(new JobView(
            job.Id,
            job.BatchId,
            job.State,
            bytesCopied,
            bytesTotal,
            job.ErrorCode));
    }

    public Task CancelAsync(Guid jobId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _worker.RequestCancel(jobId);

        var job = _repository.GetJobRecord(jobId)
            ?? throw new AppException(ErrorCodes.NotFound, "Job not found.", new[] { jobId });

        if (job.State is JobStates.Planned or JobStates.Copying or JobStates.Verified or JobStates.Failed)
        {
            var root = _repository.GetStorageRoot(job.RootId);
            if (root is not null)
            {
                _fileStore.CleanupStaging(root.AbsolutePath, jobId);
            }
            _repository.UpdateJobState(jobId, JobStates.Cancelled, null, _timeProvider.GetUtcNow());
        }
        else if (job.State is JobStates.Registered or JobStates.Cleaning)
        {
            // Target already registered as formal attachment -> never delete target, transition to source_cleanup_pending
            _repository.UpdateJobState(jobId, JobStates.SourceCleanupPending, null, _timeProvider.GetUtcNow());
        }

        return Task.CompletedTask;
    }

    public async Task RetryAsync(Guid jobId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var job = _repository.GetJobRecord(jobId)
            ?? throw new AppException(ErrorCodes.NotFound, "Job not found.", new[] { jobId });

        if (job.State == JobStates.Done)
        {
            return;
        }

        if (job.State is JobStates.Failed or JobStates.Planned)
        {
            var root = _repository.GetStorageRoot(job.RootId)
                ?? throw new AppException(ErrorCodes.PathUnavailable, "Storage root not found.", new[] { job.RootId });
            var allManagedRootPaths = _repository.ListStorageRoots().Select(r => r.AbsolutePath).ToArray();
            _fileStore.ValidateSourceAndRoot(job.SourcePath, root.AbsolutePath, allManagedRootPaths);

            var manifest = _fileStore.BuildSourceManifest(jobId, job.SourcePath);
            _repository.ReplaceJobManifest(jobId, manifest.Items, JobStates.Planned, _timeProvider.GetUtcNow());
            await _worker.ExecuteJobAsync(jobId, ct);
            return;
        }

        await _recoveryService.ReconcileSingleJobAsync(jobId, ct);
    }

    public Task OpenAsync(Guid attachmentId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var att = _repository.GetAttachment(attachmentId)
            ?? throw new AppException(ErrorCodes.NotFound, "Attachment not found.", new[] { attachmentId });
        var root = _repository.GetStorageRoot(att.RootId)
            ?? throw new AppException(ErrorCodes.PathUnavailable, "Storage root not found.", new[] { att.RootId });

        string fullPath = _fileStore.ResolveManagedAttachmentFullPath(root.AbsolutePath, att.RelativePath);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            _repository.SetAttachmentState(attachmentId, "missing");
            throw new AppException(ErrorCodes.PathUnavailable, "Attachment file is missing on disk.", new[] { attachmentId });
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = fullPath,
            UseShellExecute = true
        });
        return Task.CompletedTask;
    }

    public Task TrashAttachmentAsync(Guid attachmentId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _repository.SetAttachmentTrashState(attachmentId, _timeProvider.GetUtcNow());
        return Task.CompletedTask;
    }

    public Task RestoreAttachmentAsync(Guid attachmentId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _repository.SetAttachmentTrashState(attachmentId, null);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AttachmentView>> ListForRecordAsync(Guid recordId, bool includeTrashed, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_repository.ListAttachmentsForRecord(recordId, includeTrashed));
    }
}
