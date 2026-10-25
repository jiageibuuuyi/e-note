using DesktopNotes.Application.Ports;
using DesktopNotes.Domain;
using Microsoft.Data.Sqlite;

namespace DesktopNotes.Infrastructure.Persistence;

public sealed partial class SqliteStore : IReminderRepositoryPort
{
    public IReadOnlyList<ReminderCandidate> GetActiveReminderCandidates()
    {
        return Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT r.id, r.title, r.body,
                       e.due_kind, e.due_date, e.due_utc_ms, e.due_zone, e.due_boundary_utc_ms
                FROM records r
                JOIN effective_records e ON e.record_id = r.id
                WHERE r.kind = 0
                  AND r.status = 0
                  AND r.deleted_utc_ms IS NULL
                  AND e.due_kind <> 0
                  AND e.due_boundary_utc_ms IS NOT NULL
                ORDER BY e.due_boundary_utc_ms ASC, r.id ASC;
                """;
            var list = new List<ReminderCandidate>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                Guid id = Guid.Parse(reader.GetString(0));
                string title = reader.GetString(1);
                string body = reader.GetString(2);
                var dueKind = (DueKind)reader.GetInt32(3);
                DateOnly? dueDate = reader.IsDBNull(4) ? null : DateOnly.ParseExact(reader.GetString(4), "yyyy-MM-dd");
                DateTimeOffset? dueInstant = reader.IsDBNull(5) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5));
                string? dueZone = reader.IsDBNull(6) ? null : reader.GetString(6);
                long boundaryMs = reader.GetInt64(7);

                list.Add(new ReminderCandidate(
                    id,
                    title,
                    body,
                    new DueSpec(dueKind, dueDate, dueInstant, dueZone),
                    boundaryMs));
            }
            return list;
        });
    }

    public bool TryClaimDelivery(
        Guid recordId,
        string dueFingerprint,
        string stage,
        string occurrenceKey,
        DateTimeOffset nowUtc,
        out Guid deliveryId)
    {
        Guid newId = Guid.NewGuid();
        long nowMs = nowUtc.ToUniversalTime().ToUnixTimeMilliseconds();

        bool claimed = Write((conn, _) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO reminder_deliveries (
                    id, record_id, due_fingerprint, stage, occurrence_key, delivery_state, attempted_utc_ms
                ) VALUES (
                    $id, $rec, $fp, $stage, $occ, 'claimed', $now
                )
                ON CONFLICT(record_id, due_fingerprint, stage, occurrence_key) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("$id", newId.ToString("D"));
            cmd.Parameters.AddWithValue("$rec", recordId.ToString("D"));
            cmd.Parameters.AddWithValue("$fp", dueFingerprint);
            cmd.Parameters.AddWithValue("$stage", stage);
            cmd.Parameters.AddWithValue("$occ", occurrenceKey);
            cmd.Parameters.AddWithValue("$now", nowMs);
            int affected = cmd.ExecuteNonQuery();
            return affected > 0;
        });

        deliveryId = claimed ? newId : Guid.Empty;
        return claimed;
    }

    public void CompleteDelivery(Guid deliveryId, bool delivered, DateTimeOffset nowUtc)
    {
        Write((conn, _) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE reminder_deliveries
                SET delivery_state = $state, attempted_utc_ms = $now
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$state", delivered ? "delivered" : "failed");
            cmd.Parameters.AddWithValue("$now", nowUtc.ToUniversalTime().ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$id", deliveryId.ToString("D"));
            cmd.ExecuteNonQuery();
            return 0;
        });
    }

    public IReadOnlyList<ReminderDeliveryRecord> GetUncertainClaimedDeliveries()
    {
        return Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT d.id, d.record_id, d.due_fingerprint, d.stage, d.occurrence_key, d.delivery_state, d.attempted_utc_ms
                FROM reminder_deliveries d
                JOIN records r ON r.id = d.record_id
                WHERE d.delivery_state = 'claimed'
                  AND r.status = 0
                  AND r.deleted_utc_ms IS NULL
                ORDER BY d.attempted_utc_ms ASC;
                """;
            var list = new List<ReminderDeliveryRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(MapDelivery(reader));
            }
            return list;
        });
    }

    public void MarkClaimedDeliveriesResolved(IReadOnlyList<Guid> deliveryIds, DateTimeOffset nowUtc)
    {
        if (deliveryIds.Count == 0) return;

        Write((conn, _) =>
        {
            long nowMs = nowUtc.ToUniversalTime().ToUnixTimeMilliseconds();
            foreach (Guid id in deliveryIds)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE reminder_deliveries
                    SET delivery_state = 'delivered', attempted_utc_ms = $now
                    WHERE id = $id AND delivery_state = 'claimed';
                    """;
                cmd.Parameters.AddWithValue("$now", nowMs);
                cmd.Parameters.AddWithValue("$id", id.ToString("D"));
                cmd.ExecuteNonQuery();
            }
            return 0;
        });
    }

    public IReadOnlyList<ReminderDeliveryRecord> ListDeliveriesForRecord(Guid recordId)
    {
        return Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, record_id, due_fingerprint, stage, occurrence_key, delivery_state, attempted_utc_ms
                FROM reminder_deliveries
                WHERE record_id = $rec
                ORDER BY attempted_utc_ms ASC;
                """;
            cmd.Parameters.AddWithValue("$rec", recordId.ToString("D"));
            var list = new List<ReminderDeliveryRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(MapDelivery(reader));
            }
            return list;
        });
    }

    private static ReminderDeliveryRecord MapDelivery(SqliteDataReader r)
    {
        return new ReminderDeliveryRecord(
            Guid.Parse(r.GetString(0)),
            Guid.Parse(r.GetString(1)),
            r.GetString(2),
            r.GetString(3),
            r.GetString(4),
            r.GetString(5),
            r.GetInt64(6));
    }
}
