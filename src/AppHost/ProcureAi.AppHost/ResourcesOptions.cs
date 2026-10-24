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
