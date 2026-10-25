using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Ports;
using Microsoft.Data.Sqlite;

namespace DesktopNotes.Infrastructure.Persistence;

public sealed partial class SqliteStore : IBackupDatabasePort
{
    public string DatabaseFilePath
    {
        get
        {
            var builder = new SqliteConnectionStringBuilder(_connectionString);
            return builder.DataSource;
        }
    }

    public void BackupOnlineTo(string destinationDbPath)
    {
        string fullDest = Path.GetFullPath(destinationDbPath);
        string? dir = Path.GetDirectoryName(fullDest);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (File.Exists(fullDest))
        {
            File.Delete(fullDest);
        }

        lock (_writeLock)
        {
            using var sourceConn = OpenConnection();
            var destBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = fullDest,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            };
            using var destConn = new SqliteConnection(destBuilder.ToString());
            destConn.Open();
            sourceConn.BackupDatabase(destConn);
        }
    }

    public void PurgeTrashedDataInSnapshot(string snapshotDbPath)
    {
        using var conn = OpenSnapshotConnection(snapshotDbPath, readOnly: false);
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM attachments WHERE trashed_utc_ms IS NOT NULL;
            DELETE FROM records WHERE deleted_utc_ms IS NOT NULL;
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public void VerifyDatabaseSnapshot(string snapshotDbPath)
    {
        if (!File.Exists(snapshotDbPath))
        {
            throw new AppException("backup_db_missing", "备份中缺少数据库快照文件。");
        }

        using var conn = OpenSnapshotConnection(snapshotDbPath, readOnly: true);

        using (var versionCmd = conn.CreateCommand())
        {
            versionCmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
            long version = Convert.ToInt64(versionCmd.ExecuteScalar() ?? 0L);
            if (version > 1)
            {
                throw new AppException("unsupported_schema_version", $"备份数据库版本 ({version}) 高于当前程序支持版本 (1)，请升级程序后再恢复。");
            }
        }

        using (var integrityCmd = conn.CreateCommand())
        {
            integrityCmd.CommandText = "PRAGMA integrity_check;";
            string? result = integrityCmd.ExecuteScalar()?.ToString();
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new AppException("corrupt_backup_db", $"备份数据库完整性检查未通过: {result}");
            }
        }

        using (var fkCmd = conn.CreateCommand())
        {
            fkCmd.CommandText = "PRAGMA foreign_key_check;";
            using var reader = fkCmd.ExecuteReader();
            if (reader.Read())
            {
                throw new AppException("corrupt_backup_fk", "备份数据库外键完整性检查失败。");
            }
        }
    }

    public IReadOnlyList<StorageRootRecord> GetAllStorageRootsFromSnapshot(string snapshotDbPath)
    {
        using var conn = OpenSnapshotConnection(snapshotDbPath, readOnly: true);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, absolute_path, platform, created_utc_ms FROM storage_roots ORDER BY created_utc_ms ASC;";
        using var reader = cmd.ExecuteReader();
        var list = new List<StorageRootRecord>();
        while (reader.Read())
        {
            list.Add(new StorageRootRecord(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3)));
        }
        return list;
    }

    public IReadOnlyList<AttachmentView> GetAttachmentsFromSnapshot(string snapshotDbPath, bool includeTrash)
    {
        using var conn = OpenSnapshotConnection(snapshotDbPath, readOnly: true);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = includeTrash
            ? """
              SELECT a.id, a.record_id, a.root_id, a.relative_path, a.original_name, a.entry_kind,
                     a.state, a.bytes_at_import, a.created_utc_ms, a.trashed_utc_ms
              FROM attachments a
              ORDER BY a.created_utc_ms ASC, a.id ASC;
              """
            : """
              SELECT a.id, a.record_id, a.root_id, a.relative_path, a.original_name, a.entry_kind,
                     a.state, a.bytes_at_import, a.created_utc_ms, a.trashed_utc_ms
              FROM attachments a
              JOIN records r ON r.id = a.record_id
              WHERE a.trashed_utc_ms IS NULL AND r.deleted_utc_ms IS NULL
              ORDER BY a.created_utc_ms ASC, a.id ASC;
              """;
        using var reader = cmd.ExecuteReader();
        var list = new List<AttachmentView>();
        while (reader.Read())
        {
            long? trashedMs = reader.IsDBNull(9) ? null : reader.GetInt64(9);
            list.Add(new AttachmentView(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt64(7),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
                trashedMs.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(trashedMs.Value) : null));
        }
        return list;
    }

    public IReadOnlyList<BackupJobSummary> GetJobsSummaryFromSnapshot(string snapshotDbPath)
    {
        using var conn = OpenSnapshotConnection(snapshotDbPath, readOnly: true);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, attachment_id, state, source_device_id FROM attachment_jobs ORDER BY created_utc_ms ASC;";
        using var reader = cmd.ExecuteReader();
        var list = new List<BackupJobSummary>();
        while (reader.Read())
        {
            list.Add(new BackupJobSummary(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3)));
        }
        return list;
    }

    public void ReplaceCurrentDatabaseWith(
        string verifiedStagedDbPath,
        IReadOnlyDictionary<Guid, string> rootMappings,
        string currentDeviceId)
    {
        lock (_writeLock)
        {
            // First update storage_roots and sanitize foreign attachment_jobs inside the staged DB
            string platform = OperatingSystem.IsWindows()
                ? "windows"
                : OperatingSystem.IsMacOS() ? "macos" : "linux";

            using (var stagedConn = OpenSnapshotConnection(verifiedStagedDbPath, readOnly: false))
            {
                using var tx = stagedConn.BeginTransaction();
                foreach (var kvp in rootMappings)
                {
                    using var updateRoot = stagedConn.CreateCommand();
                    updateRoot.Transaction = tx;
                    updateRoot.CommandText = """
                        UPDATE storage_roots
                        SET absolute_path = $path,
                            platform = $platform
                        WHERE id = $id;
                        """;
                    updateRoot.Parameters.AddWithValue("$id", kvp.Key.ToString("D"));
                    updateRoot.Parameters.AddWithValue("$path", Path.GetFullPath(kvp.Value));
                    updateRoot.Parameters.AddWithValue("$platform", platform);
                    updateRoot.ExecuteNonQuery();
                }

                // Rule 8.4 & 11.2: Any incomplete or source_cleanup_pending job from a backup must become needs_review
                // and must NEVER execute source cleanup on the restoring machine.
                using (var updateJobs = stagedConn.CreateCommand())
                {
                    updateJobs.Transaction = tx;
                    updateJobs.CommandText = """
                        UPDATE attachment_jobs
                        SET state = 'needs_review',
                            error_code = 'restored_from_backup_no_source_cleanup'
                        WHERE state NOT IN ('done', 'cancelled', 'needs_review');
                        """;
                    updateJobs.ExecuteNonQuery();
                }

                tx.Commit();
            }

            // Copy the staged database into the active database via SQLite Online Backup API
            // so even if the current store is file-backed or shared-memory, it atomically replaces all tables & WAL state!
            using var sourceConn = OpenSnapshotConnection(verifiedStagedDbPath, readOnly: true);
            using var destConn = OpenConnection();
            sourceConn.BackupDatabase(destConn);
        }
    }

    private static SqliteConnection OpenSnapshotConnection(string dbPath, bool readOnly)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(dbPath),
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false
        };
        var conn = new SqliteConnection(builder.ToString());
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }
}
