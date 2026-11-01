using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Gateway.Configuration;

/// <summary>
/// The gateway's part of the <c>DemoReset</c> section: the maintenance window of the nightly demo reset (plan 07 Task 0b). While the Redis
/// key <see cref="MaintenanceKey"/> exists, users' writes get 503 (<see cref="Bff.MaintenanceMiddleware"/>).
/// </summary>
public sealed class MaintenanceOptions
{
    public const string SectionName = "DemoReset";

    /// <summary>The Redis key of the flag; its value is the ISO time it was set.</summary>
    [Required(AllowEmptyStrings = false)]
    public string MaintenanceKey { get; set; } = "procure:maintenance";

    /// <summary>The flag's expiry: a reset that crashes before it lifts the flag never locks the app for longer than this.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan MaintenanceTtl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The <c>Retry-After</c> of the 503 (whole seconds).</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan MaintenanceRetryAfter { get; set; } = TimeSpan.FromSeconds(30);
}
