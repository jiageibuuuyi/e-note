using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Ports;
using DesktopNotes.Application.Records;
using DesktopNotes.Application.Reminders;
using DesktopNotes.Domain;
using DesktopNotes.Infrastructure.Persistence;
using Xunit;

namespace DesktopNotes.Integration.Tests;

public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public ManualTimeProvider(DateTimeOffset initialUtcNow)
    {
        _utcNow = initialUtcNow.ToUniversalTime();
    }

    public void SetUtcNow(DateTimeOffset value) => _utcNow = value.ToUniversalTime();

    public override DateTimeOffset GetUtcNow() => _utcNow;
}

public sealed class RecordingNotificationSink : INotificationSink
{
    public List<SystemNotificationPayload> Sent { get; } = new();

    public Task<bool> SendAsync(SystemNotificationPayload notification, CancellationToken ct)
    {
        Sent.Add(notification);
        return Task.FromResult(true);
    }
}

public class ReminderTests
{
    private static readonly TimeZoneInfo ZoneUtc8 = TimeZoneInfo.CreateCustomTimeZone(
        "TestUTC8", TimeSpan.FromHours(8), "TestUTC8", "TestUTC8");

    [Fact]
    public async Task Date_and_instant_boundaries_deduplication_inheritance_and_startup_claim_recovery()
    {
        using var store = new SqliteStore(":memory:");
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 25, 23, 59, 59, TimeSpan.FromHours(8)));
        var recordService = new RecordService(store, clock, () => ZoneUtc8);
        var sink = new RecordingNotificationSink();
        using var scheduler = new ReminderScheduler(store, sink, clock, () => ZoneUtc8)
        {
            EnableQuietHours = false
        };

        // Default notifications disabled per R12
        Assert.False(scheduler.NotificationsEnabled);

        Guid parentId = Guid.NewGuid();
        Guid childId = Guid.NewGuid();
        var dueOct25 = new DueSpec(DueKind.Date, new DateOnly(2026, 10, 25), null, null);

        var p = (await recordService.SaveAsync(new SaveRecordRequest(
            parentId, 0, RecordKind.Task, null, "主任务", "正文1",
            new EffectiveFields(null, "甲", Priority.High, null, dueOct25, null),
            InheritField.None), CancellationToken.None)).Changed.Single();

        await recordService.SaveAsync(new SaveRecordRequest(
            childId, 0, RecordKind.Task, parentId, "跟随子任务", "子正文",
            new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null),
            InheritField.All), CancellationToken.None);

        // 1. At 2026-10-25 23:59:59 +08: both parent and child are DueToday, NOT overdue; notifications off -> 0 sent
        var summaryBeforeMidnight = await scheduler.EvaluateNowAsync(CancellationToken.None);
        Assert.Equal(2, summaryBeforeMidnight.DueTodayCount);
        Assert.Equal(0, summaryBeforeMidnight.OverdueCount);
        Assert.Equal(0, summaryBeforeMidnight.NotificationsSent);

        // 2. Enable notifications and advance to 2026-10-26 00:00:00 +08 -> both become Overdue!
        scheduler.NotificationsEnabled = true;
        clock.SetUtcNow(new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.FromHours(8)));

        var summaryAtMidnight = await scheduler.EvaluateNowAsync(CancellationToken.None);
        Assert.Equal(0, summaryAtMidnight.DueTodayCount);
        Assert.Equal(2, summaryAtMidnight.OverdueCount);
        Assert.Equal(2, summaryAtMidnight.NotificationsSent);

        // 3. Re-evaluating or editing body does NOT change due_fingerprint and does NOT re-send notifications
        p = (await recordService.GetAsync(parentId, CancellationToken.None))!;
        p = (await recordService.SaveAsync(new SaveRecordRequest(
            parentId, p.Version, RecordKind.Task, null, "主任务", "修改后的正文不改变截止指纹",
            p.Own, InheritField.None), CancellationToken.None)).Changed.First(x => x.Id == parentId);

        var summaryAfterBodyEdit = await scheduler.EvaluateNowAsync(CancellationToken.None);
        Assert.Equal(0, summaryAfterBodyEdit.NotificationsSent);

        // 4. Changing parent deadline to 2026-10-26 (Instant 12:00 +08) updates both parent and unoverridden child fingerprints
        var dueOct26Noon = new DueSpec(
            DueKind.Instant,
            null,
            new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.FromHours(8)).ToUniversalTime(),
            "Asia/Shanghai");

        await recordService.SaveAsync(new SaveRecordRequest(
            parentId, p.Version, RecordKind.Task, null, "主任务", "新截止",
            p.Own with { Due = dueOct26Noon }, InheritField.None), CancellationToken.None);

        // At 11:30 +08 (exactly 30 minutes before 12:00 +08) -> triggers "before" reminder for both parent and child
        clock.SetUtcNow(new DateTimeOffset(2026, 10, 26, 11, 30, 0, TimeSpan.FromHours(8)));
        sink.Sent.Clear();
        var summaryBeforeInstant = await scheduler.EvaluateNowAsync(CancellationToken.None);
        Assert.Equal(2, summaryBeforeInstant.NotificationsSent);
        Assert.All(sink.Sent, n => Assert.Equal("before", n.Stage));

        // 5. Closing child stops reminders for child when 12:00 +08 arrives
        var childCurrent = (await recordService.GetAsync(childId, CancellationToken.None))!;
        await recordService.CloseAsync(childId, childCurrent.Version, includeChildren: false, CancellationToken.None);

        clock.SetUtcNow(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.FromHours(8)));
        sink.Sent.Clear();
        var summaryAtNoon = await scheduler.EvaluateNowAsync(CancellationToken.None);
        Assert.Equal(1, summaryAtNoon.ActiveCandidateCount);
        Assert.Equal(1, summaryAtNoon.OverdueCount);
        Assert.Equal(1, summaryAtNoon.NotificationsSent);
        Assert.Equal(parentId, sink.Sent.Single().RecordId);

        // 6. Uncertain claimed rows on restart emit a single summary notification instead of re-sending individually
        store.TryClaimDelivery(parentId, "custom-fp-1", "today", "2026-10-27", clock.GetUtcNow(), out _);
        store.TryClaimDelivery(parentId, "custom-fp-2", "before", "2026-10-27", clock.GetUtcNow(), out _);
        sink.Sent.Clear();

        int recoveryCount = await scheduler.ReconcileStartupClaimedAsync(CancellationToken.None);
        Assert.Equal(1, recoveryCount);
        Assert.Single(sink.Sent);
        Assert.Equal("recovery_summary", sink.Sent[0].Stage);
        Assert.Empty(store.GetUncertainClaimedDeliveries());
    }
}
