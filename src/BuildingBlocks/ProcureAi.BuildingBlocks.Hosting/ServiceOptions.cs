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
}
