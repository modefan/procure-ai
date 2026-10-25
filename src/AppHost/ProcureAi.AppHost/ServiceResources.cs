using Aspire.Hosting.ApplicationModel;

namespace ProcureAi.AppHost;

/// <summary>What one service needs from the AppHost. Everything else comes from <see cref="SharedResources"/>.</summary>
/// <param name="Name">Short service name, e.g. <c>requests</c>; the resources are called <c>&lt;name&gt;-migrate</c> and <c>&lt;name&gt;-api</c>.</param>
/// <param name="Database">The service's Postgres database, e.g. <c>requestsdb</c> (also its connection-string name).</param>
/// <param name="CapturedTables">Tables Debezium captures for the audit trail (wired by <c>AddDebeziumFor</c>); empty when none.</param>
/// <param name="GatewayCluster">The YARP cluster the gateway routes this service's traffic to, e.g. <c>requests</c>.</param>
/// <param name="ApiRoutePrefix">The path the gateway exposes the service under, e.g. <c>/api/requests</c>.</param>
public sealed record ServiceSpec(string Name, string Database, string[] CapturedTables, string GatewayCluster, string ApiRoutePrefix);

/// <summary>The infrastructure and settings every service resource is wired to.</summary>
/// <param name="InternalClientSecret">Secret of the Keycloak client services use to call each other; <c>null</c> until a service needs it.</param>
/// <param name="DebeziumPassword">Password of the Postgres <c>debezium</c> role (secret parameter), for every Debezium Server.</param>
/// <param name="UseDataVolumes">Whether containers keep their data in named volumes (offsets of Debezium).</param>
/// <param name="OtlpEndpoint">The Aspire dashboard's OTLP endpoint (the Serilog OpenTelemetry sink has no environment fallback).</param>
public sealed record SharedResources(
    IResourceBuilder<PostgresServerResource> Postgres,
    IResourceBuilder<RabbitMQServerResource> RabbitMq,
    IResourceBuilder<RedisResource> Redis,
    IResourceBuilder<ContainerResource> Keycloak,
    ReferenceExpression KeycloakAuthority,
    ParameterResource AdminApiKeySha256,
    ParameterResource? InternalClientSecret,
    string? OtlpEndpoint,
    ResourcesOptions Limits,
    ParameterResource DebeziumPassword,
    bool UseDataVolumes);

public static class ServiceResources
{
    public const string MigrateSuffix = "-migrate";
    public const string ApiSuffix = "-api";

    private const string DebeziumPrefix = "debezium-";
    private const string DebeziumImage = "quay.io/debezium/server";
    private const string DebeziumTag = "3.7.0.Final";
    private const int DebeziumHttpPort = 8080;

    /// <summary>
    /// Adds a service the one way every service is added: its database, a short-lived <c>&lt;name&gt;-migrate</c> resource
    /// (the same project with <c>--migrate</c>: schema, Wolverine message store and demo seed data) and the
    /// <c>&lt;name&gt;-api</c> resource, which waits for the migration to finish and reports readiness on <c>/health/ready</c>.
    /// A separate resource is the simplest ordering Aspire offers (<c>WaitForCompletion</c>), it mirrors the Kubernetes job /
    /// Argo CD PreSync hook of the later deployment, and the API itself never changes the schema at startup.
    /// Returns the API so the gateway can reference it.
    /// </summary>
    public static IResourceBuilder<ProjectResource> AddProcureAiService<TProject>(
        this IDistributedApplicationBuilder builder, ServiceSpec spec, SharedResources shared)
        where TProject : IProjectMetadata, new()
    {
        // Aspire creates the database on the server. The Postgres init script only creates the debezium role; the
        // migration creates the publication and grants (CdcMigrations.EnableCdc).
        var database = shared.Postgres.AddDatabase(spec.Database);

        var migrate = builder.AddProject<TProject>(spec.Name + MigrateSuffix)
            .WithArgs("--migrate")
            .WithEnvironment("DemoSeed__Enabled", "true")
            .WithReference(database)
            .WithReference(shared.RabbitMq)
            .WithReference(shared.Redis)
            .WaitFor(database)
            .WithDotNetLimits(shared.Limits.DotNet);

        var api = builder.AddProject<TProject>(spec.Name + ApiSuffix)
            // No launchSettings.json: Aspire picks a free port, so dev runs and test runs never clash.
            .WithHttpEndpoint(name: "http")
            .WithReference(database)
            .WithReference(shared.RabbitMq)
            .WithReference(shared.Redis)
            .WithEnvironment("Auth__Authority", shared.KeycloakAuthority)
            // The local Keycloak container speaks plain http; the deployed authority is https and keeps the default (true).
            .WithEnvironment("Auth__RequireHttpsMetadata", "false")
            .WithEnvironment("Auth__OpenApi__AuthorizationUrl", ReferenceExpression.Create($"{shared.KeycloakAuthority}/protocol/openid-connect/auth"))
            .WithEnvironment("Auth__OpenApi__TokenUrl", ReferenceExpression.Create($"{shared.KeycloakAuthority}/protocol/openid-connect/token"))
            .WithEnvironment("Auth__AdminApiKeySha256", shared.AdminApiKeySha256)
            .WithOtlpEndpoint(shared.OtlpEndpoint)
            .WaitFor(shared.Keycloak)
            .WaitFor(shared.RabbitMq)
            .WaitFor(shared.Redis)
            .WaitForCompletion(migrate)
            .WithHttpHealthCheck("/health/ready")
            .WithDotNetLimits(shared.Limits.DotNet);

        if (spec.CapturedTables.Length > 0)
        {
            builder.AddDebeziumFor(spec.Database, spec.CapturedTables, migrate, shared);
        }

        if (shared.InternalClientSecret is { } secret)
        {
            api.WithEnvironment("Auth__InternalClientSecret", secret);
        }

        return api;
    }

    /// <summary>
    /// Adds the Debezium Server for one database: change data capture from the Postgres WAL to the <c>cdc</c> exchange.
    /// Debezium Server runs exactly one source connector per instance, so every captured database gets its own container
    /// <c>debezium-&lt;database&gt;</c> (Java heap capped, limits from <c>Resources:Debezium</c>) from the one shared
    /// <c>application.properties</c>, plus per-database environment variables. It starts after the service's migrate
    /// resource finished (which created the publication) and after RabbitMQ (which declares the exchange).
    /// Routing keys are <c>&lt;database&gt;.public.&lt;table&gt;</c>.
    /// </summary>
    public static IResourceBuilder<ContainerResource> AddDebeziumFor(
        this IDistributedApplicationBuilder builder,
        string database,
        string[] tables,
        IResourceBuilder<ProjectResource> migrate,
        SharedResources shared)
    {
        var debezium = builder.AddContainer(DebeziumPrefix + database, DebeziumImage, DebeziumTag)
            .WithHttpEndpoint(targetPort: DebeziumHttpPort, name: "http")
            .WithBindMount("../../../infra/debezium/application.properties", "/debezium/config/application.properties", isReadOnly: true)
            .WithEnvironment("JAVA_OPTS", shared.Limits.Debezium.JavaOpts)
            .WithEnvironment("DEBEZIUM_SOURCE_DATABASE_PASSWORD", shared.DebeziumPassword)
            .WithEnvironment("DEBEZIUM_SOURCE_DATABASE_DBNAME", database)
            .WithEnvironment("DEBEZIUM_SOURCE_TOPIC_PREFIX", database)
            .WithEnvironment("DEBEZIUM_SOURCE_SLOT_NAME", "debezium_" + database)
            .WithEnvironment("DEBEZIUM_SOURCE_TABLE_INCLUDE_LIST", string.Join(',', tables.Select(t => "public." + t)))
            .WithEnvironment("DEBEZIUM_SINK_RABBITMQ_CONNECTION_USERNAME", shared.RabbitMq.Resource.UserNameReference)
            .WithEnvironment("DEBEZIUM_SINK_RABBITMQ_CONNECTION_PASSWORD", shared.RabbitMq.Resource.PasswordParameter)
            .WithHttpHealthCheck("/q/health")
            .WaitForCompletion(migrate)
            .WaitFor(shared.RabbitMq)
            .WithContainerLimits(shared.Limits.Debezium);
        if (shared.UseDataVolumes)
        {
            debezium.WithVolume($"{DebeziumPrefix}{database}-data", "/debezium/data");
        }

        return debezium;
    }

    /// <summary>The Serilog OpenTelemetry sink has no environment-variable fallback, so it is pointed at the dashboard here.</summary>
    public static IResourceBuilder<T> WithOtlpEndpoint<T>(this IResourceBuilder<T> builder, string? otlpEndpoint)
        where T : IResourceWithEnvironment =>
        builder.WithEnvironment(context =>
        {
            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            {
                context.EnvironmentVariables["Serilog__WriteTo__OpenTelemetry__Args__endpoint"] = otlpEndpoint;
            }
        });
}
