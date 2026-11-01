using System.ComponentModel.DataAnnotations;

namespace ProcureAi.BuildingBlocks.Web.Demo;

/// <summary>
/// Section <c>DemoReset</c>. Off by default: the endpoint does not exist (404) unless a demo deployment (the AppHost, later the demo
/// Kubernetes overlay) switches it on with <c>DemoReset__Enabled=true</c>. The nightly time lives with the caller (ProcureAi.DemoData).
/// Bound and validated at startup by <see cref="DemoResetSetup.AddProcureAiDemoResetOptions"/>.
/// </summary>
public sealed class DemoResetOptions
{
    public const string SectionName = "DemoReset";

    /// <summary>
    /// The longest <see cref="DrainTimeout"/> allowed: below ProcureAi.DemoData's <c>DemoData:ResetCallTimeout</c> (2 min), so a service
    /// that cannot drain answers 503 (and the caller retries) before the caller gives up on the call.
    /// </summary>
    public const string MaxDrainTimeout = "00:01:45";

    public bool Enabled { get; set; }

    /// <summary>
    /// A service that receives what the others' resets cause (Audit: their change data capture) first waits until its queues and inbox have
    /// been empty for <see cref="QuietPeriod"/>, at most <see cref="DrainTimeout"/>; then it clears. Clearing in the middle of a transaction's
    /// changes would leave half of them, later matched without their other half: false "changed outside the app" alerts.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:01:00")]
    public TimeSpan QuietPeriod { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How long the wait for <see cref="QuietPeriod"/> may take before the reset fails (503, so the caller retries).</summary>
    [Range(typeof(TimeSpan), "00:00:01", MaxDrainTimeout)]
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>How often the queues and the inbox are looked at while waiting for <see cref="QuietPeriod"/>.</summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:00:10")]
    public TimeSpan DrainPollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
}
