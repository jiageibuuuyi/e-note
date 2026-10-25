using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Ports;
using Microsoft.Data.Sqlite;

namespace DesktopNotes.Infrastructure.Persistence;

public sealed partial class SqliteStore
{
    public StorageRootRecord EnsureDefaultStorageRoot(string defaultRootPath, string platform, DateTimeOffset nowUtc)
    {
        return Write((conn, _) =>
        {
            using (var findCmd = conn.CreateCommand())
            {
                findCmd.CommandText = "SELECT id, absolute_path, platform, created_utc_ms FROM storage_roots ORDER BY created_utc_ms ASC LIMIT 1;";
                using var reader = findCmd.ExecuteReader();
                if (reader.Read())
                {
                    return new StorageRootRecord(
                        Guid.Parse(reader.GetString(0)),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt64(3));
                }
            }

            var rootId = Guid.NewGuid();
            long nowMs = nowUtc.ToUniversalTime().ToUnixTimeMilliseconds();
            using (var insCmd = conn.CreateCommand())
            {
                insCmd.CommandText = """
                    INSERT INTO storage_roots (id, absolute_path, platform, created_utc_ms)
                    VALUES ($id, $path, $plat, $now);
                    """;
                insCmd.Parameters.AddWithValue("$id", rootId.ToString("D"));
                insCmd.Parameters.AddWithValue("$path", defaultRootPath);
                insCmd.Parameters.AddWithValue("$plat", platform);
                insCmd.Parameters.AddWithValue("$now", nowMs);
                insCmd.ExecuteNonQuery();
            }

            return new StorageRootRecord(rootId, defaultRootPath, platform, nowMs);
        });
    }

    public StorageRootRecord RegisterStorageRoot(Guid id, string absolutePath, string platform, DateTimeOffset nowUtc)
    {
        return Write((conn, _) =>
        {
            long nowMs = nowUtc.ToUniversalTime().ToUnixTimeMilliseconds();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO storage_roots (id, absolute_path, platform, created_utc_ms)
                VALUES ($id, $path, $plat, $now)
                ON CONFLICT(id) DO UPDATE SET absolute_path = excluded.absolute_path, platform = excluded.platform;
                """;
            cmd.Parameters.AddWithValue("$id", id.ToString("D"));
            cmd.Parameters.AddWithValue("$path", absolutePath);
            cmd.Parameters.AddWithValue("$plat", platform);
            cmd.Parameters.AddWithValue("$now", nowMs);
            cmd.ExecuteNonQuery();
            return new StorageRootRecord(id, absolutePath, platform, nowMs);
        });
    }

    public StorageRootRecord? GetStorageRoot(Guid id)
    {
        return Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, absolute_path, platform, created_utc_ms FROM storage_roots WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id.ToString("D"));
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return new StorageRootRecord(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3));
        });
    }

    public IReadOnlyList<StorageRootRecord> ListStorageRoots()
    {
        return Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, absolute_path, platform, created_utc_ms FROM storage_roots ORDER BY created_utc_ms ASC;";
            var list = new List<StorageRootRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new StorageRootRecord(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3)));
            }
            return list;
        });
    }

    public Guid ResolveAttachmentRootForRecord(Guid recordId, Guid defaultRootId)
    {
        return Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT attachment_root_id FROM effective_records WHERE record_id = $id;";
            cmd.Parameters.AddWithValue("$id", recordId.ToString("D"));
            object? val = cmd.ExecuteScalar();
            if (val is string s && Guid.TryParse(s, out var rootId))
            {
                return rootId;
            }
            return defaultRootId;
        });
    }

    public void CreatePlannedJob(AttachmentJobRecord job, IReadOnlyList<AttachmentJobItemRecord> items)
    {
        Write((conn, _) =>
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO attachment_jobs (
                        id, batch_id, record_id, attachment_id, root_id,
                        operation, source_path, target_relative_path, staging_relative_path,
                        state, source_device_id, error_code, created_utc_ms, updated_utc_ms
                    ) VALUES (
                        $id, $batch, $rec, $att, $root,
                        $op, $src, $target, $staging,
                        $state, $dev, $err, $created, $updated
                    );
                    """;
                cmd.Parameters.AddWithValue("$id", job.Id.ToString("D"));
                cmd.Parameters.AddWithValue("$batch", job.BatchId.ToString("D"));
                cmd.Parameters.AddWithValue("$rec", job.RecordId.ToString("D"));
                cmd.Parameters.AddWithValue("$att", job.AttachmentId.ToString("D"));
                cmd.Parameters.AddWithValue("$root", job.RootId.ToString("D"));
                cmd.Parameters.AddWithValue("$op", job.Operation);
                cmd.Parameters.AddWithValue("$src", job.SourcePath);
                cmd.Parameters.AddWithValue("$target", job.TargetRelativePath);
                cmd.Parameters.AddWithValue("$staging", job.StagingRelativePath);
                cmd.Parameters.AddWithValue("$state", job.State);
                cmd.Parameters.AddWithValue("$dev", job.SourceDeviceId);
                cmd.Parameters.AddWithValue("$err", (object?)job.ErrorCode ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$created", job.CreatedUtcMs);
                cmd.Parameters.AddWithValue("$updated", job.UpdatedUtcMs);
                cmd.ExecuteNonQuery();
            }

            InsertJobItemsInternal(conn, items);
            return 0;
        });
    }

    public void ReplaceJobManifest(Guid jobId, IReadOnlyList<AttachmentJobItemRecord> items, string newState, DateTimeOffset nowUtc)
    {
        Write((conn, _) =>
        {
            using (var delCmd = conn.CreateCommand())
            {
                delCmd.CommandText = "DELETE FROM attachment_job_items WHERE job_id = $id;";
                delCmd.Parameters.AddWithValue("$id", jobId.ToString("D"));
                delCmd.ExecuteNonQuery();
            }

            InsertJobItemsInternal(conn, items);

            using (var updCmd = conn.CreateCommand())
            {
                updCmd.CommandText = "UPDATE attachment_jobs SET state = $state, error_code = NULL, updated_utc_ms = $now WHERE id = $id;";
                updCmd.Parameters.AddWithValue("$state", newState);
                updCmd.Parameters.AddWithValue("$now", nowUtc.ToUniversalTime().ToUnixTimeMilliseconds());
                updCmd.Parameters.AddWithValue("$id", jobId.ToString("D"));
                updCmd.ExecuteNonQuery();
            }

            return 0;
        });
    }

    private static void InsertJobItemsInternal(SqliteConnection conn, IReadOnlyList<AttachmentJobItemRecord> items)
    {
        foreach (var item in items)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO attachment_job_items (
                    job_id, relative_path, entry_kind, source_identity,
                    source_size, source_mtime_ns, sha256, verified, source_removed
                ) VALUES (
                    $job, $rel, $kind, $ident,
                    $size, $mtime, $sha, $ver, $rem
                );
                """;
            cmd.Parameters.AddWithValue("$job", item.JobId.ToString("D"));
            cmd.Parameters.AddWithValue("$rel", item.RelativePath);
            cmd.Parameters.AddWithValue("$kind", item.EntryKind);
            cmd.Parameters.AddWithValue("$ident", item.SourceIdentity);
            cmd.Parameters.AddWithValue("$size", item.SourceSize);
            cmd.Parameters.AddWithValue("$mtime", item.SourceMtimeNs);
            cmd.Parameters.AddWithValue("$sha", (object?)item.Sha256 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ver", item.Verified ? 1 : 0);
            cmd.Parameters.AddWithValue("$rem", item.SourceRemoved ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateJobState(Guid jobId, string state, string? errorCode, DateTimeOffset nowUtc)
    {
        Write((conn, _) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE attachment_jobs SET state = $state, error_code = $err, updated_utc_ms = $now WHERE id = $id;";
            cmd.Parameters.AddWithValue("$state", state);
            cmd.Parameters.AddWithValue("$err", (object?)errorCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", nowUtc.ToUniversalTime().ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$id", jobId.ToString("D"));
            cmd.ExecuteNonQuery();
            return 0;
        });
    }

    public void MarkJobItemsVerified(Guid jobId, IReadOnlyDictionary<string, string?> fileHashes, string newState, DateTimeOffset nowUtc)
    {
        Write((conn, _) =>
        {
            foreach (var (relPath, sha) in fileHashes)
            {
                using var itemCmd = conn.CreateCommand();
                itemCmd.CommandText = """
                    UPDATE attachment_job_items
                    SET sha256 = $sha, verified = 1
                    WHERE job_id = $job AND relative_path = $rel;
                    """;
                itemCmd.Parameters.AddWithValue("$sha", (object?)sha ?? DBNull.Value);
                itemCmd.Parameters.AddWithValue("$job", jobId.ToString("D"));
                itemCmd.Parameters.AddWithValue("$rel", relPath);
                itemCmd.ExecuteNonQuery();
            }

            using var jobCmd = conn.CreateCommand();
            jobCmd.CommandText = "UPDATE attachment_jobs SET state = $state, error_code = NULL, updated_utc_ms = $now WHERE id = $id;";
            jobCmd.Parameters.AddWithValue("$state", newState);
            jobCmd.Parameters.AddWithValue("$now", nowUtc.ToUniversalTime().ToUnixTimeMilliseconds());
            jobCmd.Parameters.AddWithValue("$id", jobId.ToString("D"));
            jobCmd.ExecuteNonQuery();
            return 0;
        });
    }

    public void MarkJobItemSourceRemoved(Guid jobId, string relativePath)
    {
        Write((conn, _) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE attachment_job_items
                SET source_removed = 1
                WHERE job_id = $job AND relative_path = $rel;
                """;
            cmd.Parameters.AddWithValue("$job", jobId.ToString("D"));
            cmd.Parameters.AddWithValue("$rel", relativePath);
            cmd.ExecuteNonQuery();
            return 0;
        });
    }

    public void RegisterPublishedAttachment(
        Guid jobId,
        AttachmentView attachment,
        string nextJobState,
        DateTimeOffset nowUtc)
    {
        Write((conn, _) =>
        {
            using (var attCmd = conn.CreateCommand())
            {
                attCmd.CommandText = """
                    INSERT INTO attachments (
                        id, record_id, root_id, relative_path, original_name,
                        entry_kind, state, bytes_at_import, created_utc_ms, trashed_utc_ms
                    ) VALUES (
                        $id, $rec, $root, $rel, $name,
                        $kind, $state, $bytes, $created, $trashed
                    )
                    ON CONFLICT(id) DO UPDATE SET
                        state = excluded.state,
                        bytes_at_import = excluded.bytes_at_import;
                    """;
                attCmd.Parameters.AddWithValue("$id", attachment.Id.ToString("D"));
                attCmd.Parameters.AddWithValue("$rec", attachment.RecordId.ToString("D"));
                attCmd.Parameters.AddWithValue("$root", attachment.RootId.ToString("D"));
                attCmd.Parameters.AddWithValue("$rel", attachment.RelativePath);
                attCmd.Parameters.AddWithValue("$name", attachment.OriginalName);
                attCmd.Parameters.AddWithValue("$kind", attachment.EntryKind);
                attCmd.Parameters.AddWithValue("$state", attachment.State);
                attCmd.Parameters.AddWithValue("$bytes", attachment.BytesAtImport);
                attCmd.Parameters.AddWithValue("$created", attachment.CreatedAt.ToUniversalTime().ToUnixTimeMilliseconds());
                attCmd.Parameters.AddWithValue("$trashed", attachment.TrashedAt.HasValue ? attachment.TrashedAt.Value.ToUniversalTime().ToUnixTimeMilliseconds() : DBNull.Value);
                attCmd.ExecuteNonQuery();
            }

            using (var jobCmd = conn.CreateCommand())
            {
                jobCmd.CommandText = "UPDATE attachment_jobs SET state = $state, error_code = NULL, updated_utc_ms = $now WHERE id = $id;";
                jobCmd.Parameters.AddWithValue("$state", nextJobState);
                jobCmd.Parameters.AddWithValue("$now", nowUtc.ToUniversalTime().ToUnixTimeMilliseconds());
                jobCmd.Parameters.AddWithValue("$id", jobId.ToString("D"));
                jobCmd.ExecuteNonQuery();
            }

            return 0;
        });
    }

    public AttachmentJobRecord? GetJobRecord(Guid jobId)
    {
        return Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, batch_id, record_id, attachment_id, root_id,
                       operation, source_path, target_relative_path, staging_relative_path,
                       state, source_device_id, error_code, created_utc_ms, updated_utc_ms
                FROM attachment_jobs
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", jobId.ToString("D"));
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? MapJobRecord(reader) : null;
        });
    }

    public IReadOnlyList<AttachmentJobItemRecord> GetJobItems(Guid jobId)
    {
        return Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT job_id, relative_path, entry_kind, source_identity,
                       source_size, source_mtime_ns, sha256, verified, source_removed
                FROM attachment_job_items
                WHERE job_id = $id
                ORDER BY relative_path ASC;
                """;
            cmd.Parameters.AddWithValue("$id", jobId.ToString("D"));
            var list = new List<AttachmentJobItemRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new AttachmentJobItemRecord(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetInt32(7) == 1,
                    reader.GetInt32(8) == 1));
            }
            return list;
        });
    }

    public IReadOnlyList<AttachmentJobRecord> GetIncompleteJobs()
    {
        return Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, batch_id, record_id, attachment_id, root_id,
                       operation, source_path, target_relative_path, staging_relative_path,
                       state, source_device_id, error_code, created_utc_ms, updated_utc_ms
                FROM attachment_jobs
                WHERE state NOT IN ('done', 'cancelled')
                ORDER BY created_utc_ms ASC;
                """;
            var list = new List<AttachmentJobRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(MapJobRecord(reader));
            }
            return list;
        });
    }

    public AttachmentView? GetAttachment(Guid attachmentId)
    {
        return Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, record_id, root_id, relative_path, original_name,
                       entry_kind, state, bytes_at_import, created_utc_ms, trashed_utc_ms
                FROM attachments
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", attachmentId.ToString("D"));
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? MapAttachmentView(reader) : null;
        });
    }

    public IReadOnlyList<AttachmentView> ListAttachmentsForRecord(Guid recordId, bool includeTrashed)
    {
        return Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, record_id, root_id, relative_path, original_name,
                       entry_kind, state, bytes_at_import, created_utc_ms, trashed_utc_ms
                FROM attachments
                WHERE record_id = $rec
                  AND ($inc = 1 OR trashed_utc_ms IS NULL)
                ORDER BY created_utc_ms ASC, id ASC;
                """;
            cmd.Parameters.AddWithValue("$rec", recordId.ToString("D"));
            cmd.Parameters.AddWithValue("$inc", includeTrashed ? 1 : 0);
            var list = new List<AttachmentView>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(MapAttachmentView(reader));
            }
            return list;
        });
    }

    public void SetAttachmentTrashState(Guid attachmentId, DateTimeOffset? trashedAtUtc)
    {
        Write((conn, _) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE attachments SET trashed_utc_ms = $trashed WHERE id = $id;";
            cmd.Parameters.AddWithValue("$trashed", trashedAtUtc.HasValue ? trashedAtUtc.Value.ToUniversalTime().ToUnixTimeMilliseconds() : DBNull.Value);
            cmd.Parameters.AddWithValue("$id", attachmentId.ToString("D"));
            int rows = cmd.ExecuteNonQuery();
            if (rows == 0)
            {
                throw new AppException(ErrorCodes.NotFound, "Attachment not found.", new[] { attachmentId });
            }
            return 0;
        });
    }

    public void SetAttachmentState(Guid attachmentId, string state)
    {
        Write((conn, _) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE attachments SET state = $state WHERE id = $id;";
            cmd.Parameters.AddWithValue("$state", state);
            cmd.Parameters.AddWithValue("$id", attachmentId.ToString("D"));
            cmd.ExecuteNonQuery();
            return 0;
        });
    }

    private static AttachmentJobRecord MapJobRecord(SqliteDataReader r)
    {
        return new AttachmentJobRecord(
            Guid.Parse(r.GetString(0)),
            Guid.Parse(r.GetString(1)),
            Guid.Parse(r.GetString(2)),
            Guid.Parse(r.GetString(3)),
            Guid.Parse(r.GetString(4)),
            r.GetString(5),
            r.GetString(6),
            r.GetString(7),
            r.GetString(8),
            r.GetString(9),
            r.GetString(10),
            r.IsDBNull(11) ? null : r.GetString(11),
            r.GetInt64(12),
            r.GetInt64(13));
    }

    private static AttachmentView MapAttachmentView(SqliteDataReader r)
    {
        return new AttachmentView(
            Guid.Parse(r.GetString(0)),
            Guid.Parse(r.GetString(1)),
            Guid.Parse(r.GetString(2)),
            r.GetString(3),
            r.GetString(4),
            r.GetString(5),
            r.GetString(6),
            r.GetInt64(7),
            DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(8)),
            r.IsDBNull(9) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(9)));
    }
}
