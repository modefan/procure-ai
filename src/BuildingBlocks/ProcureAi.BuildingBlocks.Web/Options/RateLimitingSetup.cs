using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ProcureAi.BuildingBlocks.Web.Options;

internal static class RateLimitingSetup
{
    private const string UnknownClient = "unknown";
    private const string HealthPrefix = "/health";

    public static IServiceCollection AddProcureAiRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>()
            .Configure<IOptions<RateLimitingOptions>>((limiter, settings) =>
            {
                var rate = settings.Value;
                limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    context.Request.Path.StartsWithSegments(HealthPrefix)
                        ? RateLimitPartition.GetNoLimiter(HealthPrefix)
                        : RateLimitPartition.GetFixedWindowLimiter(
                            context.Connection.RemoteIpAddress?.ToString() ?? UnknownClient,
                            _ => new FixedWindowRateLimiterOptions
                            {
                                PermitLimit = rate.PermitLimit,
                                Window = TimeSpan.FromSeconds(rate.WindowSeconds),
                                QueueLimit = rate.QueueLimit,
                                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            }));
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
