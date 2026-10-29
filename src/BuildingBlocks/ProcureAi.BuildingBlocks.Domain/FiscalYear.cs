using System.Globalization;

namespace ProcureAi.BuildingBlocks.Domain;

/// <summary>
/// A South African company fiscal year: 1 March to the end of February, in the tenant's local time. It is named after the
/// calendar year it ends in, so 1 March 2026 to 28 February 2027 is <c>FY2027</c>. Shared: Budgets keeps budgets per year, and the
/// Assistant's <c>get_budget</c> tool asks Budgets for the current one.
/// </summary>
public readonly record struct FiscalYear(int EndYear)
{
    private const string Prefix = "FY";

    /// <summary>The first month of a fiscal year (March).</summary>
    private const int FirstMonth = 3;

    public override string ToString() => Prefix + EndYear.ToString(CultureInfo.InvariantCulture);

    /// <summary>The fiscal year the instant falls in, judged by the calendar in <paramref name="zone"/>.</summary>
    public static FiscalYear For(DateTimeOffset at, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        return new FiscalYear(local.Month >= FirstMonth ? local.Year + 1 : local.Year);
    }

    public static bool TryParse(string? text, out FiscalYear year)
    {
        year = default;
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal)
            || !int.TryParse(text.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var endYear)
            || endYear is < 1 or > 9999)
        {
            return false;
        }

        year = new FiscalYear(endYear);
        return true;
    }

    /// <summary>Midnight local time on 1 March, the first moment of the year.</summary>
    public DateTimeOffset Start(TimeZoneInfo zone) => LocalMidnight(EndYear - 1, zone);

    /// <summary>The first moment of the next fiscal year (so the year is the half-open range Start to End).</summary>
    public DateTimeOffset End(TimeZoneInfo zone) => LocalMidnight(EndYear, zone);

    private static DateTimeOffset LocalMidnight(int year, TimeZoneInfo zone)
    {
        var local = new DateTime(year, FirstMonth, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
