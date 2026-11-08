using System.ComponentModel.DataAnnotations;
using ProcureAi.BuildingBlocks.Web.Http;

namespace ProcureAi.Gateway.Configuration;

/// <summary>
/// The <c>SystemAvailability</c> section: how the gateway reads the demo's on/off switch from Access and what it blocks while the demo is
/// off (plan 08 Task 12b, <see cref="Bff.SystemAvailabilityMiddleware"/>). Retries and timeouts of the read come from the base class: it
/// sits in front of every browser call, so it fails fast.
/// </summary>
public sealed class SystemAvailabilityOptions : ServiceClientResilienceOptions, IValidatableObject
{
    public const string SectionName = "SystemAvailability";

    public SystemAvailabilityOptions()
    {
        AttemptTimeout = TimeSpan.FromSeconds(2);
        TotalTimeout = TimeSpan.FromSeconds(4);
    }

    /// <summary>How long the switch is trusted before Access is asked again: a change reaches the gateway within this time.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long the last answer is kept while Access cannot be reached.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "7.00:00:00")]
    public TimeSpan FailSafeMaxDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>After a failed read, the last answer (or "on", when there never was one) is used this long before Access is asked again.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan FailSafeThrottleDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The <c>Retry-After</c> of the 503 (whole seconds).</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The paths (and everything below them) that are refused while the demo is off: the browser's API, BFF and hub calls (appsettings:
    /// <c>/api</c>, <c>/bff</c>, <c>/hubs</c>). No code default: the configuration binder adds to an array instead of replacing it.
    /// </summary>
    [MinLength(1)]
    public string[] BlockedPaths { get; set; } = [];

    /// <summary>
    /// Paths below <see cref="BlockedPaths"/> that stay open while the demo is off: the switch itself (the web app reads it, the admin key
    /// sets it; appsettings: <c>/api/v1/system/availability</c>). Health, Swagger UI and the web app's files are outside the blocked paths anyway.
    /// </summary>
    public string[] AllowedPaths { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var path in BlockedPaths.Concat(AllowedPaths))
        {
            if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || path.Length > 1 && path.EndsWith('/'))
            {
                yield return new ValidationResult(
                    $"'{path}' is not a path ('/segment[/segment...]', no trailing '/').", [nameof(BlockedPaths), nameof(AllowedPaths)]);
            }
        }
    }
}
