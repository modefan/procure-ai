using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Npgsql;
using RabbitMQ.Client;
using StackExchange.Redis;

namespace ProcureAi.BuildingBlocks.Web.Health;

/// <summary>
/// Readiness checks (<c>/health/ready</c>) that a service opts into for the dependencies it needs to serve traffic.
/// Liveness (<c>/health/live</c>) stays process-only: a restart would not bring a database back.
/// </summary>
public static class ReadinessChecks
{
    /// <summary>How long a probe may take before it counts as unhealthy.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>Opens a connection to Postgres and runs <c>SELECT 1</c>.</summary>
    public static IServiceCollection AddPostgresReadiness(this IServiceCollection services, string connectionString) =>
        Add(services, "postgres", _ => new PostgresCheck(connectionString));

    /// <summary>Opens an AMQP connection to RabbitMQ (<c>amqp://user:password@host:port</c>).</summary>
    public static IServiceCollection AddRabbitMqReadiness(this IServiceCollection services, string connectionString) =>
        Add(services, "rabbitmq", _ => new RabbitMqCheck(new Uri(connectionString)));

    /// <summary>Pings Redis through the registered <see cref="IConnectionMultiplexer"/>.</summary>
    public static IServiceCollection AddRedisReadiness(this IServiceCollection services) =>
        Add(services, "redis", sp => new RedisCheck(sp.GetRequiredService<IConnectionMultiplexer>()));

    private static IServiceCollection Add(IServiceCollection services, string name, Func<IServiceProvider, IHealthCheck> factory)
    {
        services.AddHealthChecks().Add(
            new HealthCheckRegistration(name, factory, HealthStatus.Unhealthy, [Extensions.ReadyTag], Timeout));
        return services;
    }

    private sealed class PostgresCheck(string connectionString) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new NpgsqlCommand("SELECT 1", connection);
                await command.ExecuteScalarAsync(cancellationToken);
                return HealthCheckResult.Healthy();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return HealthCheckResult.Unhealthy("Postgres is not reachable.", exception);
            }
        }
    }

    private sealed class RabbitMqCheck(Uri uri) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                var factory = new ConnectionFactory { Uri = uri };
                await using var connection = await factory.CreateConnectionAsync(cancellationToken);
                return HealthCheckResult.Healthy();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return HealthCheckResult.Unhealthy("RabbitMQ is not reachable.", exception);
            }
        }
    }

    private sealed class RedisCheck(IConnectionMultiplexer redis) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                // The multiplexer reconnects in the background (AbortOnConnectFail is off), so check before pinging.
                if (!redis.IsConnected)
                {
                    return HealthCheckResult.Unhealthy("Redis is not connected.");
                }

                await redis.GetDatabase().PingAsync().WaitAsync(cancellationToken);
                return HealthCheckResult.Healthy();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return HealthCheckResult.Unhealthy("Redis is not reachable.", exception);
            }
        }
    }
}
