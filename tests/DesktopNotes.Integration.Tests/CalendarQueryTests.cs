using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Queries;
using DesktopNotes.Application.Records;
using DesktopNotes.Domain;
using DesktopNotes.Infrastructure.Persistence;
using Xunit;

namespace DesktopNotes.Integration.Tests;

public class CalendarQueryTests
{
    private static readonly TimeZoneInfo ZoneUtc8 = TimeZoneInfo.CreateCustomTimeZone(
        "TestUTC8", TimeSpan.FromHours(8), "TestUTC8", "TestUTC8");

    [Fact]
    public async Task Calendar_supports_four_axes_cross_utc_day_dateless_snapshots_subtasks_and_leap_year()
    {
        using var store = new SqliteStore(":memory:");
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.FromHours(8)));
        var recordService = new RecordService(store, clock, () => ZoneUtc8);
        var queryService = new RecordQueryService(store, () => ZoneUtc8);

        var emptyOwn = new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null);

        // 1. Dateless task -> appears in Dateless (Day == null)
        Guid datelessId = Guid.NewGuid();
        await recordService.SaveAsync(new SaveRecordRequest(
            datelessId, 0, RecordKind.Task, null, "无日期任务", "", emptyOwn, InheritField.None), CancellationToken.None);

        // 2. Cross-UTC-day instant: 2026-10-31 20:00 UTC is 2026-11-01 04:00 +08 (belongs to Nov 2026 in +08, NOT Oct 2026!)
        Guid crossUtcId = Guid.NewGuid();
        var nov1LocalUtc = new DateTimeOffset(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);
        await recordService.SaveAsync(new SaveRecordRequest(
            crossUtcId, 0, RecordKind.Task, null, "跨UTC日任务", "",
            emptyOwn with { Due = new DueSpec(DueKind.Instant, null, nov1LocalUtc, "Asia/Shanghai") },
            InheritField.None), CancellationToken.None);

        // 3. Parent with Due = 2026-10-20 and inherited subtask -> both appear in Oct 2026 Due view, subtask has ParentId
        Guid parentId = Guid.NewGuid();
        Guid childId = Guid.NewGuid();
        var oct20Due = new DueSpec(DueKind.Date, new DateOnly(2026, 10, 20), null, null);
        await recordService.SaveAsync(new SaveRecordRequest(
            parentId, 0, RecordKind.Task, null, "日历父任务", "",
            emptyOwn with { Due = oct20Due }, InheritField.None), CancellationToken.None);
        var childRec = (await recordService.SaveAsync(new SaveRecordRequest(
            childId, 0, RecordKind.Task, parentId, "日历子任务", "",
            emptyOwn, InheritField.All), CancellationToken.None)).Changed.Single();

        // Complete child on 2026-10-22 +08 -> frozen Due stays 2026-10-20, Closed date is 2026-10-22
        clock.SetUtcNow(new DateTimeOffset(2026, 10, 22, 14, 0, 0, TimeSpan.FromHours(8)));
        await recordService.CloseAsync(childId, childRec.Version, includeChildren: false, CancellationToken.None);

        // Modify parent Due to 2026-10-28 -> completed child still stays at 2026-10-20 in Due view!
        var pCurrent = (await recordService.GetAsync(parentId, CancellationToken.None))!;
        await recordService.SaveAsync(new SaveRecordRequest(
            parentId, pCurrent.Version, RecordKind.Task, null, "日历父任务", "",
            emptyOwn with { Due = new DueSpec(DueKind.Date, new DateOnly(2026, 10, 28), null, null) },
            InheritField.None), CancellationToken.None);

        var octDueEntries = await queryService.CalendarAsync(
            new CalendarQuery(2026, 10, CalendarAxis.Due, RecordKind.Task, Status: null), CancellationToken.None);

        Assert.Contains(octDueEntries, e => e.Record.Id == datelessId && e.Day is null);
        Assert.DoesNotContain(octDueEntries, e => e.Record.Id == crossUtcId);
        Assert.Contains(octDueEntries, e => e.Record.Id == parentId && e.Day == new DateOnly(2026, 10, 28));
        Assert.Contains(octDueEntries, e => e.Record.Id == childId && e.Record.ParentId == parentId && e.Day == new DateOnly(2026, 10, 20));

        // Verify crossUtcId appears on 2026-11-01 in Nov 2026 Due view
        var novDueEntries = await queryService.CalendarAsync(
            new CalendarQuery(2026, 11, CalendarAxis.Due, RecordKind.Task, Status: null), CancellationToken.None);
        Assert.Contains(novDueEntries, e => e.Record.Id == crossUtcId && e.Day == new DateOnly(2026, 11, 1));

        // Verify Closed axis places completed child on 2026-10-22
        var octClosedEntries = await queryService.CalendarAsync(
            new CalendarQuery(2026, 10, CalendarAxis.Closed, RecordKind.Task, Status: RecordStatus.Closed), CancellationToken.None);
        Assert.Contains(octClosedEntries, e => e.Record.Id == childId && e.Day == new DateOnly(2026, 10, 22));

        // 4. Leap year February 2028-02-29 test
        Guid leapId = Guid.NewGuid();
        await recordService.SaveAsync(new SaveRecordRequest(
            leapId, 0, RecordKind.Task, null, "闰年任务", "",
            emptyOwn with { Due = new DueSpec(DueKind.Date, new DateOnly(2028, 2, 29), null, null) },
            InheritField.None), CancellationToken.None);

        var leapFebEntries = await queryService.CalendarAsync(
            new CalendarQuery(2028, 2, CalendarAxis.Due, RecordKind.Task, Status: RecordStatus.Active), CancellationToken.None);
        Assert.Contains(leapFebEntries, e => e.Record.Id == leapId && e.Day == new DateOnly(2028, 2, 29));
    }
}
