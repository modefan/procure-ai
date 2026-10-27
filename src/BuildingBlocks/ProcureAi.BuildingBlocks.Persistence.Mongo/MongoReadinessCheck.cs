using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ProcureAi.BuildingBlocks.Persistence.Mongo;

/// <summary>Readiness: the MongoDB server answers a <c>ping</c> (the counterpart of the Postgres <c>SELECT 1</c> check).</summary>
public sealed class MongoReadinessCheck(IMongoClient client) : IHealthCheck
{
    /// <summary>How long a probe may take before it counts as unhealthy (the same as the other readiness checks).</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("MongoDB is not reachable.", exception);
        }
    }

    /// <summary>Adds the check to <c>/health/ready</c>.</summary>
    public static IServiceCollection AddMongoReadiness(IServiceCollection services)
    {
        services.AddHealthChecks().Add(new HealthCheckRegistration(
            "mongodb", sp => new MongoReadinessCheck(sp.GetRequiredService<IMongoClient>()), HealthStatus.Unhealthy, [Extensions.ReadyTag], Timeout));
        return services;
    }
}
