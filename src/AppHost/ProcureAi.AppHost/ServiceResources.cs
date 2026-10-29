using Aspire.Hosting.ApplicationModel;

namespace ProcureAi.AppHost;

/// <summary>Which kind of server holds a service's database. The value is passed as <c>Service__Store</c>; the names match <c>ProcureAi.BuildingBlocks.Hosting.ServiceStore</c> (the AppHost does not reference the Hosting library).</summary>
public enum ServiceStore
{
    Postgres,
    MongoDB,

    /// <summary>No database (Notifications): no database resource, no <c>-migrate</c> resource, no Debezium.</summary>
    None,
}

/// <summary>What one service needs from the AppHost. Everything else comes from <see cref="SharedResources"/>.</summary>
/// <param name="Name">Short service name, e.g. <c>requests</c>; the resources are called <c>&lt;name&gt;-migrate</c> and <c>&lt;name&gt;-api</c>.</param>
/// <param name="Database">The service's database on the Postgres or MongoDB server, e.g. <c>requestsdb</c> (also its connection-string name); unused for <see cref="ServiceStore.None"/>.</param>
/// <param name="CapturedTables">Tables Debezium captures for the audit trail (wired by <c>AddDebeziumFor</c>); empty when none.</param>
/// <param name="GatewayCluster">The YARP cluster the gateway routes this service's traffic to, e.g. <c>requests</c>.</param>
/// <param name="ApiRoutePrefix">The path the gateway exposes the service under, e.g. <c>/api/requests</c>.</param>
/// <param name="Store">Which server holds the database, or <see cref="ServiceStore.None"/>. MongoDB services and services without a database have no Debezium (it reads the Postgres WAL), so <c>CapturedTables</c> must be empty.</param>
/// <param name="SecondaryMongoDatabase">A second database on the <c>mongo</c> resource next to a Postgres store (the Assistant's chat history,
/// <c>assistantchatdb</c>), also its connection-string name; null for none. Only for <see cref="ServiceStore.Postgres"/>.</param>
public sealed record ServiceSpec(
    string Name, string Database, string[] CapturedTables, string GatewayCluster, string ApiRoutePrefix, ServiceStore Store = ServiceStore.Postgres,
    string? SecondaryMongoDatabase = null);

/// <summary>The infrastructure and settings every service resource is wired to.</summary>
/// <param name="InternalClientSecret">Secret of the Keycloak client <c>procure-internal</c>, with which services call each other (<c>Auth:Internal:ClientSecret</c>).</param>
/// <param name="AccessAdminClientSecret">Secret of the Keycloak client <c>procure-access-admin</c> (Keycloak admin API). Not passed to any service here: only the Access service gets it.</param>
/// <param name="DebeziumPassword">Password of the Postgres <c>debezium</c> role (secret parameter), for every Debezium Server.</param>
/// <param name="UseDataVolumes">Whether containers keep their data in named volumes (offsets of Debezium).</param>
/// <param name="DebeziumSelfHealLostOffsets">Config <c>Debezium:SelfHealLostOffsets</c>: when a Debezium Server's stored offset is no longer available
/// (slot gone, database reset), drop it and stream from a new slot instead of failing. Leaves a CDC gap; local runs only.</param>
/// <param name="OtlpEndpoint">The Aspire dashboard's OTLP endpoint (the Serilog OpenTelemetry sink has no environment fallback).</param>
public sealed record SharedResources(
    IResourceBuilder<PostgresServerResource> Postgres,
    IResourceBuilder<RabbitMQServerResource> RabbitMq,
    IResourceBuilder<RedisResource> Redis,
    IResourceBuilder<MongoDBServerResource> Mongo,
    IResourceBuilder<ContainerResource> Keycloak,
    ReferenceExpression KeycloakAuthority,
    ParameterResource AdminApiKeySha256,
    ParameterResource InternalClientSecret,
    ParameterResource AccessAdminClientSecret,
    string? OtlpEndpoint,
    ResourcesOptions Limits,
    ParameterResource DebeziumPassword,
    bool UseDataVolumes,
    bool DebeziumSelfHealLostOffsets = false);

public static class ServiceResources
{
    public const string MigrateSuffix = "-migrate";
    public const string ApiSuffix = "-api";

    /// <summary>The Keycloak client services use to call each other (client credentials).</summary>
    public const string InternalClientId = "procure-internal";

    private const string DebeziumPrefix = "debezium-";
    private const string DebeziumImage = "quay.io/debezium/server";
    private const string DebeziumTag = "3.7.0.Final";
    private const int DebeziumHttpPort = 8080;

    /// <summary>Overrides <c>debezium.source.snapshot.mode.configuration.based.snapshot.on.data.error</c> of application.properties.</summary>
    public const string SelfHealEnvironmentVariable = "DEBEZIUM_SOURCE_SNAPSHOT_MODE_CONFIGURATION_BASED_SNAPSHOT_ON_DATA_ERROR";

    /// <summary>
    /// Adds a service the one way every service is added: its database, a short-lived <c>&lt;name&gt;-migrate</c> resource (neither for a
    /// service without a database, <see cref="ServiceStore.None"/>)
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
        // A MongoDB service's database is created on the mongo resource instead. Debezium reads the Postgres WAL, so there is nothing to capture.
        // A service without a database (ServiceStore.None, Notifications) gets neither a database nor a migrate resource.
        if (spec.Store != ServiceStore.Postgres && spec.CapturedTables.Length > 0)
        {
            throw new ArgumentException($"{spec.Name}: a {spec.Store} service has no Debezium capture (CapturedTables must be empty).", nameof(spec));
        }

        if (spec.SecondaryMongoDatabase is not null && spec.Store != ServiceStore.Postgres)
        {
            throw new ArgumentException(
                $"{spec.Name}: a SecondaryMongoDatabase is only for a service whose main store is Postgres, not {spec.Store}.", nameof(spec));
        }

        IResourceBuilder<IResourceWithConnectionString>? database = spec.Store switch
        {
            ServiceStore.MongoDB => shared.Mongo.AddDatabase(spec.Database),
            ServiceStore.Postgres => shared.Postgres.AddDatabase(spec.Database),
            _ => null,
        };

        IResourceBuilder<ProjectResource>? migrate = null;
        if (database is not null)
        {
            migrate = builder.AddProject<TProject>(spec.Name + MigrateSuffix)
                .WithArgs("--migrate")
                .WithEnvironment("DemoSeed__Enabled", "true")
                .WithEnvironment("Service__Store", spec.Store.ToString())
                .WithReference(database)
                .WithReference(shared.RabbitMq)
                .WithReference(shared.Redis)
                .WaitFor(database)
                .WithDotNetLimits(shared.Limits.DotNet);
        }

        var api = builder.AddProject<TProject>(spec.Name + ApiSuffix)
            // No launchSettings.json: Aspire picks a free port, so dev runs and test runs never clash.
            .WithHttpEndpoint(name: "http")
            .WithEnvironment("Service__Store", spec.Store.ToString())
            .WithReference(shared.RabbitMq)
            .WithReference(shared.Redis)
            .WithEnvironment("Auth__Authority", shared.KeycloakAuthority)
            // The local Keycloak container speaks plain http; the deployed authority is https and keeps the default (true).
            .WithEnvironment("Auth__RequireHttpsMetadata", "false")
            .WithEnvironment("Auth__OpenApi__AuthorizationUrl", ReferenceExpression.Create($"{shared.KeycloakAuthority}/protocol/openid-connect/auth"))
            .WithEnvironment("Auth__OpenApi__TokenUrl", ReferenceExpression.Create($"{shared.KeycloakAuthority}/protocol/openid-connect/token"))
            .WithEnvironment("Auth__AdminApiKeySha256", shared.AdminApiKeySha256)
            // Service to service: tokens of the internal client are service accounts, and this service logs in as that client.
            .WithEnvironment("Auth__InternalClientId", InternalClientId)
            .WithEnvironment("Auth__Internal__ClientId", InternalClientId)
            .WithEnvironment("Auth__Internal__ClientSecret", shared.InternalClientSecret)
            .WithEnvironment("Auth__Internal__TokenUrl", ReferenceExpression.Create($"{shared.KeycloakAuthority}/protocol/openid-connect/token"))
            .WithOtlpEndpoint(shared.OtlpEndpoint)
            .WaitFor(shared.Keycloak)
            .WaitFor(shared.RabbitMq)
            .WaitFor(shared.Redis)
            .WithHttpHealthCheck("/health/ready")
            .WithDotNetLimits(shared.Limits.DotNet);

        if (database is not null && migrate is not null)
        {
            api.WithReference(database).WaitForCompletion(migrate);
        }

        if (spec.SecondaryMongoDatabase is not null && migrate is not null)
        {
            // A second database on MongoDB (the Assistant's chat history): both resources get its connection string and its name
            // (Service__SecondaryMongoConnectionName), so the readiness check pings it and --migrate creates its indexes.
            var secondary = shared.Mongo.AddDatabase(spec.SecondaryMongoDatabase);
            foreach (var resource in new[] { migrate, api })
            {
                resource.WithReference(secondary).WaitFor(secondary)
                    .WithEnvironment("Service__SecondaryMongoConnectionName", spec.SecondaryMongoDatabase);
            }
        }

        if (migrate is not null && spec.CapturedTables.Length > 0)
        {
            // Debezium takes no data snapshot (snapshot.mode=configuration_based, snapshot.data=false), so it only sees changes made after its slot exists. The API
            // therefore waits for Debezium to be healthy: no change made through the app can precede the capture.
            // (The demo seed written by --migrate runs before Debezium starts and is deliberately not captured.)
            api.WaitFor(builder.AddDebeziumFor(spec.Database, spec.CapturedTables, migrate, shared));
        }

        return api;
    }

    /// <summary>
    /// The services this API calls over HTTP (<c>AddProcureAiServiceClient("&lt;name&gt;-api")</c>): a reference for each, so service
    /// discovery resolves <c>http://&lt;name&gt;-api</c>, and a wait, so the API is not ready before the services it needs are.
    /// </summary>
    public static IResourceBuilder<ProjectResource> CallsServices(
        this IResourceBuilder<ProjectResource> api, params IResourceBuilder<ProjectResource>[] callees)
    {
        foreach (var callee in callees)
        {
            api.WithReference(callee).WaitFor(callee);
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
            // Off in application.properties (a lost offset fails loudly); on for local runs (AppHost appsettings), where a
            // reset database is normal. A reset leaves a CDC gap: changes between the lost position and the restart are not audited.
            .WithEnvironment(SelfHealEnvironmentVariable, shared.DebeziumSelfHealLostOffsets ? "true" : "false")
            .WithEnvironment("DEBEZIUM_SOURCE_TABLE_INCLUDE_LIST", string.Join(',', tables.Select(t => "public." + t)))
            .WithEnvironment("DEBEZIUM_SINK_RABBITMQ_CONNECTION_USERNAME", shared.RabbitMq.Resource.UserNameReference)
            .WithEnvironment("DEBEZIUM_SINK_RABBITMQ_CONNECTION_PASSWORD", shared.RabbitMq.Resource.PasswordParameter)
            .WithHttpHealthCheck("/q/health")
            .WaitForCompletion(migrate)
            .WaitFor(shared.RabbitMq)
            .WithContainerLimits(shared.Limits.Debezium);
        if (shared.UseDataVolumes)
        {
            // The offsets (last WAL position read) must live exactly as long as the database they point into. Postgres's
            // WithDataVolume is scoped per AppHost ({app}-{sha of the AppHost path}-postgres-data), so this volume is scoped
            // the same way. A fixed name was shared by every clone and worktree: a fresh worktree's empty database met another
            // checkout's offsets and Debezium refused to start ("LSN ... no longer available on the server").
            debezium.WithVolume(VolumeNameGenerator.Generate(debezium, "data"), "/debezium/data");
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
