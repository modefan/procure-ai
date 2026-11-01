using Microsoft.Extensions.Configuration;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ProcureAi.AppHost;

/// <summary>
/// Resource caps for everything the AppHost starts (the PC is shared while work runs).
/// Bound from the <c>Resources</c> section of appsettings; no numbers live in code.
/// <c>tests/testcontainers.settings.json</c> carries the same values for Testcontainers.
/// </summary>
public sealed class ResourcesOptions
{
    public const string SectionName = "Resources";

    [Required, ValidateObjectMembers] public PostgresLimits Postgres { get; set; } = null!;
    [Required, ValidateObjectMembers] public MongoLimits Mongo { get; set; } = null!;
    [Required, ValidateObjectMembers] public KeycloakLimits Keycloak { get; set; } = null!;
    [Required, ValidateObjectMembers] public DebeziumLimits Debezium { get; set; } = null!;
    [Required, ValidateObjectMembers] public RabbitMqLimits RabbitMq { get; set; } = null!;
    [Required, ValidateObjectMembers] public RedisLimits Redis { get; set; } = null!;
    [Required, ValidateObjectMembers] public ContainerLimits Mailpit { get; set; } = null!;
    [Required, ValidateObjectMembers] public DotNetLimits DotNet { get; set; } = null!;
    [Required, ValidateObjectMembers] public AngularLimits Angular { get; set; } = null!;

    /// <summary>Binds and validates the section with the options pattern (throws on invalid configuration).</summary>
    public static ResourcesOptions Load(IConfiguration configuration)
    {
        using var provider = new ServiceCollection()
            .AddOptions<ResourcesOptions>()
            .Bind(configuration.GetSection(SectionName))
            .ValidateDataAnnotations()
            .Services.BuildServiceProvider();

        // Reading .Value runs the validation right here (no host, so ValidateOnStart would not apply).
        return provider.GetRequiredService<IOptions<ResourcesOptions>>().Value;
    }
}

public class ContainerLimits
{
    [Range(16, 8192)] public int MemoryMb { get; set; }
    [Range(0.1, 8)] public double Cpus { get; set; }
}

public sealed class PostgresLimits : ContainerLimits
{
    [Required] public string SharedBuffers { get; set; } = null!;
    [Required] public string WorkMem { get; set; } = null!;

    /// <summary>
    /// The server's <c>max_connections</c>. Ten services share one server locally, each with a few connection pools (EF Core, Wolverine,
    /// Marten), and the seed's burst took more than Postgres's default of 100 (53300 "too many clients"). A deployment uses its own value.
    /// </summary>
    [Range(20, 1000)] public int MaxConnections { get; set; } = 100;

    /// <summary>
    /// <c>Database__MaxPoolSize</c> of every service with a Postgres database: it caps <b>each</b> Npgsql pool the service opens, and a
    /// service opens two or three (see <c>PoolsOf</c> in ServiceResourcesTests). All pools times this value, plus the Debezium servers
    /// (one connection each), Keycloak and <see cref="ReservedConnections"/>, must fit <see cref="MaxConnections"/> (a test checks it).
    /// </summary>
    [Range(1, 100)] public int PoolSizePerService { get; set; } = 8;

    /// <summary>Connections kept free for operators, migrations, pgaudit reads and the Debezium snapshots.</summary>
    [Range(0, 200)] public int ReservedConnections { get; set; } = 30;
}

public sealed class MongoLimits : ContainerLimits
{
    [Range(0.25, 8)] public double WiredTigerCacheGb { get; set; }
}

public sealed class KeycloakLimits : ContainerLimits
{
    [Required] public string JavaHeapOpts { get; set; } = null!;
}

public sealed class DebeziumLimits : ContainerLimits
{
    [Required] public string JavaOpts { get; set; } = null!;
}

public sealed class RabbitMqLimits : ContainerLimits
{
    [Required] public string VmMemoryHighWatermarkAbsolute { get; set; } = null!;
}

public sealed class RedisLimits : ContainerLimits
{
    [Required] public string MaxMemory { get; set; } = null!;
    [Required] public string MaxMemoryPolicy { get; set; } = null!;
}

public sealed class DotNetLimits
{
    [Range(0, 1)] public int GcServer { get; set; }
    [Range(0, 9)] public int GcConserveMemory { get; set; }
    [Required] public string GcHeapHardLimit { get; set; } = null!;
}

public sealed class AngularLimits
{
    [Required] public string NodeOptions { get; set; } = null!;
}
