using DesktopNotes.Application.Ports;
using DesktopNotes.Domain;

namespace DesktopNotes.Application.Reminders;

public sealed record ReminderEvaluationSummary(
    int ActiveCandidateCount,
    int DueTodayCount,
    int OverdueCount,
    DateTimeOffset? NextWakeupUtc,
    int NotificationsSent);

public sealed class ReminderScheduler : IDisposable
{
    private readonly IReminderRepositoryPort _repository;
    private readonly INotificationSink? _notificationSink;
    private readonly TimeProvider _timeProvider;
    private Func<TimeZoneInfo> _zoneProvider;
    private Timer? _timer;

    public bool NotificationsEnabled { get; set; } = false; // Default false per R12
    public TimeSpan LeadTimeBeforeInstant { get; set; } = TimeSpan.FromMinutes(30);
    public bool EnableQuietHours { get; set; } = true;
    public TimeOnly QuietStart { get; set; } = new(22, 0);
    public TimeOnly QuietEnd { get; set; } = new(8, 0);

    public ReminderScheduler(
        IReminderRepositoryPort repository,
        INotificationSink? notificationSink = null,
        TimeProvider? timeProvider = null,
        Func<TimeZoneInfo>? zoneProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _notificationSink = notificationSink;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _zoneProvider = zoneProvider ?? (() => TimeZoneInfo.Local);
    }

    public void SetTimeZoneProvider(Func<TimeZoneInfo> zoneProvider)
    {
        _zoneProvider = zoneProvider ?? throw new ArgumentNullException(nameof(zoneProvider));
    }

    public async Task<int> ReconcileStartupClaimedAsync(CancellationToken ct)
    {
        var claimed = _repository.GetUncertainClaimedDeliveries();
        if (claimed.Count == 0)
        {
            return 0;
        }

        var nowUtc = _timeProvider.GetUtcNow();
        _repository.MarkClaimedDeliveriesResolved(claimed.Select(c => c.Id).ToArray(), nowUtc);

        if (NotificationsEnabled && _notificationSink is not null)
        {
            await _notificationSink.SendAsync(new SystemNotificationPayload(
                RecordId: null,
                Stage: "recovery_summary",
                Title: "未完成到期提醒汇总",
                Message: $"上次运行期间有 {claimed.Count} 项待确认提醒，请在面板查看。"), ct);
            return 1;
        }

        return 0;
    }

    public async Task<ReminderEvaluationSummary> EvaluateNowAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var nowUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        long nowMs = nowUtc.ToUnixTimeMilliseconds();
        var zone = _zoneProvider();
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var localToday = DateOnly.FromDateTime(localNow.DateTime);
        var localTime = TimeOnly.FromDateTime(localNow.DateTime);

        bool inQuietHours = EnableQuietHours && IsInQuietHours(localTime, QuietStart, QuietEnd);

        var candidates = _repository.GetActiveReminderCandidates();
        int dueTodayCount = 0;
        int overdueCount = 0;
        int notificationsSent = 0;
        long? nextBoundaryMs = null;

        foreach (var c in candidates)
        {
            var stage = DeadlineRules.EvaluateStage(c.EffectiveDue, RecordStatus.Active, nowUtc, zone);
            if (stage == DeadlineStage.DueToday) dueTodayCount++;
            else if (stage == DeadlineStage.Overdue) overdueCount++;

            if (c.DueBoundaryUtcMs > nowMs)
            {
                if (!nextBoundaryMs.HasValue || c.DueBoundaryUtcMs < nextBoundaryMs.Value)
                {
                    nextBoundaryMs = c.DueBoundaryUtcMs;
                }

                if (c.EffectiveDue.Kind == DueKind.Instant)
                {
                    long beforeTriggerMs = c.DueBoundaryUtcMs - (long)LeadTimeBeforeInstant.TotalMilliseconds;
                    if (beforeTriggerMs > nowMs && (!nextBoundaryMs.HasValue || beforeTriggerMs < nextBoundaryMs.Value))
                    {
                        nextBoundaryMs = beforeTriggerMs;
                    }
                }
            }

            if (!NotificationsEnabled || _notificationSink is null || inQuietHours)
            {
                continue;
            }

            string fingerprint = DeadlineRules.ComputeFingerprint(c.EffectiveDue);
            string displayName = DisplayName.Create(c.Title, c.Body);

            // 1. Overdue notification (once per record + due_fingerprint)
            if (nowMs >= c.DueBoundaryUtcMs)
            {
                string occKey = c.DueBoundaryUtcMs.ToString();
                if (_repository.TryClaimDelivery(c.RecordId, fingerprint, "overdue", occKey, nowUtc, out Guid delId))
                {
                    bool ok = await _notificationSink.SendAsync(new SystemNotificationPayload(
                        c.RecordId, "overdue", "任务已逾期", displayName), ct);
                    _repository.CompleteDelivery(delId, ok, nowUtc);
                    if (ok) notificationsSent++;
                }
                continue;
            }

            // 2. 30-minute advance notification for Instant deadline
            if (c.EffectiveDue.Kind == DueKind.Instant)
            {
                long leadMs = (long)LeadTimeBeforeInstant.TotalMilliseconds;
                if (nowMs >= c.DueBoundaryUtcMs - leadMs && nowMs < c.DueBoundaryUtcMs)
                {
                    string occKey = c.DueBoundaryUtcMs.ToString();
                    if (_repository.TryClaimDelivery(c.RecordId, fingerprint, "before", occKey, nowUtc, out Guid delId))
                    {
                        bool ok = await _notificationSink.SendAsync(new SystemNotificationPayload(
                            c.RecordId, "before", "任务即将到期", displayName), ct);
                        _repository.CompleteDelivery(delId, ok, nowUtc);
                        if (ok) notificationsSent++;
                    }
                    continue;
                }
            }

            // 3. Today summary stage (once per local day per due-today task)
            if (stage == DeadlineStage.DueToday)
            {
                string todayKey = localToday.ToString("yyyy-MM-dd");
                if (_repository.TryClaimDelivery(c.RecordId, fingerprint, "today", todayKey, nowUtc, out Guid delId))
                {
                    bool ok = await _notificationSink.SendAsync(new SystemNotificationPayload(
                        c.RecordId, "today", "今日到期任务", displayName), ct);
                    _repository.CompleteDelivery(delId, ok, nowUtc);
                    if (ok) notificationsSent++;
                }
            }
        }

        // Next local midnight is also a wakeup boundary for date-only tasks
        var nextLocalMidnightUtc = DeadlineRules.ResolveLocalMidnightUtc(localToday.AddDays(1), zone);
        long midnightMs = nextLocalMidnightUtc.ToUnixTimeMilliseconds();
        if (!nextBoundaryMs.HasValue || midnightMs < nextBoundaryMs.Value)
        {
            nextBoundaryMs = midnightMs;
        }

        DateTimeOffset? nextWakeup = nextBoundaryMs.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(nextBoundaryMs.Value)
            : null;

        ScheduleNextTimer(nowUtc, nextWakeup);

        return new ReminderEvaluationSummary(
            candidates.Count,
            dueTodayCount,
            overdueCount,
            nextWakeup,
            notificationsSent);
    }

    private void ScheduleNextTimer(DateTimeOffset nowUtc, DateTimeOffset? nextWakeupUtc)
    {
        TimeSpan delay = TimeSpan.FromSeconds(60); // 60-second fallback check
        if (nextWakeupUtc.HasValue)
        {
            var diff = nextWakeupUtc.Value - nowUtc;
            if (diff > TimeSpan.Zero && diff < delay)
            {
                delay = diff;
            }
        }

        _timer?.Dispose();
        _timer = new Timer(_ => _ = EvaluateNowAsync(CancellationToken.None), null, delay, Timeout.InfiniteTimeSpan);
    }

    private static bool IsInQuietHours(TimeOnly current, TimeOnly quietStart, TimeOnly quietEnd)
    {
        if (quietStart <= quietEnd)
        {
            return current >= quietStart && current < quietEnd;
        }
        return current >= quietStart || current < quietEnd;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
