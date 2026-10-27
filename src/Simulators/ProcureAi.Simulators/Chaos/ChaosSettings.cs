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

/// <summary>The chaos switch: how the supplier and the ERP misbehave (section <c>Simulators:Chaos</c>).</summary>
public sealed class ChaosConfiguration
{
    public ChaosSettings Supplier { get; set; } = new();

    public ChaosSettings Erp { get; set; } = new();

    public IEnumerable<(string Property, string Message)> Validate() =>
        Supplier.Validate("supplier").Concat(Erp.Validate("erp"));
}
