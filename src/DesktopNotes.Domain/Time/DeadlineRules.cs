namespace DesktopNotes.Domain;

public static class DeadlineRules
{
    public static DateTimeOffset? Boundary(DueSpec due, TimeZoneInfo displayZone)
    {
        ArgumentNullException.ThrowIfNull(displayZone);

        return due.Kind switch
        {
            DueKind.None => null,
            DueKind.Instant => due.Instant?.ToUniversalTime(),
            DueKind.Date when due.Date is { } date => ResolveLocalMidnightUtc(date.AddDays(1), displayZone),
            _ => null
        };
    }

    public static bool IsStartValid(DateTimeOffset? start, DueSpec due, TimeZoneInfo displayZone)
    {
        if (start is null || due.Kind == DueKind.None)
        {
            return true;
        }

        var boundary = Boundary(due, displayZone);
        if (boundary is null)
        {
            return true;
        }

        return due.Kind switch
        {
            DueKind.Instant => start.Value.ToUniversalTime() <= boundary.Value,
            DueKind.Date => start.Value.ToUniversalTime() < boundary.Value,
            _ => true
        };
    }

    public static DeadlineStage EvaluateStage(
        DueSpec due,
        RecordStatus status,
        DateTimeOffset nowUtc,
        TimeZoneInfo displayZone)
    {
        if (due.Kind == DueKind.None)
        {
            return status == RecordStatus.Closed ? DeadlineStage.Completed : DeadlineStage.None;
        }

        if (status == RecordStatus.Closed)
        {
            return DeadlineStage.Completed;
        }

        var boundary = Boundary(due, displayZone);
        if (boundary is null)
        {
            return DeadlineStage.Normal;
        }

        var utcNow = nowUtc.ToUniversalTime();
        if (utcNow >= boundary.Value)
        {
            return DeadlineStage.Overdue;
        }

        var localNow = TimeZoneInfo.ConvertTime(utcNow, displayZone);
        var localToday = DateOnly.FromDateTime(localNow.DateTime);

        DateOnly dueLocalDay = due.Kind switch
        {
            DueKind.Date when due.Date is { } d => d,
            DueKind.Instant when due.Instant is { } inst => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(inst, displayZone).DateTime),
            _ => localToday.AddDays(1)
        };

        return dueLocalDay == localToday ? DeadlineStage.DueToday : DeadlineStage.Normal;
    }

    public static string ComputeFingerprint(DueSpec due)
    {
        return due.Kind switch
        {
            DueKind.None => "none",
            DueKind.Date when due.Date is { } d => $"date:{d:yyyy-MM-dd}",
            DueKind.Instant when due.Instant is { } inst => $"instant:{inst.ToUniversalTime().ToUnixTimeMilliseconds()}",
            _ => "none"
        };
    }

    public static DateTimeOffset ResolveLocalMidnightUtc(DateOnly localDay, TimeZoneInfo zone)
    {
        var midnight = localDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(midnight))
        {
            // DST spring-forward exactly at midnight: step forward minute-by-minute to first valid instant of that day
            var probe = midnight;
            for (int minutes = 1; minutes <= 240; minutes++)
            {
                var candidate = midnight.AddMinutes(minutes);
                if (!zone.IsInvalidTime(candidate))
                {
                    probe = candidate;
                    break;
                }
            }
            var offset = zone.GetUtcOffset(probe);
            return new DateTimeOffset(probe, offset).ToUniversalTime();
        }

        if (zone.IsAmbiguousTime(midnight))
        {
            // Repeated midnight: take the earlier instance (larger UTC offset / daylight saving offset before fallback)
            var offsets = zone.GetAmbiguousTimeOffsets(midnight);
            var earlierOffset = offsets.OrderByDescending(o => o).First();
            return new DateTimeOffset(midnight, earlierOffset).ToUniversalTime();
        }

        var normalOffset = zone.GetUtcOffset(midnight);
        return new DateTimeOffset(midnight, normalOffset).ToUniversalTime();
    }
}
