using DesktopNotes.Domain;
using Xunit;

namespace DesktopNotes.Domain.Tests;

public class DeadlineRulesTests
{
    [Fact]
    public void Date_only_expires_at_next_local_day()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("TestUTC8",
            TimeSpan.FromHours(8), "TestUTC8", "TestUTC8");
        var due = new DueSpec(DueKind.Date, new DateOnly(2026, 10, 25), null, null);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 16, 0, 0, TimeSpan.Zero),
            DeadlineRules.Boundary(due, zone));
        Assert.Null(DeadlineRules.Boundary(DueSpec.Empty, zone));
    }

    [Fact]
    public void Instant_due_uses_exact_utc_instant_and_validates_start_order()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("TestUTC8",
            TimeSpan.FromHours(8), "TestUTC8", "TestUTC8");
        var dueInstant = new DateTimeOffset(2026, 10, 25, 10, 30, 0, TimeSpan.Zero);
        var due = new DueSpec(DueKind.Instant, null, dueInstant, "Asia/Shanghai");

        Assert.Equal(dueInstant, DeadlineRules.Boundary(due, zone));
        Assert.True(DeadlineRules.IsStartValid(dueInstant.AddMinutes(-1), due, zone));
        Assert.True(DeadlineRules.IsStartValid(dueInstant, due, zone));
        Assert.False(DeadlineRules.IsStartValid(dueInstant.AddMinutes(1), due, zone));
    }

    [Fact]
    public void Dst_transition_day_calculates_real_local_midnight_boundary()
    {
        // Construct a custom DST zone with spring-forward and fall-back transitions
        var adjustment = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2020, 1, 1),
            new DateTime(2030, 12, 31),
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday));

        var dstZone = TimeZoneInfo.CreateCustomTimeZone(
            "CustomDstEastern",
            TimeSpan.FromHours(-5),
            "CustomDstEastern",
            "CustomDstEastern",
            "CustomDstEasternDaylight",
            new[] { adjustment });

        // 2026-03-08 is spring-forward day (23-hour day in US Eastern, -05:00 -> -04:00)
        var springDue = new DueSpec(DueKind.Date, new DateOnly(2026, 3, 8), null, null);
        var boundary = DeadlineRules.Boundary(springDue, dstZone);
        // Next local midnight is 2026-03-09 00:00:00 -04:00 = 2026-03-09 04:00:00 UTC
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 4, 0, 0, TimeSpan.Zero), boundary);
    }

    [Fact]
    public void Evaluation_classifies_normal_due_today_overdue_and_closed()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("TestUTC8",
            TimeSpan.FromHours(8), "TestUTC8", "TestUTC8");
        var dueDate = new DueSpec(DueKind.Date, new DateOnly(2026, 10, 25), null, null);

        // 2026-10-24 12:00 +08 -> Normal
        var beforeToday = new DateTimeOffset(2026, 10, 24, 4, 0, 0, TimeSpan.Zero);
        Assert.Equal(DeadlineStage.Normal, DeadlineRules.EvaluateStage(dueDate, RecordStatus.Active, beforeToday, zone));

        // 2026-10-25 23:59:59 +08 -> DueToday
        var endOfToday = new DateTimeOffset(2026, 10, 25, 15, 59, 59, TimeSpan.Zero);
        Assert.Equal(DeadlineStage.DueToday, DeadlineRules.EvaluateStage(dueDate, RecordStatus.Active, endOfToday, zone));

        // 2026-10-26 00:00:00 +08 -> Overdue
        var nextMidnight = new DateTimeOffset(2026, 10, 25, 16, 0, 0, TimeSpan.Zero);
        Assert.Equal(DeadlineStage.Overdue, DeadlineRules.EvaluateStage(dueDate, RecordStatus.Active, nextMidnight, zone));

        // Closed task -> Completed
        Assert.Equal(DeadlineStage.Completed, DeadlineRules.EvaluateStage(dueDate, RecordStatus.Closed, nextMidnight, zone));
    }
}
