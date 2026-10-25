using System.Text.Json;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Domain;

namespace DesktopNotes.Application.Records;

public sealed record ProjectedRecord(
    Guid RecordId,
    EffectiveFields Effective,
    long? DueBoundaryUtcMs,
    string ProjectionZone,
    long RecordVersion);

public static class ProjectionBuilder
{
    public static ProjectedRecord BuildActive(
        Guid recordId,
        EffectiveFields own,
        EffectiveFields? parentEffective,
        InheritField inheritMask,
        long recordVersion,
        TimeZoneInfo displayZone)
    {
        var effective = InheritanceResolver.Resolve(own, parentEffective, inheritMask);
        if (!RecordRules.IsValidDueSpec(effective.Due))
        {
            throw new AppException(ErrorCodes.Validation, "Invalid effective deadline specification.", new[] { recordId });
        }

        if (!DeadlineRules.IsStartValid(effective.Start, effective.Due, displayZone))
        {
            throw new AppException(
                ErrorCodes.Validation,
                "Effective start time cannot be later than effective deadline.",
                new[] { recordId });
        }

        var boundary = DeadlineRules.Boundary(effective.Due, displayZone);
        long? boundaryUtcMs = boundary?.ToUniversalTime().ToUnixTimeMilliseconds();

        return new ProjectedRecord(
            recordId,
            effective,
            boundaryUtcMs,
            displayZone.Id,
            recordVersion);
    }

    public static ProjectedRecord BuildFromSnapshot(
        Guid recordId,
        string snapshotJson,
        long recordVersion,
        TimeZoneInfo displayZone)
    {
        var effective = DeserializeSnapshot(snapshotJson);
        var boundary = DeadlineRules.Boundary(effective.Due, displayZone);
        long? boundaryUtcMs = boundary?.ToUniversalTime().ToUnixTimeMilliseconds();

        return new ProjectedRecord(
            recordId,
            effective,
            boundaryUtcMs,
            displayZone.Id,
            recordVersion);
    }

    public static string SerializeSnapshot(EffectiveFields effective)
    {
        var dto = new SnapshotPayload(
            SchemaVersion: 1,
            Publisher: effective.Publisher,
            Assignee: effective.Assignee,
            Priority: (int)effective.Priority,
            StartUtcMs: effective.Start?.ToUniversalTime().ToUnixTimeMilliseconds(),
            DueKind: (int)effective.Due.Kind,
            DueDate: effective.Due.Date?.ToString("yyyy-MM-dd"),
            DueUtcMs: effective.Due.Instant?.ToUniversalTime().ToUnixTimeMilliseconds(),
            DueZone: effective.Due.ZoneId,
            AttachmentRootId: effective.AttachmentRootId?.ToString("D"));

        return JsonSerializer.Serialize(dto);
    }

    public static EffectiveFields DeserializeSnapshot(string json)
    {
        var dto = JsonSerializer.Deserialize<SnapshotPayload>(json)
            ?? throw new InvalidOperationException("Invalid completion snapshot JSON.");

        var kind = (DueKind)dto.DueKind;
        DueSpec due = kind switch
        {
            DueKind.Date when !string.IsNullOrWhiteSpace(dto.DueDate) =>
                new DueSpec(DueKind.Date, DateOnly.ParseExact(dto.DueDate, "yyyy-MM-dd"), null, null),
            DueKind.Instant when dto.DueUtcMs.HasValue =>
                new DueSpec(DueKind.Instant, null, DateTimeOffset.FromUnixTimeMilliseconds(dto.DueUtcMs.Value), dto.DueZone ?? "UTC"),
            _ => DueSpec.Empty
        };

        return new EffectiveFields(
            dto.Publisher,
            dto.Assignee,
            (Priority)dto.Priority,
            dto.StartUtcMs.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(dto.StartUtcMs.Value) : null,
            due,
            !string.IsNullOrWhiteSpace(dto.AttachmentRootId) && Guid.TryParse(dto.AttachmentRootId, out var rootId) ? rootId : null);
    }

    private sealed record SnapshotPayload(
        int SchemaVersion,
        string? Publisher,
        string? Assignee,
        int Priority,
        long? StartUtcMs,
        int DueKind,
        string? DueDate,
        long? DueUtcMs,
        string? DueZone,
        string? AttachmentRootId);
}
