using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Web.Auth;

namespace ProcureAi.BuildingBlocks.Web.RateLimiting;

public static class RateLimitingSetup
{
    private const string HealthPrefix = "/health";

    public static IServiceCollection AddProcureAiRateLimiting(
        this IServiceCollection services, RateLimitPartitioning partitioning = RateLimitPartitioning.UserAndTenant)
    {
        services.AddRateLimiter(_ => { });
        services.AddSingleton(sp => new RateLimitPartitionResolver(
            partitioning, partitioning == RateLimitPartitioning.UserAndTenant ? sp.GetRequiredService<ApiKeyValidator>() : null));
        services.AddOptions<RateLimiterOptions>()
            .Configure<IOptions<RateLimitingOptions>, RateLimitPartitionResolver>((limiter, settings, resolver) =>
            {
                var rate = settings.Value;
                limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                {
                    if (context.Request.Path.StartsWithSegments(HealthPrefix))
                    {
                        return RateLimitPartition.GetNoLimiter(HealthPrefix);
                    }

                    var (partition, isAdminKey) = resolver.Resolve(context);
                    var permitLimit = isAdminKey ? rate.AdminApiKeyPermitLimit ?? rate.PermitLimit : rate.PermitLimit;
                    return RateLimitPartition.GetFixedWindowLimiter(
                        partition,
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = permitLimit,
                            Window = TimeSpan.FromSeconds(rate.WindowSeconds),
                            QueueLimit = rate.QueueLimit,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        });
                });
                limiter.OnRejected = async (rejected, _) =>
                {
                    var http = rejected.HttpContext;
                    if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    {
                        http.Response.Headers.RetryAfter =
                            ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                    }

                    await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(
                        new ProblemDetailsContext
                        {
                            HttpContext = http,
                            ProblemDetails = new ProblemDetails
                            {
                                Status = StatusCodes.Status429TooManyRequests,
                                Title = "Too many requests",
                            },
                        });
                };
            });
        return services;
    }
}
