using System.Text;
using System.Text.Json;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Ports;
using DesktopNotes.Application.Records;
using DesktopNotes.Domain;
using Microsoft.Data.Sqlite;

namespace DesktopNotes.Infrastructure.Persistence;

public sealed class SqliteStore : IRecordRepositoryPort, IDisposable
{
    private readonly string _connectionString;
    private readonly object _writeLock = new();
    private SqliteConnection? _keepAliveConnection;

    public string ConnectionString => _connectionString;

    public SqliteStore(string databasePathOrConnectionString, string? initialSqlPath = null)
    {
        if (string.Equals(databasePathOrConnectionString.Trim(), ":memory:", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(databasePathOrConnectionString.Trim(), "Data Source=:memory:", StringComparison.OrdinalIgnoreCase))
        {
            _connectionString = $"Data Source=file:desktopnotes_{Guid.NewGuid():N}?mode=memory&cache=shared";
            _keepAliveConnection = new SqliteConnection(_connectionString);
            _keepAliveConnection.Open();
        }
        else if (databasePathOrConnectionString.Contains("Data Source=", StringComparison.OrdinalIgnoreCase))
        {
            _connectionString = databasePathOrConnectionString;
            if (_connectionString.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase) ||
                _connectionString.Contains("mode=memory", StringComparison.OrdinalIgnoreCase))
            {
                _keepAliveConnection = new SqliteConnection(_connectionString);
                _keepAliveConnection.Open();
            }
        }
        else
        {
            string fullPath = Path.GetFullPath(databasePathOrConnectionString);
            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = fullPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true
            }.ToString();
        }

        MigrationRunner.Initialize(_connectionString, initialSqlPath);
    }

    public T Read<T>(Func<SqliteConnection, T> action)
    {
        using var connection = OpenConnection();
        return action(connection);
    }

    public T Write<T>(Func<SqliteConnection, SqliteTransaction?, T> action)
    {
        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var beginCmd = connection.CreateCommand();
            beginCmd.CommandText = "BEGIN IMMEDIATE;";
            beginCmd.ExecuteNonQuery();

            using var wrapperTx = new ImmediateTransaction(connection);
            try
            {
                T result = action(connection, wrapperTx.Inner);
                wrapperTx.Commit();
                return result;
            }
            catch
            {
                wrapperTx.Rollback();
                throw;
            }
        }
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            PRAGMA synchronous = FULL;
            """;
        pragma.ExecuteNonQuery();
        return conn;
    }

    public MutationResult SaveRecord(SaveRecordRequest request, DateTimeOffset nowUtc, TimeZoneInfo displayZone)
    {
        if (!RecordRules.HasValidText(request.Title, request.Body))
        {
            throw new AppException(ErrorCodes.Validation, "Title or body must not be empty.", new[] { request.Id });
        }

        if (!RecordRules.IsValidDueSpec(request.Own.Due))
        {
            throw new AppException(ErrorCodes.Validation, "Invalid deadline specification.", new[] { request.Id });
        }

        return Write((conn, _) =>
        {
            long nowMs = nowUtc.ToUniversalTime().ToUnixTimeMilliseconds();
            var existing = LoadRawRecord(conn, request.Id);

            if (request.ExpectedVersion == 0)
            {
                if (existing is not null)
                {
                    throw new AppException(ErrorCodes.AlreadyExists, "Record ID already exists.", new[] { request.Id });
                }

                EffectiveFields? parentEffective = null;
                if (request.ParentId is { } parentId)
                {
                    if (request.Kind != RecordKind.Task || parentId == request.Id)
                    {
                        throw new AppException(ErrorCodes.Validation, "Invalid child task hierarchy.", new[] { request.Id });
                    }

                    var parentRaw = LoadRawRecord(conn, parentId)
                        ?? throw new AppException(ErrorCodes.Validation, "Parent task does not exist.", new[] { request.Id, parentId });

                    if (parentRaw.Kind != RecordKind.Task || parentRaw.ParentId is not null)
                    {
                        throw new AppException(ErrorCodes.Validation, "Parent must be a root task.", new[] { request.Id, parentId });
                    }

                    if (parentRaw.Status != RecordStatus.Active || parentRaw.DeletedUtcMs.HasValue)
                    {
                        throw new AppException(ErrorCodes.ParentRequired, "Parent task must be active.", new[] { request.Id, parentId });
                    }

                    parentEffective = LoadEffectiveFields(conn, parentId);
                }
                else if (request.Inherit != InheritField.None)
                {
                    throw new AppException(ErrorCodes.Validation, "Root record cannot inherit fields.", new[] { request.Id });
                }

                var projected = ProjectionBuilder.BuildActive(
                    request.Id,
                    request.Own,
                    parentEffective,
                    request.Inherit,
                    recordVersion: 1,
                    displayZone);

                InsertRawRecord(conn, request, nowMs, version: 1);
                UpsertEffectiveRecord(conn, projected);

                var createdView = LoadRecordView(conn, request.Id)!;
                return new MutationResult(new[] { createdView });
            }
            else
            {
                if (existing is null)
                {
                    throw new AppException(ErrorCodes.NotFound, "Record not found.", new[] { request.Id });
                }

                if (existing.Version != request.ExpectedVersion)
                {
                    throw new AppException(ErrorCodes.Conflict, "Record version conflict.", new[] { request.Id });
                }

                if (existing.Status != RecordStatus.Active || existing.DeletedUtcMs.HasValue)
                {
                    throw new AppException(ErrorCodes.Validation, "Closed or trashed records cannot be edited directly.", new[] { request.Id });
                }

                if (existing.Kind != request.Kind || existing.ParentId != request.ParentId)
                {
                    throw new AppException(ErrorCodes.Validation, "Record kind and parent cannot be modified in V1.", new[] { request.Id });
                }

                EffectiveFields? parentEffective = null;
                if (request.ParentId is { } parentId)
                {
                    parentEffective = LoadEffectiveFields(conn, parentId);
                }
                else if (request.Inherit != InheritField.None)
                {
                    throw new AppException(ErrorCodes.Validation, "Root record cannot inherit fields.", new[] { request.Id });
                }

                long newVersion = existing.Version + 1;
                var projectedSelf = ProjectionBuilder.BuildActive(
                    request.Id,
                    request.Own,
                    parentEffective,
                    request.Inherit,
                    newVersion,
                    displayZone);

                UpdateRawRecord(conn, request, nowMs, newVersion);
                UpsertEffectiveRecord(conn, projectedSelf);
                DeleteClaimedRemindersIfDueChanged(conn, request.Id, existing.EffectiveDue, projectedSelf.Effective.Due);

                var changedIds = new List<Guid> { request.Id };

                if (request.ParentId is null && request.Kind == RecordKind.Task)
                {
                    var activeChildren = LoadActiveChildren(conn, request.Id);
                    var conflicts = new List<Guid>();
                    var childProjections = new List<(RawRecordRow Child, ProjectedRecord Projection)>();

                    foreach (var child in activeChildren)
                    {
                        if (child.Inherit == InheritField.None)
                        {
                            continue;
                        }

                        try
                        {
                            var childProjected = ProjectionBuilder.BuildActive(
                                child.Id,
                                child.Own,
                                projectedSelf.Effective,
                                child.Inherit,
                                child.Version + 1,
                                displayZone);
                            childProjections.Add((child, childProjected));
                        }
                        catch (AppException ex) when (ex.Code == ErrorCodes.Validation)
                        {
                            conflicts.Add(child.Id);
                        }
                    }

                    if (conflicts.Count > 0)
                    {
                        throw new AppException(
                            ErrorCodes.Validation,
                            "Parent update causes invalid start/deadline dates in one or more subtasks.",
                            conflicts);
                    }

                    foreach (var (child, childProjection) in childProjections)
                    {
                        TouchRecordVersion(conn, child.Id, childProjection.RecordVersion, nowMs);
                        UpsertEffectiveRecord(conn, childProjection);
                        DeleteClaimedRemindersIfDueChanged(conn, child.Id, child.EffectiveDue, childProjection.Effective.Due);
                        changedIds.Add(child.Id);
                    }
                }

                var views = changedIds.Select(id => LoadRecordView(conn, id)!).ToArray();
                return new MutationResult(views);
            }
        });
    }

    public MutationResult CloseRecord(Guid id, long expectedVersion, bool includeChildren, DateTimeOffset nowUtc, TimeZoneInfo displayZone)
    {
        return Write((conn, _) =>
        {
            var target = LoadRawRecord(conn, id)
                ?? throw new AppException(ErrorCodes.NotFound, "Record not found.", new[] { id });

            if (target.Version != expectedVersion)
            {
                throw new AppException(ErrorCodes.Conflict, "Record version conflict.", new[] { id });
            }

            if (target.DeletedUtcMs.HasValue)
            {
                throw new AppException(ErrorCodes.Validation, "Cannot complete a trashed record.", new[] { id });
            }

            if (target.Status == RecordStatus.Closed)
            {
                return new MutationResult(new[] { LoadRecordView(conn, id)! });
            }

            long nowMs = nowUtc.ToUniversalTime().ToUnixTimeMilliseconds();
            var changedIds = new List<Guid>();

            if (target.ParentId is null && target.Kind == RecordKind.Task)
            {
                var activeChildren = LoadActiveChildren(conn, id);
                if (activeChildren.Count > 0)
                {
                    if (!includeChildren)
                    {
                        throw new AppException(
                            ErrorCodes.ActiveChildren,
                            "Active subtasks exist; confirm completing all subtasks together.",
                            activeChildren.Select(c => c.Id).ToArray());
                    }

                    foreach (var child in activeChildren)
                    {
                        CloseSingleRecordInternal(conn, child, nowMs, displayZone);
                        changedIds.Add(child.Id);
                    }
                }
            }

            CloseSingleRecordInternal(conn, target, nowMs, displayZone);
            changedIds.Add(id);

            var views = changedIds.Select(cid => LoadRecordView(conn, cid)!).ToArray();
            return new MutationResult(views);
        });
    }

    private static void CloseSingleRecordInternal(
        SqliteConnection conn,
        RawRecordRow row,
        long nowMs,
        TimeZoneInfo displayZone)
    {
        var effective = LoadEffectiveFields(conn, row.Id);
        string snapshotJson = ProjectionBuilder.SerializeSnapshot(effective);
        long nextVersion = row.Version + 1;

        using (var snapCmd = conn.CreateCommand())
        {
            snapCmd.CommandText = """
                INSERT INTO completion_snapshots (record_id, payload_json, closed_utc_ms)
                VALUES ($id, $json, $closed)
                ON CONFLICT(record_id) DO UPDATE SET payload_json = excluded.payload_json, closed_utc_ms = excluded.closed_utc_ms;
                """;
            snapCmd.Parameters.AddWithValue("$id", row.Id.ToString("D"));
            snapCmd.Parameters.AddWithValue("$json", snapshotJson);
            snapCmd.Parameters.AddWithValue("$closed", nowMs);
            snapCmd.ExecuteNonQuery();
        }

        using (var updCmd = conn.CreateCommand())
        {
            updCmd.CommandText = """
                UPDATE records
                SET status = 1, closed_utc_ms = $closed, updated_utc_ms = $now, version = $ver
                WHERE id = $id;
                """;
            updCmd.Parameters.AddWithValue("$closed", nowMs);
            updCmd.Parameters.AddWithValue("$now", nowMs);
            updCmd.Parameters.AddWithValue("$ver", nextVersion);
            updCmd.Parameters.AddWithValue("$id", row.Id.ToString("D"));
            updCmd.ExecuteNonQuery();
        }

        var frozenProjection = ProjectionBuilder.BuildFromSnapshot(row.Id, snapshotJson, nextVersion, displayZone);
        UpsertEffectiveRecord(conn, frozenProjection);
        DeleteClaimedReminders(conn, row.Id);
    }

    public MutationResult ReopenRecord(Guid id, long expectedVersion, bool includeParent, DateTimeOffset nowUtc, TimeZoneInfo displayZone)
    {
        return Write((conn, _) =>
        {
            var target = LoadRawRecord(conn, id)
                ?? throw new AppException(ErrorCodes.NotFound, "Record not found.", new[] { id });

            if (target.Version != expectedVersion)
            {
                throw new AppException(ErrorCodes.Conflict, "Record version conflict.", new[] { id });
            }

            if (target.DeletedUtcMs.HasValue)
            {
                throw new AppException(ErrorCodes.Validation, "Restore record from trash before reopening.", new[] { id });
            }

            if (target.Status == RecordStatus.Active)
            {
                return new MutationResult(new[] { LoadRecordView(conn, id)! });
            }

            long nowMs = nowUtc.ToUniversalTime().ToUnixTimeMilliseconds();
            var changedIds = new List<Guid>();

            EffectiveFields? parentEffective = null;
            if (target.ParentId is { } parentId)
            {
                var parent = LoadRawRecord(conn, parentId)
                    ?? throw new AppException(ErrorCodes.NotFound, "Parent task not found.", new[] { parentId });

                if (parent.DeletedUtcMs.HasValue)
                {
                    throw new AppException(ErrorCodes.ParentRequired, "Parent task is in trash.", new[] { parentId });
                }

                if (parent.Status == RecordStatus.Closed)
                {
                    if (!includeParent)
                    {
                        throw new AppException(ErrorCodes.ParentRequired, "Parent task is completed; reopen parent together.", new[] { parentId });
                    }

                    ReopenSingleRecordInternal(conn, parent, null, nowMs, displayZone);
                    changedIds.Add(parentId);
                }

                parentEffective = LoadEffectiveFields(conn, parentId);
            }

            ReopenSingleRecordInternal(conn, target, parentEffective, nowMs, displayZone);
            changedIds.Add(id);

            var views = changedIds.Select(cid => LoadRecordView(conn, cid)!).ToArray();
            return new MutationResult(views);
        });
    }

    private static void ReopenSingleRecordInternal(
        SqliteConnection conn,
        RawRecordRow row,
        EffectiveFields? parentEffective,
        long nowMs,
        TimeZoneInfo displayZone)
    {
        long nextVersion = row.Version + 1;
        var projected = ProjectionBuilder.BuildActive(
            row.Id,
            row.Own,
            parentEffective,
            row.Inherit,
            nextVersion,
            displayZone);

        using (var delSnap = conn.CreateCommand())
        {
            delSnap.CommandText = "DELETE FROM completion_snapshots WHERE record_id = $id;";
            delSnap.Parameters.AddWithValue("$id", row.Id.ToString("D"));
            delSnap.ExecuteNonQuery();
        }

        using (var updCmd = conn.CreateCommand())
        {
            updCmd.CommandText = """
                UPDATE records
                SET status = 0, closed_utc_ms = NULL, updated_utc_ms = $now, version = $ver
                WHERE id = $id;
                """;
            updCmd.Parameters.AddWithValue("$now", nowMs);
            updCmd.Parameters.AddWithValue("$ver", nextVersion);
            updCmd.Parameters.AddWithValue("$id", row.Id.ToString("D"));
            updCmd.ExecuteNonQuery();
        }

        UpsertEffectiveRecord(conn, projected);
    }

    public MutationResult TrashRecord(Guid id, long expectedVersion, DateTimeOffset nowUtc, TimeZoneInfo displayZone)
    {
        return Write((conn, _) =>
        {
            var target = LoadRawRecord(conn, id)
                ?? throw new AppException(ErrorCodes.NotFound, "Record not found.", new[] { id });

            if (target.Version != expectedVersion)
            {
                throw new AppException(ErrorCodes.Conflict, "Record version conflict.", new[] { id });
            }

            if (target.DeletedUtcMs.HasValue)
            {
                return new MutationResult(new[] { LoadRecordView(conn, id)! });
            }

            long nowMs = nowUtc.ToUniversalTime().ToUnixTimeMilliseconds();
            string batchId = Guid.NewGuid().ToString("D");
            var changedIds = new List<Guid>();

            if (target.ParentId is null && target.Kind == RecordKind.Task)
            {
                var nonDeletedChildren = LoadNonDeletedChildren(conn, id);
                foreach (var child in nonDeletedChildren)
                {
                    MarkRecordTrashed(conn, child.Id, child.Version + 1, nowMs, batchId, displayZone);
                    changedIds.Add(child.Id);
                }
            }

            MarkRecordTrashed(conn, target.Id, target.Version + 1, nowMs, batchId, displayZone);
            changedIds.Add(id);

            var views = changedIds.Select(cid => LoadRecordView(conn, cid)!).ToArray();
            return new MutationResult(views);
        });
    }

    private static void MarkRecordTrashed(
        SqliteConnection conn,
        Guid id,
        long nextVersion,
        long nowMs,
        string batchId,
        TimeZoneInfo displayZone)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE records
                SET deleted_utc_ms = $del, deletion_batch_id = $batch, updated_utc_ms = $now, version = $ver
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$del", nowMs);
            cmd.Parameters.AddWithValue("$batch", batchId);
            cmd.Parameters.AddWithValue("$now", nowMs);
            cmd.Parameters.AddWithValue("$ver", nextVersion);
            cmd.Parameters.AddWithValue("$id", id.ToString("D"));
            cmd.ExecuteNonQuery();
        }

        using (var effCmd = conn.CreateCommand())
        {
            effCmd.CommandText = "UPDATE effective_records SET record_version = $ver, projection_zone = $zone WHERE record_id = $id;";
            effCmd.Parameters.AddWithValue("$ver", nextVersion);
            effCmd.Parameters.AddWithValue("$zone", displayZone.Id);
            effCmd.Parameters.AddWithValue("$id", id.ToString("D"));
            effCmd.ExecuteNonQuery();
        }

        DeleteClaimedReminders(conn, id);
    }

    public MutationResult RestoreRecord(Guid id, long expectedVersion, bool includeParent, DateTimeOffset nowUtc, TimeZoneInfo displayZone)
    {
        return Write((conn, _) =>
        {
            var target = LoadRawRecord(conn, id)
                ?? throw new AppException(ErrorCodes.NotFound, "Record not found.", new[] { id });

            if (target.Version != expectedVersion)
            {
                throw new AppException(ErrorCodes.Conflict, "Record version conflict.", new[] { id });
            }

            if (!target.DeletedUtcMs.HasValue)
            {
                return new MutationResult(new[] { LoadRecordView(conn, id)! });
            }

            long nowMs = nowUtc.ToUniversalTime().ToUnixTimeMilliseconds();
            var changedIds = new List<Guid>();

            if (target.ParentId is { } parentId)
            {
                var parent = LoadRawRecord(conn, parentId)
                    ?? throw new AppException(ErrorCodes.NotFound, "Parent task not found.", new[] { parentId });

                if (parent.DeletedUtcMs.HasValue)
                {
                    if (!includeParent)
                    {
                        throw new AppException(ErrorCodes.ParentRequired, "Parent task is in trash; restore parent first.", new[] { parentId });
                    }

                    RestoreSingleRecordInternal(conn, parent, null, nowMs, displayZone);
                    changedIds.Add(parentId);
                    parent = LoadRawRecord(conn, parentId)!;
                }

                if (target.Status == RecordStatus.Active && parent.Status == RecordStatus.Closed)
                {
                    if (!includeParent)
                    {
                        throw new AppException(ErrorCodes.ParentRequired, "Parent task is closed while child is active; reopen parent together.", new[] { parentId });
                    }

                    ReopenSingleRecordInternal(conn, parent, null, nowMs, displayZone);
                    if (!changedIds.Contains(parentId))
                    {
                        changedIds.Add(parentId);
                    }
                }

                var parentEffective = LoadEffectiveFields(conn, parentId);
                RestoreSingleRecordInternal(conn, target, parentEffective, nowMs, displayZone);
                changedIds.Add(id);
            }
            else
            {
                string? batchId = target.DeletionBatchId;
                RestoreSingleRecordInternal(conn, target, null, nowMs, displayZone);
                changedIds.Add(id);

                if (target.Kind == RecordKind.Task && !string.IsNullOrWhiteSpace(batchId))
                {
                    var parentEffective = LoadEffectiveFields(conn, id);
                    var batchChildren = LoadChildrenByDeletionBatch(conn, id, batchId);
                    foreach (var child in batchChildren)
                    {
                        RestoreSingleRecordInternal(conn, child, parentEffective, nowMs, displayZone);
                        changedIds.Add(child.Id);
                    }
                }
            }

            var views = changedIds.Select(cid => LoadRecordView(conn, cid)!).ToArray();
            return new MutationResult(views);
        });
    }

    private static void RestoreSingleRecordInternal(
        SqliteConnection conn,
        RawRecordRow row,
        EffectiveFields? parentEffective,
        long nowMs,
        TimeZoneInfo displayZone)
    {
        long nextVersion = row.Version + 1;
        ProjectedRecord projected;
        if (row.Status == RecordStatus.Active)
        {
            projected = ProjectionBuilder.BuildActive(row.Id, row.Own, parentEffective, row.Inherit, nextVersion, displayZone);
        }
        else
        {
            string? snapshotJson = LoadSnapshotJson(conn, row.Id);
            projected = snapshotJson is not null
                ? ProjectionBuilder.BuildFromSnapshot(row.Id, snapshotJson, nextVersion, displayZone)
                : ProjectionBuilder.BuildActive(row.Id, row.Own, parentEffective, row.Inherit, nextVersion, displayZone);
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE records
                SET deleted_utc_ms = NULL, deletion_batch_id = NULL, updated_utc_ms = $now, version = $ver
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$now", nowMs);
            cmd.Parameters.AddWithValue("$ver", nextVersion);
            cmd.Parameters.AddWithValue("$id", row.Id.ToString("D"));
            cmd.ExecuteNonQuery();
        }

        UpsertEffectiveRecord(conn, projected);
    }

    public RecordView? GetRecord(Guid id)
    {
        return Read(conn => LoadRecordView(conn, id));
    }

    public Page<RecordView> SearchRecords(RecordQuery query)
    {
        int skip = Math.Max(0, query.Skip);
        int take = Math.Clamp(query.Take, 1, 200);
        string q = (query.Text ?? string.Empty).Trim();

        return Read(conn =>
        {
            string whereClause = """
                WHERE r.kind = $kind
                  AND (($trash = 0 AND r.deleted_utc_ms IS NULL) OR ($trash = 1 AND r.deleted_utc_ms IS NOT NULL))
                  AND ($all_status = 1 OR r.status = $status)
                  AND ($q = ''
                       OR instr(r.title, $q) > 0
                       OR instr(r.body, $q) > 0
                       OR instr(coalesce(e.publisher, ''), $q) > 0
                       OR instr(coalesce(e.assignee, ''), $q) > 0)
                """;

            string orderByClause = BuildOrderByClause(query.Sort);

            int total;
            using (var countCmd = conn.CreateCommand())
            {
                countCmd.CommandText = $"""
                    SELECT count(*)
                    FROM records r
                    JOIN effective_records e ON e.record_id = r.id
                    {whereClause};
                    """;
                BindSearchParameters(countCmd, query, q);
                total = Convert.ToInt32(countCmd.ExecuteScalar() ?? 0);
            }

            var items = new List<RecordView>();
            using (var listCmd = conn.CreateCommand())
            {
                listCmd.CommandText = $"""
                    SELECT {SelectRecordViewColumns}
                    FROM records r
                    JOIN effective_records e ON e.record_id = r.id
                    {whereClause}
                    {orderByClause}
                    LIMIT $take OFFSET $skip;
                    """;
                BindSearchParameters(listCmd, query, q);
                listCmd.Parameters.AddWithValue("$take", take);
                listCmd.Parameters.AddWithValue("$skip", skip);

                using var reader = listCmd.ExecuteReader();
                while (reader.Read())
                {
                    items.Add(MapRecordView(reader));
                }
            }

            return new Page<RecordView>(items, total);
        });
    }

    private static void BindSearchParameters(SqliteCommand cmd, RecordQuery query, string q)
    {
        cmd.Parameters.AddWithValue("$kind", (int)query.Kind);
        cmd.Parameters.AddWithValue("$trash", query.Trash ? 1 : 0);
        cmd.Parameters.AddWithValue("$all_status", query.Status.HasValue ? 0 : 1);
        cmd.Parameters.AddWithValue("$status", query.Status.HasValue ? (int)query.Status.Value : 0);
        cmd.Parameters.AddWithValue("$q", q);
    }

    private static string BuildOrderByClause(IReadOnlyList<SortTerm>? sortTerms)
    {
        if (sortTerms is null || sortTerms.Count == 0)
        {
            return """
                ORDER BY e.priority ASC,
                         (e.due_boundary_utc_ms IS NULL) ASC,
                         e.due_boundary_utc_ms ASC,
                         r.created_utc_ms ASC,
                         r.id ASC
                """;
        }

        var parts = new List<string>();
        var seen = new HashSet<SortKey>();

        foreach (var term in sortTerms)
        {
            if (!seen.Add(term.Key) || seen.Count > 3)
            {
                continue;
            }

            string dir = term.Descending ? "DESC" : "ASC";
            switch (term.Key)
            {
                case SortKey.Priority:
                    parts.Add($"e.priority {dir}");
                    break;
                case SortKey.Deadline:
                    // Null deadlines always stay at the end regardless of ASC or DESC
                    parts.Add("(e.due_boundary_utc_ms IS NULL) ASC");
                    parts.Add($"e.due_boundary_utc_ms {dir}");
                    break;
                case SortKey.Created:
                    parts.Add($"r.created_utc_ms {dir}");
                    break;
            }
        }

        parts.Add("r.id ASC");
        return "ORDER BY " + string.Join(", ", parts);
    }

    public IReadOnlyList<CalendarEntry> QueryCalendar(CalendarQuery query, TimeZoneInfo displayZone)
    {
        var monthStart = new DateOnly(query.Year, query.Month, 1);
        var nextMonthStart = monthStart.AddMonths(1);
        long startUtcMs = DeadlineRules.ResolveLocalMidnightUtc(monthStart, displayZone).ToUnixTimeMilliseconds();
        long endUtcMs = DeadlineRules.ResolveLocalMidnightUtc(nextMonthStart, displayZone).ToUnixTimeMilliseconds();

        return Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT {SelectRecordViewColumns}
                FROM records r
                JOIN effective_records e ON e.record_id = r.id
                WHERE r.kind = $kind
                  AND r.deleted_utc_ms IS NULL
                  AND ($all_status = 1 OR r.status = $status)
                ORDER BY e.priority ASC,
                         (e.due_boundary_utc_ms IS NULL) ASC,
                         e.due_boundary_utc_ms ASC,
                         r.created_utc_ms ASC,
                         r.id ASC;
                """;
            cmd.Parameters.AddWithValue("$kind", (int)query.Kind);
            cmd.Parameters.AddWithValue("$all_status", query.Status.HasValue ? 0 : 1);
            cmd.Parameters.AddWithValue("$status", query.Status.HasValue ? (int)query.Status.Value : 0);

            var entries = new List<CalendarEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var view = MapRecordView(reader);
                DateOnly? day = ResolveCalendarDay(view, query.Axis, displayZone, monthStart, nextMonthStart, startUtcMs, endUtcMs, out bool include);
                if (include)
                {
                    entries.Add(new CalendarEntry(day, view));
                }
            }

            return entries;
        });
    }

    private static DateOnly? ResolveCalendarDay(
        RecordView view,
        CalendarAxis axis,
        TimeZoneInfo displayZone,
        DateOnly monthStart,
        DateOnly nextMonthStart,
        long startUtcMs,
        long endUtcMs,
        out bool include)
    {
        switch (axis)
        {
            case CalendarAxis.Due:
                if (view.Effective.Due.Kind == DueKind.None)
                {
                    include = true;
                    return null;
                }
                if (view.Effective.Due.Kind == DueKind.Date && view.Effective.Due.Date is { } dueDate)
                {
                    include = dueDate >= monthStart && dueDate < nextMonthStart;
                    return dueDate;
                }
                if (view.Effective.Due.Kind == DueKind.Instant && view.Effective.Due.Instant is { } dueInstant)
                {
                    long ms = dueInstant.ToUniversalTime().ToUnixTimeMilliseconds();
                    if (ms >= startUtcMs && ms < endUtcMs)
                    {
                        var localDt = TimeZoneInfo.ConvertTime(dueInstant, displayZone);
                        include = true;
                        return DateOnly.FromDateTime(localDt.DateTime);
                    }
                    include = false;
                    return null;
                }
                include = false;
                return null;

            case CalendarAxis.Start:
                if (view.Effective.Start is null)
                {
                    include = true;
                    return null;
                }
                return MatchTimestampInMonth(view.Effective.Start.Value, displayZone, startUtcMs, endUtcMs, out include);

            case CalendarAxis.Created:
                return MatchTimestampInMonth(view.CreatedAt, displayZone, startUtcMs, endUtcMs, out include);

            case CalendarAxis.Closed:
                if (view.ClosedAt is null)
                {
                    include = true;
                    return null;
                }
                return MatchTimestampInMonth(view.ClosedAt.Value, displayZone, startUtcMs, endUtcMs, out include);

            default:
                include = false;
                return null;
        }
    }

    private static DateOnly? MatchTimestampInMonth(
        DateTimeOffset timestamp,
        TimeZoneInfo displayZone,
        long startUtcMs,
        long endUtcMs,
        out bool include)
    {
        long ms = timestamp.ToUniversalTime().ToUnixTimeMilliseconds();
        if (ms >= startUtcMs && ms < endUtcMs)
        {
            var localDt = TimeZoneInfo.ConvertTime(timestamp, displayZone);
            include = true;
            return DateOnly.FromDateTime(localDt.DateTime);
        }

        include = false;
        return null;
    }

    public void SaveDraft(string editorId, Guid? recordId, long? baseVersion, string title, string body, DateTimeOffset nowUtc)
    {
        Write((conn, _) =>
        {
            string payloadJson = JsonSerializer.Serialize(new DraftPayload(title ?? string.Empty, body ?? string.Empty));
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO drafts (editor_id, record_id, base_version, payload_json, updated_utc_ms)
                VALUES ($editor, $rec, $ver, $json, $now)
                ON CONFLICT(editor_id) DO UPDATE SET
                    record_id = excluded.record_id,
                    base_version = excluded.base_version,
                    payload_json = excluded.payload_json,
                    updated_utc_ms = excluded.updated_utc_ms;
                """;
            cmd.Parameters.AddWithValue("$editor", editorId);
            cmd.Parameters.AddWithValue("$rec", recordId.HasValue ? recordId.Value.ToString("D") : DBNull.Value);
            cmd.Parameters.AddWithValue("$ver", baseVersion.HasValue ? baseVersion.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$json", payloadJson);
            cmd.Parameters.AddWithValue("$now", nowUtc.ToUniversalTime().ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
            return 0;
        });
    }

    public DraftView? GetDraft(string editorId)
    {
        return Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT editor_id, record_id, base_version, payload_json, updated_utc_ms FROM drafts WHERE editor_id = $editor;";
            cmd.Parameters.AddWithValue("$editor", editorId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            Guid? recordId = reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1));
            long? baseVer = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            var payload = JsonSerializer.Deserialize<DraftPayload>(reader.GetString(3))
                ?? new DraftPayload(string.Empty, string.Empty);
            var updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4));

            return new DraftView(reader.GetString(0), recordId, baseVer, payload.Title, payload.Body, updatedAt);
        });
    }

    public void DeleteDraft(string editorId)
    {
        Write((conn, _) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM drafts WHERE editor_id = $editor;";
            cmd.Parameters.AddWithValue("$editor", editorId);
            cmd.ExecuteNonQuery();
            return 0;
        });
    }

    public void ReprojectAll(TimeZoneInfo displayZone)
    {
        Write((conn, _) =>
        {
            var allRecords = new List<RawRecordRow>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT r.id, r.parent_id, r.kind, r.title, r.body, r.status,
                           r.publisher, r.assignee, r.priority, r.start_utc_ms,
                           r.due_kind, r.due_date, r.due_utc_ms, r.due_zone, r.attachment_root_id,
                           r.inherit_mask, r.created_utc_ms, r.updated_utc_ms, r.closed_utc_ms,
                           r.deleted_utc_ms, r.deletion_batch_id, r.version,
                           e.due_kind, e.due_date, e.due_utc_ms, e.due_zone
                    FROM records r
                    LEFT JOIN effective_records e ON e.record_id = r.id
                    ORDER BY (r.parent_id IS NOT NULL) ASC, r.created_utc_ms ASC;
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    allRecords.Add(MapRawRecordRow(reader));
                }
            }

            var rootEffectives = new Dictionary<Guid, EffectiveFields>();
            foreach (var rec in allRecords)
            {
                ProjectedRecord projected;
                if (rec.Status == RecordStatus.Closed)
                {
                    string? snap = LoadSnapshotJson(conn, rec.Id);
                    projected = snap is not null
                        ? ProjectionBuilder.BuildFromSnapshot(rec.Id, snap, rec.Version, displayZone)
                        : ProjectionBuilder.BuildActive(rec.Id, rec.Own, null, InheritField.None, rec.Version, displayZone);
                }
                else
                {
                    EffectiveFields? parentEff = rec.ParentId.HasValue && rootEffectives.TryGetValue(rec.ParentId.Value, out var pe)
                        ? pe
                        : null;
                    projected = ProjectionBuilder.BuildActive(rec.Id, rec.Own, parentEff, rec.Inherit, rec.Version, displayZone);
                }

                UpsertEffectiveRecord(conn, projected);
                if (rec.ParentId is null)
                {
                    rootEffectives[rec.Id] = projected.Effective;
                }
            }

            return 0;
        });
    }

    private const string SelectRecordViewColumns = """
        r.id, r.parent_id, r.kind, r.title, r.body, r.status,
        r.publisher, r.assignee, r.priority, r.start_utc_ms,
        r.due_kind, r.due_date, r.due_utc_ms, r.due_zone, r.attachment_root_id,
        r.inherit_mask, r.created_utc_ms, r.closed_utc_ms, r.deleted_utc_ms, r.version,
        e.publisher, e.assignee, e.priority, e.start_utc_ms,
        e.due_kind, e.due_date, e.due_utc_ms, e.due_zone, e.attachment_root_id
        """;

    private static RecordView? LoadRecordView(SqliteConnection conn, Guid id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {SelectRecordViewColumns}
            FROM records r
            JOIN effective_records e ON e.record_id = r.id
            WHERE r.id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapRecordView(reader) : null;
    }

    private static RecordView MapRecordView(SqliteDataReader r)
    {
        Guid id = Guid.Parse(r.GetString(0));
        Guid? parentId = r.IsDBNull(1) ? null : Guid.Parse(r.GetString(1));
        var kind = (RecordKind)r.GetInt32(2);
        string title = r.GetString(3);
        string body = r.GetString(4);
        var status = (RecordStatus)r.GetInt32(5);

        var own = ReadEffectiveFields(r, 6);
        var inherit = (InheritField)r.GetInt32(15);
        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(16));
        DateTimeOffset? closedAt = r.IsDBNull(17) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(17));
        DateTimeOffset? deletedAt = r.IsDBNull(18) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(18));
        long version = r.GetInt64(19);

        var effective = ReadEffectiveFields(r, 20);

        return new RecordView(
            id,
            parentId,
            kind,
            title,
            body,
            status,
            own,
            effective,
            inherit,
            version,
            createdAt,
            closedAt,
            deletedAt);
    }

    private static EffectiveFields ReadEffectiveFields(SqliteDataReader r, int offset)
    {
        string? publisher = r.IsDBNull(offset) ? null : r.GetString(offset);
        string? assignee = r.IsDBNull(offset + 1) ? null : r.GetString(offset + 1);
        var priority = (Priority)r.GetInt32(offset + 2);
        DateTimeOffset? start = r.IsDBNull(offset + 3) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(offset + 3));
        var dueKind = (DueKind)r.GetInt32(offset + 4);
        DateOnly? dueDate = r.IsDBNull(offset + 5) ? null : DateOnly.ParseExact(r.GetString(offset + 5), "yyyy-MM-dd");
        DateTimeOffset? dueInstant = r.IsDBNull(offset + 6) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(offset + 6));
        string? dueZone = r.IsDBNull(offset + 7) ? null : r.GetString(offset + 7);
        Guid? rootId = r.IsDBNull(offset + 8) ? null : Guid.Parse(r.GetString(offset + 8));

        return new EffectiveFields(
            publisher,
            assignee,
            priority,
            start,
            new DueSpec(dueKind, dueDate, dueInstant, dueZone),
            rootId);
    }

    private static void InsertRawRecord(SqliteConnection conn, SaveRecordRequest req, long nowMs, long version)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO records (
                id, parent_id, kind, title, body, status,
                publisher, assignee, priority, start_utc_ms,
                due_kind, due_date, due_utc_ms, due_zone,
                attachment_root_id, inherit_mask,
                created_utc_ms, updated_utc_ms, version
            ) VALUES (
                $id, $parent_id, $kind, $title, $body, 0,
                $pub, $ass, $prio, $start,
                $due_kind, $due_date, $due_utc, $due_zone,
                $root_id, $mask,
                $now, $now, $ver
            );
            """;
        BindRecordParameters(cmd, req, nowMs, version);
        cmd.ExecuteNonQuery();
    }

    private static void UpdateRawRecord(SqliteConnection conn, SaveRecordRequest req, long nowMs, long version)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE records SET
                title = $title,
                body = $body,
                publisher = $pub,
                assignee = $ass,
                priority = $prio,
                start_utc_ms = $start,
                due_kind = $due_kind,
                due_date = $due_date,
                due_utc_ms = $due_utc,
                due_zone = $due_zone,
                attachment_root_id = $root_id,
                inherit_mask = $mask,
                updated_utc_ms = $now,
                version = $ver
            WHERE id = $id;
            """;
        BindRecordParameters(cmd, req, nowMs, version);
        cmd.ExecuteNonQuery();
    }

    private static void BindRecordParameters(SqliteCommand cmd, SaveRecordRequest req, long nowMs, long version)
    {
        cmd.Parameters.AddWithValue("$id", req.Id.ToString("D"));
        cmd.Parameters.AddWithValue("$parent_id", req.ParentId.HasValue ? req.ParentId.Value.ToString("D") : DBNull.Value);
        cmd.Parameters.AddWithValue("$kind", (int)req.Kind);
        cmd.Parameters.AddWithValue("$title", req.Title ?? string.Empty);
        cmd.Parameters.AddWithValue("$body", req.Body ?? string.Empty);
        cmd.Parameters.AddWithValue("$pub", (object?)req.Own.Publisher ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ass", (object?)req.Own.Assignee ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$prio", (int)req.Own.Priority);
        cmd.Parameters.AddWithValue("$start", req.Own.Start.HasValue ? req.Own.Start.Value.ToUniversalTime().ToUnixTimeMilliseconds() : DBNull.Value);
        cmd.Parameters.AddWithValue("$due_kind", (int)req.Own.Due.Kind);
        cmd.Parameters.AddWithValue("$due_date", req.Own.Due.Date.HasValue ? req.Own.Due.Date.Value.ToString("yyyy-MM-dd") : DBNull.Value);
        cmd.Parameters.AddWithValue("$due_utc", req.Own.Due.Instant.HasValue ? req.Own.Due.Instant.Value.ToUniversalTime().ToUnixTimeMilliseconds() : DBNull.Value);
        cmd.Parameters.AddWithValue("$due_zone", (object?)req.Own.Due.ZoneId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$root_id", req.Own.AttachmentRootId.HasValue ? req.Own.AttachmentRootId.Value.ToString("D") : DBNull.Value);
        cmd.Parameters.AddWithValue("$mask", (int)req.Inherit);
        cmd.Parameters.AddWithValue("$now", nowMs);
        cmd.Parameters.AddWithValue("$ver", version);
    }

    private static void UpsertEffectiveRecord(SqliteConnection conn, ProjectedRecord p)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO effective_records (
                record_id, publisher, assignee, priority, start_utc_ms,
                due_kind, due_date, due_utc_ms, due_zone,
                attachment_root_id, due_boundary_utc_ms, projection_zone, record_version
            ) VALUES (
                $id, $pub, $ass, $prio, $start,
                $due_kind, $due_date, $due_utc, $due_zone,
                $root_id, $boundary, $proj_zone, $ver
            )
            ON CONFLICT(record_id) DO UPDATE SET
                publisher = excluded.publisher,
                assignee = excluded.assignee,
                priority = excluded.priority,
                start_utc_ms = excluded.start_utc_ms,
                due_kind = excluded.due_kind,
                due_date = excluded.due_date,
                due_utc_ms = excluded.due_utc_ms,
                due_zone = excluded.due_zone,
                attachment_root_id = excluded.attachment_root_id,
                due_boundary_utc_ms = excluded.due_boundary_utc_ms,
                projection_zone = excluded.projection_zone,
                record_version = excluded.record_version;
            """;
        cmd.Parameters.AddWithValue("$id", p.RecordId.ToString("D"));
        cmd.Parameters.AddWithValue("$pub", (object?)p.Effective.Publisher ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ass", (object?)p.Effective.Assignee ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$prio", (int)p.Effective.Priority);
        cmd.Parameters.AddWithValue("$start", p.Effective.Start.HasValue ? p.Effective.Start.Value.ToUniversalTime().ToUnixTimeMilliseconds() : DBNull.Value);
        cmd.Parameters.AddWithValue("$due_kind", (int)p.Effective.Due.Kind);
        cmd.Parameters.AddWithValue("$due_date", p.Effective.Due.Date.HasValue ? p.Effective.Due.Date.Value.ToString("yyyy-MM-dd") : DBNull.Value);
        cmd.Parameters.AddWithValue("$due_utc", p.Effective.Due.Instant.HasValue ? p.Effective.Due.Instant.Value.ToUniversalTime().ToUnixTimeMilliseconds() : DBNull.Value);
        cmd.Parameters.AddWithValue("$due_zone", (object?)p.Effective.Due.ZoneId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$root_id", p.Effective.AttachmentRootId.HasValue ? p.Effective.AttachmentRootId.Value.ToString("D") : DBNull.Value);
        cmd.Parameters.AddWithValue("$boundary", p.DueBoundaryUtcMs.HasValue ? p.DueBoundaryUtcMs.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$proj_zone", p.ProjectionZone);
        cmd.Parameters.AddWithValue("$ver", p.RecordVersion);
        cmd.ExecuteNonQuery();
    }

    private static void TouchRecordVersion(SqliteConnection conn, Guid id, long newVersion, long nowMs)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE records SET updated_utc_ms = $now, version = $ver WHERE id = $id;";
        cmd.Parameters.AddWithValue("$now", nowMs);
        cmd.Parameters.AddWithValue("$ver", newVersion);
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        cmd.ExecuteNonQuery();
    }

    private static void DeleteClaimedRemindersIfDueChanged(SqliteConnection conn, Guid id, DueSpec oldDue, DueSpec newDue)
    {
        if (DeadlineRules.ComputeFingerprint(oldDue) != DeadlineRules.ComputeFingerprint(newDue))
        {
            DeleteClaimedReminders(conn, id);
        }
    }

    private static void DeleteClaimedReminders(SqliteConnection conn, Guid id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM reminder_deliveries WHERE record_id = $id AND delivery_state = 'claimed';";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        cmd.ExecuteNonQuery();
    }

    private static EffectiveFields LoadEffectiveFields(SqliteConnection conn, Guid id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT publisher, assignee, priority, start_utc_ms,
                   due_kind, due_date, due_utc_ms, due_zone, attachment_root_id
            FROM effective_records
            WHERE record_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException($"Missing effective_records row for {id}.");
        }

        return ReadEffectiveFields(reader, 0);
    }

    private static string? LoadSnapshotJson(SqliteConnection conn, Guid id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload_json FROM completion_snapshots WHERE record_id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        return cmd.ExecuteScalar() as string;
    }

    private static RawRecordRow? LoadRawRecord(SqliteConnection conn, Guid id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.parent_id, r.kind, r.title, r.body, r.status,
                   r.publisher, r.assignee, r.priority, r.start_utc_ms,
                   r.due_kind, r.due_date, r.due_utc_ms, r.due_zone, r.attachment_root_id,
                   r.inherit_mask, r.created_utc_ms, r.updated_utc_ms, r.closed_utc_ms,
                   r.deleted_utc_ms, r.deletion_batch_id, r.version,
                   e.due_kind, e.due_date, e.due_utc_ms, e.due_zone
            FROM records r
            LEFT JOIN effective_records e ON e.record_id = r.id
            WHERE r.id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapRawRecordRow(reader) : null;
    }

    private static List<RawRecordRow> LoadActiveChildren(SqliteConnection conn, Guid parentId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.parent_id, r.kind, r.title, r.body, r.status,
                   r.publisher, r.assignee, r.priority, r.start_utc_ms,
                   r.due_kind, r.due_date, r.due_utc_ms, r.due_zone, r.attachment_root_id,
                   r.inherit_mask, r.created_utc_ms, r.updated_utc_ms, r.closed_utc_ms,
                   r.deleted_utc_ms, r.deletion_batch_id, r.version,
                   e.due_kind, e.due_date, e.due_utc_ms, e.due_zone
            FROM records r
            LEFT JOIN effective_records e ON e.record_id = r.id
            WHERE r.parent_id = $pid AND r.status = 0 AND r.deleted_utc_ms IS NULL
            ORDER BY r.created_utc_ms ASC, r.id ASC;
            """;
        cmd.Parameters.AddWithValue("$pid", parentId.ToString("D"));
        var list = new List<RawRecordRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(MapRawRecordRow(reader));
        }
        return list;
    }

    private static List<RawRecordRow> LoadNonDeletedChildren(SqliteConnection conn, Guid parentId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.parent_id, r.kind, r.title, r.body, r.status,
                   r.publisher, r.assignee, r.priority, r.start_utc_ms,
                   r.due_kind, r.due_date, r.due_utc_ms, r.due_zone, r.attachment_root_id,
                   r.inherit_mask, r.created_utc_ms, r.updated_utc_ms, r.closed_utc_ms,
                   r.deleted_utc_ms, r.deletion_batch_id, r.version,
                   e.due_kind, e.due_date, e.due_utc_ms, e.due_zone
            FROM records r
            LEFT JOIN effective_records e ON e.record_id = r.id
            WHERE r.parent_id = $pid AND r.deleted_utc_ms IS NULL
            ORDER BY r.created_utc_ms ASC, r.id ASC;
            """;
        cmd.Parameters.AddWithValue("$pid", parentId.ToString("D"));
        var list = new List<RawRecordRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(MapRawRecordRow(reader));
        }
        return list;
    }

    private static List<RawRecordRow> LoadChildrenByDeletionBatch(SqliteConnection conn, Guid parentId, string batchId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.parent_id, r.kind, r.title, r.body, r.status,
                   r.publisher, r.assignee, r.priority, r.start_utc_ms,
                   r.due_kind, r.due_date, r.due_utc_ms, r.due_zone, r.attachment_root_id,
                   r.inherit_mask, r.created_utc_ms, r.updated_utc_ms, r.closed_utc_ms,
                   r.deleted_utc_ms, r.deletion_batch_id, r.version,
                   e.due_kind, e.due_date, e.due_utc_ms, e.due_zone
            FROM records r
            LEFT JOIN effective_records e ON e.record_id = r.id
            WHERE r.parent_id = $pid AND r.deletion_batch_id = $batch
            ORDER BY r.created_utc_ms ASC, r.id ASC;
            """;
        cmd.Parameters.AddWithValue("$pid", parentId.ToString("D"));
        cmd.Parameters.AddWithValue("$batch", batchId);
        var list = new List<RawRecordRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(MapRawRecordRow(reader));
        }
        return list;
    }

    private static RawRecordRow MapRawRecordRow(SqliteDataReader r)
    {
        Guid id = Guid.Parse(r.GetString(0));
        Guid? parentId = r.IsDBNull(1) ? null : Guid.Parse(r.GetString(1));
        var kind = (RecordKind)r.GetInt32(2);
        string title = r.GetString(3);
        string body = r.GetString(4);
        var status = (RecordStatus)r.GetInt32(5);
        var own = ReadEffectiveFields(r, 6);
        var inherit = (InheritField)r.GetInt32(15);
        long createdMs = r.GetInt64(16);
        long updatedMs = r.GetInt64(17);
        long? closedMs = r.IsDBNull(18) ? null : r.GetInt64(18);
        long? deletedMs = r.IsDBNull(19) ? null : r.GetInt64(19);
        string? batchId = r.IsDBNull(20) ? null : r.GetString(20);
        long version = r.GetInt64(21);

        DueSpec effectiveDue = DueSpec.Empty;
        if (!r.IsDBNull(22))
        {
            var effKind = (DueKind)r.GetInt32(22);
            DateOnly? effDate = r.IsDBNull(23) ? null : DateOnly.ParseExact(r.GetString(23), "yyyy-MM-dd");
            DateTimeOffset? effUtc = r.IsDBNull(24) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(24));
            string? effZone = r.IsDBNull(25) ? null : r.GetString(25);
            effectiveDue = new DueSpec(effKind, effDate, effUtc, effZone);
        }

        return new RawRecordRow(
            id,
            parentId,
            kind,
            title,
            body,
            status,
            own,
            inherit,
            createdMs,
            updatedMs,
            closedMs,
            deletedMs,
            batchId,
            version,
            effectiveDue);
    }

    public void Dispose()
    {
        _keepAliveConnection?.Dispose();
        _keepAliveConnection = null;
    }

    private sealed record RawRecordRow(
        Guid Id,
        Guid? ParentId,
        RecordKind Kind,
        string Title,
        string Body,
        RecordStatus Status,
        EffectiveFields Own,
        InheritField Inherit,
        long CreatedUtcMs,
        long UpdatedUtcMs,
        long? ClosedUtcMs,
        long? DeletedUtcMs,
        string? DeletionBatchId,
        long Version,
        DueSpec EffectiveDue);

    private sealed record DraftPayload(string Title, string Body);

    private sealed class NonClosingConnectionWrapper
    {
        public SqliteConnection Connection { get; }
        public NonClosingConnectionWrapper(SqliteConnection connection) => Connection = connection;
    }

    private sealed class ImmediateTransaction : IDisposable
    {
        private readonly SqliteConnection _connection;
        private bool _completed;

        public SqliteTransaction? Inner => null!;

        public ImmediateTransaction(SqliteConnection connection)
        {
            _connection = connection;
        }

        public void Commit()
        {
            if (_completed) return;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "COMMIT;";
            cmd.ExecuteNonQuery();
            _completed = true;
        }

        public void Rollback()
        {
            if (_completed) return;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "ROLLBACK;";
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // Ignore rollback failure if transaction already aborted by SQLite trigger
            }
            _completed = true;
        }

        public void Dispose()
        {
            if (!_completed)
            {
                Rollback();
            }
        }
    }
}
