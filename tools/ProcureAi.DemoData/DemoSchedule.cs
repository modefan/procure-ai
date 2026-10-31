namespace ProcureAi.DemoData;

/// <summary>When the nightly reset runs next (plan 06 ruling 4: 02:00 Africa/Johannesburg).</summary>
public static class DemoSchedule
{
    /// <summary>
    /// The first moment strictly after <paramref name="now"/> whose local time in <paramref name="zone"/> is <paramref name="timeOfDay"/>.
    /// In a zone with daylight saving, a time that does not exist on the night the clocks go forward moves to the next valid minute.
    /// </summary>
    public static DateTimeOffset NextRun(DateTimeOffset now, TimeSpan timeOfDay, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var candidate = DateTime.SpecifyKind(local.Date + timeOfDay, DateTimeKind.Unspecified);
        if (candidate <= local.DateTime)
        {
            candidate = candidate.AddDays(1);
        }

        while (zone.IsInvalidTime(candidate))
        {
            candidate = candidate.AddMinutes(1);
        }

        return new DateTimeOffset(candidate, zone.GetUtcOffset(candidate));
    }
}
