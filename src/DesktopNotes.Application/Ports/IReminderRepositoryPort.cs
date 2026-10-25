using DesktopNotes.Domain;

namespace DesktopNotes.Application.Ports;

public sealed record ReminderCandidate(
    Guid RecordId,
    string Title,
    string Body,
    DueSpec EffectiveDue,
    long DueBoundaryUtcMs);

public sealed record ReminderDeliveryRecord(
    Guid Id,
    Guid RecordId,
    string DueFingerprint,
    string Stage,
    string OccurrenceKey,
    string DeliveryState,
    long AttemptedUtcMs);

public sealed record SystemNotificationPayload(
    Guid? RecordId,
    string Stage,
    string Title,
    string Message);

public interface INotificationSink
{
    Task<bool> SendAsync(SystemNotificationPayload notification, CancellationToken ct);
}

public interface IReminderRepositoryPort
{
    IReadOnlyList<ReminderCandidate> GetActiveReminderCandidates();
    bool TryClaimDelivery(Guid recordId, string dueFingerprint, string stage, string occurrenceKey, DateTimeOffset nowUtc, out Guid deliveryId);
    void CompleteDelivery(Guid deliveryId, bool delivered, DateTimeOffset nowUtc);
    IReadOnlyList<ReminderDeliveryRecord> GetUncertainClaimedDeliveries();
    void MarkClaimedDeliveriesResolved(IReadOnlyList<Guid> deliveryIds, DateTimeOffset nowUtc);
    IReadOnlyList<ReminderDeliveryRecord> ListDeliveriesForRecord(Guid recordId);
}
