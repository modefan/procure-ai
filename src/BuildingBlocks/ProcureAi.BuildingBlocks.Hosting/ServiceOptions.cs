using System.ComponentModel.DataAnnotations;

namespace ProcureAi.BuildingBlocks.Hosting;

/// <summary>The <c>Service</c> section every service's appsettings carries: what the shared composition root cannot know.</summary>
public sealed class ServiceOptions
{
    public const string SectionName = "Service";

    /// <summary>
    /// The name of the service's database connection string (<c>ConnectionStrings:&lt;name&gt;</c>), e.g. <c>requestsdb</c>.
    /// The readiness check probes it; AppHost and the OpenAPI build step set the same name.
    /// </summary>
    [Required]
    public string DatabaseConnectionName { get; set; } = null!;

    /// <summary>
    /// What the database is: decides the readiness check (Postgres <c>SELECT 1</c> or a MongoDB ping) and what <c>--migrate</c> runs
    /// (the EF Core migrations and Wolverine's message store, or the registered <c>IMongoMigration</c>s). The AppHost sets it
    /// (<c>Service__Store</c>) from the service's <c>ServiceSpec</c>.
    /// </summary>
    public ServiceStore Store { get; set; } = ServiceStore.Postgres;
}

/// <summary>The kind of database a service owns.</summary>
public enum ServiceStore
{
    Postgres,
    MongoDB,
}
