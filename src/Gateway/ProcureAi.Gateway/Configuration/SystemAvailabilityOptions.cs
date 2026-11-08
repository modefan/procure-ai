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

    /// <summary>
    /// How long the read waits for the gateway's service token before it reads anonymously instead (Keycloak slow or down; a cached token is
    /// instant). Kept well under <c>AttemptTimeout</c>.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.050", "00:00:10")]
    public TimeSpan TokenTimeout { get; set; } = TimeSpan.FromSeconds(1);

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
    /// sets it; appsettings: <c>/api/v1/system/availability</c>) and logging out (<c>/bff/logout</c>, <c>/bff/logout-all</c>). Health, Swagger UI and the web app's files are outside the blocked paths anyway.
    /// </summary>
    public string[] AllowedPaths { get; set; } = [];

    /// <summary>
    /// Paths that must stay reachable while the demo is off, whatever is configured: the switch (the web app reads it to show the maintenance
    /// page, the admin key turns the demo back on through it), the web app's <c>config.json</c>, health and Swagger UI. A blocked path that
    /// covers one of them (or <c>/</c>, which covers the web app itself) is refused at start-up unless an allowed path opens it again.
    /// </summary>
    public static readonly string[] AlwaysReachable = ["/api/v1/system/availability", "/config.json", "/health", "/swagger"];

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

        foreach (var blocked in BlockedPaths.Where(IsPath))
        {
            if (blocked == "/")
            {
                yield return new ValidationResult(
                    "'/' cannot be blocked: it would refuse the web app (and its maintenance page) too.", [nameof(BlockedPaths)]);
                continue;
            }

            foreach (var reachable in AlwaysReachable.Where(reachable => Covers(blocked, reachable)
                && !AllowedPaths.Where(IsPath).Any(allowed => Covers(allowed, reachable))))
            {
                yield return new ValidationResult(
                    $"The blocked path '{blocked}' would refuse '{reachable}', which must stay reachable while the demo is off.",
                    [nameof(BlockedPaths)]);
            }
        }

        foreach (var allowed in AllowedPaths.Where(IsPath).Where(allowed => !BlockedPaths.Where(IsPath).Any(blocked => Covers(blocked, allowed))))
        {
            yield return new ValidationResult(
                $"The allowed path '{allowed}' is not below any blocked path, so it has no effect.", [nameof(AllowedPaths)]);
        }
    }

    private static bool IsPath(string path) => !string.IsNullOrWhiteSpace(path) && path.StartsWith('/');

    /// <summary>True when <paramref name="path"/> is <paramref name="prefix"/> or below it (whole segments, any casing), as the middleware matches.</summary>
    private static bool Covers(string prefix, string path) =>
        new Microsoft.AspNetCore.Http.PathString(path).StartsWithSegments(prefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
        || prefix == "/";
}
