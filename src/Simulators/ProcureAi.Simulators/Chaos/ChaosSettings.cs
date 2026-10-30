namespace ProcureAi.Simulators.Chaos;

public enum ChaosMode
{
    /// <summary>Latency only.</summary>
    Normal,

    /// <summary>Latency times five.</summary>
    Slow,

    /// <summary><see cref="ChaosSettings.FailureRate"/> of the calls answer 503.</summary>
    Failing,

    /// <summary>Every call answers 503 after 100 ms.</summary>
    Down,
}

public enum ChaosTarget
{
    Supplier,
    Erp,
}

/// <summary>How one simulated system misbehaves. Used as configuration and as the body of the chaos endpoint.</summary>
public sealed class ChaosSettings
{
    public static readonly TimeSpan MaxAllowedLatency = TimeSpan.FromSeconds(30);

    public ChaosMode Mode { get; set; } = ChaosMode.Normal;

    public double FailureRate { get; set; } = 0.1;

    public TimeSpan MinLatency { get; set; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan MaxLatency { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// When the mode returns to Normal by itself (<see cref="ChaosRestoreOptions.AutoRestoreAfter"/> after it was switched away from Normal);
    /// null in Normal. Set by the simulator only: neither the body of a PUT nor configuration can set it (no public setter).
    /// </summary>
    public DateTimeOffset? RestoresAt { get; internal set; }

    public ChaosSettings Clone() => (ChaosSettings)MemberwiseClone();

    /// <summary>The problems with these settings, by property name (empty when they are fine).</summary>
    public IEnumerable<(string Property, string Message)> Validate(string prefix)
    {
        if (!Enum.IsDefined(Mode))
        {
            yield return ($"{prefix}.mode", "Unknown mode.");
        }

        if (double.IsNaN(FailureRate) || FailureRate is < 0 or > 1)
        {
            yield return ($"{prefix}.failureRate", "Must be between 0 and 1.");
        }

        if (MinLatency < TimeSpan.Zero)
        {
            yield return ($"{prefix}.minLatency", "Must not be negative.");
        }

        if (MaxLatency < MinLatency)
        {
            yield return ($"{prefix}.maxLatency", "Must not be less than minLatency.");
        }

        if (MaxLatency > MaxAllowedLatency)
        {
            yield return ($"{prefix}.maxLatency", $"Must not exceed {MaxAllowedLatency} (it is multiplied by five in Slow mode).");
        }
    }
}

/// <summary>
/// Section <c>Simulators:Chaos</c> as well: how long a chaos mode lasts before the simulator restores Normal by itself, so a reviewer who
/// broke the supplier from the status screen cannot leave the demo broken (plan 06 ruling 11).
/// </summary>
public sealed class ChaosRestoreOptions
{
    public const string Section = SimulatorOptions.Section + ":Chaos";

    public TimeSpan AutoRestoreAfter { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>The chaos switch: how the supplier and the ERP misbehave (section <c>Simulators:Chaos</c>).</summary>
public sealed class ChaosConfiguration
{
    public ChaosSettings Supplier { get; set; } = new();

    public ChaosSettings Erp { get; set; } = new();

    public IEnumerable<(string Property, string Message)> Validate() =>
        Supplier.Validate("supplier").Concat(Erp.Validate("erp"));
}
