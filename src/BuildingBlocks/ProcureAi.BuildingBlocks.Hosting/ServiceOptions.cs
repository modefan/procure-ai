using System.ComponentModel.DataAnnotations;

namespace ProcureAi.BuildingBlocks.Hosting;

/// <summary>The <c>Service</c> section every service's appsettings carries: what the shared composition root cannot know.</summary>
public sealed class ServiceOptions : IValidatableObject
{
    public const string SectionName = "Service";

    /// <summary>
    /// The name of the service's database connection string (<c>ConnectionStrings:&lt;name&gt;</c>), e.g. <c>requestsdb</c>.
    /// The readiness check probes it; AppHost and the OpenAPI build step set the same name. Required unless <see cref="Store"/> is
    /// <see cref="ServiceStore.None"/>.
    /// </summary>
    public string? DatabaseConnectionName { get; set; }

    /// <summary>
    /// What the database is: decides the readiness check (Postgres <c>SELECT 1</c>, a MongoDB ping, or none) and what <c>--migrate</c>
    /// runs (the EF Core migrations and Wolverine's message store, the registered <c>IMongoMigration</c>s, or nothing). The AppHost sets
    /// it (<c>Service__Store</c>) from the service's <c>ServiceSpec</c>.
    /// </summary>
    public ServiceStore Store { get; set; } = ServiceStore.Postgres;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Store != ServiceStore.None && string.IsNullOrWhiteSpace(DatabaseConnectionName))
        {
            yield return new ValidationResult(
                $"'{nameof(DatabaseConnectionName)}' is required for a service whose store is {Store}.", [nameof(DatabaseConnectionName)]);
        }
    }
}

/// <summary>The kind of database a service owns.</summary>
public enum ServiceStore
{
    Postgres,
    MongoDB,

    /// <summary>
    /// No database at all (Notifications): no readiness check for one, nothing for <c>--migrate</c> to do, and no message store, so no
    /// durable inbox or outbox. Its Wolverine listeners are buffered.
    /// </summary>
    None,
}
