using ProcureAi.Budgets.Domain;
using ProcureAi.BuildingBlocks.Domain;
using Shouldly;

namespace ProcureAi.Budgets.Tests;

public sealed class FiscalYearTests
{
    private static readonly TimeZoneInfo Sast = TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg");

    [Theory]
    [InlineData("2027-02-28T21:30:00Z", 2027)] // 23:30 on 28 Feb in SAST: still the year ending Feb 2027
    [InlineData("2027-02-28T22:30:00Z", 2028)] // 00:30 on 1 March in SAST: next year
    [InlineData("2026-03-01T00:00:00+02:00", 2027)]
    [InlineData("2028-02-29T23:59:00+02:00", 2028)] // leap day, still the year ending Feb 2028
    [InlineData("2026-12-31T10:00:00Z", 2027)]
    [InlineData("2027-01-15T10:00:00Z", 2027)]
    public void Instants_fall_in_the_right_fiscal_year(string instant, int endYear)
    {
        FiscalYear.For(DateTimeOffset.Parse(instant), Sast).EndYear.ShouldBe(endYear);
    }

    [Fact]
    public void Prints_as_FY_and_parses_back()
    {
        new FiscalYear(2027).ToString().ShouldBe("FY2027");
        FiscalYear.TryParse("FY2027", out var parsed).ShouldBeTrue();
        parsed.ShouldBe(new FiscalYear(2027));
        FiscalYear.TryParse("2027", out _).ShouldBeFalse();
        FiscalYear.TryParse(null, out _).ShouldBeFalse();
    }

    [Fact]
    public void Starts_on_1_March_local_and_ends_where_the_next_year_starts()
    {
        var year = new FiscalYear(2027);
        year.Start(Sast).ShouldBe(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.FromHours(2)));
        year.End(Sast).ShouldBe(new DateTimeOffset(2027, 3, 1, 0, 0, 0, TimeSpan.FromHours(2)));
        year.End(Sast).ShouldBe(new FiscalYear(2028).Start(Sast));
    }
}
