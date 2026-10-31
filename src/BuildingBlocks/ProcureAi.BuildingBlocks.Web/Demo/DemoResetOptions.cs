namespace ProcureAi.BuildingBlocks.Web.Demo;

/// <summary>
/// Section <c>DemoReset</c>. Off by default: the endpoint does not exist (404) unless a demo deployment (the AppHost, later the demo
/// Kubernetes overlay) switches it on with <c>DemoReset__Enabled=true</c>. The nightly time lives with the caller (ProcureAi.DemoData).
/// </summary>
public sealed class DemoResetOptions
{
    public const string SectionName = "DemoReset";

    public bool Enabled { get; set; }

    /// <summary>
    /// A service that receives what the others' resets cause (Audit: their change data capture) first waits until its queues and inbox have
    /// been empty for <see cref="QuietPeriod"/>, at most <see cref="DrainTimeout"/>; then it clears. Clearing in the middle of a transaction's
    /// changes would leave half of them, later matched without their other half: false "changed outside the app" alerts.
    /// </summary>
    public TimeSpan QuietPeriod { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How long the wait for <see cref="QuietPeriod"/> may take before the reset fails (503, so the caller retries).</summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
