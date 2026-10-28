using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Notifications.Application.Presence;

/// <summary>
/// <c>Notifications:Presence</c>: how often clients send <c>Heartbeat()</c>, how long a connection counts as online without one, how
/// often stale connections are swept (by one instance at a time), and how many entities one connection may watch.
/// </summary>
public sealed class PresenceOptions : IValidatableObject
{
    public const string SectionName = "Notifications:Presence";

    /// <summary>How often the client sends <c>Heartbeat()</c> (the Angular client uses this value too). Default 15 s.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A connection without a heartbeat for this long is no longer online or viewing (a closed laptop, a killed browser). At least two
    /// heartbeat intervals, so one late heartbeat never drops a user. Default 45 s.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:02", "01:00:00")]
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>How often an instance tries to sweep stale connections (one instance at a time, under a distributed lock). Default 15 s.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long a sweep waits for another instance's sweep to finish before it skips its turn. Default 5 s.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:01:00")]
    public TimeSpan SweepLockWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How many entities one connection may watch at once (a page shows one or two). Default 20.</summary>
    [Range(1, 200)]
    public int MaxWatchesPerConnection { get; set; } = 20;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Ttl < 2 * HeartbeatInterval)
        {
            yield return new ValidationResult(
                $"Ttl ({Ttl}) must be at least twice the HeartbeatInterval ({HeartbeatInterval}).", [nameof(Ttl), nameof(HeartbeatInterval)]);
        }
    }
}

/// <summary>
/// The clock of presence (heartbeat times, TTL, the sweeper's timer): a keyed <see cref="TimeProvider"/>, so tests can drive it without
/// freezing the clock of everything else (HTTP resilience timeouts, tokens).
/// </summary>
public static class PresenceClock
{
    public const string Key = "presence";
}
