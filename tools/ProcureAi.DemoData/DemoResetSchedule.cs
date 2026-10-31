using System.ComponentModel.DataAnnotations;

namespace ProcureAi.DemoData;

/// <summary>Section <c>DemoReset</c> (plan 06 ruling 4): when <c>schedule</c> runs the nightly reset.</summary>
public sealed class DemoResetScheduleOptions
{
    public const string Section = "DemoReset";

    /// <summary>The local time of day in <see cref="TimeZone"/>, <c>02:00</c>.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "23:59:59")]
    public TimeSpan TimeOfDay { get; set; } = new(2, 0, 0);

    /// <summary>The IANA time zone, <c>Africa/Johannesburg</c> (SAST, UTC+2, no daylight saving).</summary>
    [Required]
    public string TimeZone { get; set; } = "Africa/Johannesburg";
}
