using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using ProcureAi.Gateway.Configuration;

namespace ProcureAi.Gateway.Bff;

/// <summary>
/// The <c>demo-entry</c> rate-limit policy (plan 07 Task 0a, plan 06 carry-overs 1 and 2): <c>RateLimiting:DemoEntry:PermitLimit</c> calls per
/// <c>RateLimiting:DemoEntry:Window</c> for each endpoint that carries it and each client IP. The IP is the connection's after the forwarded
/// headers (<c>UseProcureAiForwardedHeaders</c> runs before the limiter), so behind a trusted ingress it is the browser's, and a caller that
/// is not a trusted proxy cannot pick its own partition with <c>X-Forwarded-For</c>. A refusal is the pipeline's 429 with <c>Retry-After</c>.
/// </summary>
public static class DemoEntryRateLimit
{
    public const string PolicyName = "demo-entry";

    private const string UnknownClient = "unknown";

    public static IServiceCollection AddDemoEntryRateLimit(this IServiceCollection services)
    {
        services.AddOptions<RateLimiterOptions>()
            .Configure<IOptions<DemoEntryRateLimitOptions>>((limiter, options) =>
                limiter.AddPolicy(PolicyName, http => RateLimitPartition.GetFixedWindowLimiter(
                    // The endpoint, not the path: /BFF/Demo-Login/ reaches the same endpoint and must not get an allowance of its own.
                    $"{http.GetEndpoint()?.DisplayName}|{http.Connection.RemoteIpAddress?.ToString() ?? UnknownClient}",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.Value.PermitLimit,
                        Window = options.Value.Window,
                        QueueLimit = 0,
                    })));
        return services;
    }
}
