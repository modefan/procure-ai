using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace ProcureAi.Purchasing.Infrastructure.Supplier;

/// <summary>
/// The <c>SupplierClient</c> section: where the supplier API is, the key we present to it, and the resilience pipeline of every
/// call (attempt timeout, retries with exponential backoff and jitter, circuit breaker, total timeout). The values live in
/// appsettings; the key comes from a secret.
/// </summary>
public sealed class SupplierClientOptions
{
    public const string SectionName = "SupplierClient";

    [Required]
    public Uri? BaseUrl { get; set; }

    /// <summary>Sent in <c>X-Api-Key</c>. From user secrets locally and a Kubernetes secret when deployed; never committed.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ApiKey { get; set; } = "";

    /// <summary>One attempt gives up after this long.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:01:00")]
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Retries of a failed attempt (exponential backoff with jitter), on top of the first attempt.</summary>
    [Range(1, 5)]
    public int RetryCount { get; set; } = 2;

    /// <summary>The base delay of the exponential backoff between retries.</summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:00:30")]
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The whole call, retries included, gives up after this long.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:05:00")]
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(20);

    [Required, ValidateObjectMembers]
    public SupplierCircuitBreakerOptions CircuitBreaker { get; set; } = new();
}

/// <summary>When the circuit to the supplier opens: a share of failed calls among enough calls in the sampling window.</summary>
public sealed class SupplierCircuitBreakerOptions
{
    [Range(0.01, 1.0)]
    public double FailureRatio { get; set; } = 0.5;

    [Range(2, 1000)]
    public int MinimumThroughput { get; set; } = 4;

    [Range(typeof(TimeSpan), "00:00:00.500", "00:10:00")]
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the circuit stays open before one trial call is let through.</summary>
    [Range(typeof(TimeSpan), "00:00:00.500", "00:10:00")]
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(20);
}
