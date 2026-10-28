using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using ProcureAi.Gateway.Configuration;

namespace ProcureAi.Gateway.Bff;

/// <summary>
/// The built-in fixed-window limiter, partitioned by session (the verified user of the session cookie) and by client IP without
/// one. It lives here, not in the middleware pipeline, because the pipeline's limiter runs before authentication and could
/// only key on a cookie value anybody can forge. Idle partitions are removed by the limiter itself.
/// </summary>
public sealed class ClientLogRateLimiter(IOptions<ClientLogOptions> options) : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(partition =>
        RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = options.Value.PermitPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));

    /// <summary>Takes one permit; on refusal says how long to wait.</summary>
    public bool TryAcquire(string partition, out TimeSpan retryAfter)
    {
        using var lease = _limiter.AttemptAcquire(partition);
        retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : TimeSpan.FromMinutes(1);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
