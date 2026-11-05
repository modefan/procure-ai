using System.Globalization;
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
/// <param name="CapturedTables">Tables Debezium captures for the audit trail (wired by <c>AddDebeziumFor</c>); for a <see cref="ServiceStore.MongoDB"/>
/// service, its collections (wired by <c>AddDebeziumForMongo</c>, plan 06 ruling 16); empty when none.</param>
/// <param name="GatewayCluster">The YARP cluster the gateway routes this service's traffic to, e.g. <c>requests</c>.</param>
/// <param name="ApiRoutePrefix">The path the gateway exposes the service under, e.g. <c>/api/requests</c>.</param>
/// <param name="Store">Which server holds the database, or <see cref="ServiceStore.None"/>. A service without a database has nothing to capture, so
/// its <c>CapturedTables</c> must be empty.</param>
/// <param name="SecondaryMongoDatabase">A second database on the <c>mongo</c> resource next to a Postgres store (the Assistant's chat history,
/// <c>assistantchatdb</c>), also its connection-string name; null for none. Only for <see cref="ServiceStore.Postgres"/>.</param>
/// <param name="SecondaryMongoCapturedCollections">Collections of <paramref name="SecondaryMongoDatabase"/> the MongoDB Debezium captures (the
/// conversations); empty or null when none.</param>
public sealed record ServiceSpec(
    string Name, string Database, string[] CapturedTables, string GatewayCluster, string ApiRoutePrefix, ServiceStore Store = ServiceStore.Postgres,
    string? SecondaryMongoDatabase = null, string[]? SecondaryMongoCapturedCollections = null);

/// <summary>The infrastructure and settings every service resource is wired to.</summary>
/// <param name="InternalClientSecret">Secret of the Keycloak client <c>procure-internal</c>, with which services call each other (<c>Auth:Internal:ClientSecret</c>).</param>
/// <param name="AccessAdminClientSecret">Secret of the Keycloak client <c>procure-access-admin</c> (Keycloak admin API). Not passed to any service here: only the Access service gets it.</param>
/// <param name="DebeziumPassword">Password of the Postgres <c>debezium</c> role (secret parameter), for every Debezium Server.</param>
/// <param name="UseDataVolumes">Whether containers keep their data in named volumes (offsets of Debezium).</param>
/// <param name="DebeziumSelfHealLostOffsets">Config <c>Debezium:SelfHealLostOffsets</c>: when a Debezium Server's stored offset is no longer available
/// (slot gone, database reset), drop it and stream from a new slot instead of failing. Leaves a CDC gap; local runs only.</param>
/// <param name="OtlpEndpoint">The Aspire dashboard's OTLP endpoint (the Serilog OpenTelemetry sink has no environment fallback).</param>
/// <param name="DebeziumMongoPassword">Password of the least-privilege MongoDB user <c>debezium</c> (secret parameter
/// <c>debezium-mongo-password</c>) that <c>debezium-mongo</c> connects as; required once a service captures MongoDB collections.</param>
/// <param name="DebeziumHeartbeatIntervalMs">Config <c>Debezium:HeartbeatIntervalMs</c>: how often each Postgres Debezium runs
/// <see cref="ServiceResources.HeartbeatActionQuery"/>. Null: application.properties' default.</param>
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
    bool DebeziumSelfHealLostOffsets = false,
    ParameterResource? DebeziumMongoPassword = null,
    int? DebeziumHeartbeatIntervalMs = null);

public static class ServiceResources
{
    public const string MigrateSuffix = "-migrate";
    public const string ApiSuffix = "-api";

    /// <summary>The Keycloak client services use to call each other (client credentials).</summary>
    public const string InternalClientId = "procure-internal";

    private const string DebeziumPrefix = "debezium-";

    /// <summary>The one Debezium Server for every captured MongoDB collection (plan 06 ruling 16).</summary>
    public const string DebeziumMongoName = DebeziumPrefix + "mongo";

    /// <summary>The least-privilege MongoDB user <c>debezium-mongo</c> connects as (<c>DebeziumMongoUser.UserName</c> in BuildingBlocks; the
    /// AppHost does not reference that library).</summary>
    public const string DebeziumMongoUser = "debezium";

    /// <summary>The port MongoDB listens on inside its container (the <c>mongo</c> resource's target port).</summary>
    private const int MongoContainerPort = 27017;
    private const string DebeziumImage = "quay.io/debezium/server";
    private const string DebeziumTag = "3.7.0.Final";
    private const int DebeziumHttpPort = 8080;

    /// <summary>Overrides <c>debezium.source.snapshot.mode.configuration.based.snapshot.on.data.error</c> of application.properties.</summary>
    public const string SelfHealEnvironmentVariable = "DEBEZIUM_SOURCE_SNAPSHOT_MODE_CONFIGURATION_BASED_SNAPSHOT_ON_DATA_ERROR";

    /// <summary>
    /// Debezium's heartbeat action (plan 08 Task 1): an update of the one row of <c>debezium_heartbeat</c>, which every service's migration
    /// adds to its publication (CdcMigrations.EnableHeartbeat). The change reaches Debezium, which confirms its LSN, so an idle database's
    /// replication slot does not hold back the server's WAL. The chart's <c>debezium.heartbeatActionQuery</c> is the same.
    /// </summary>
    public const string HeartbeatActionQuery = "UPDATE debezium_heartbeat SET beat_at = now() WHERE id = 1";

    public const string HeartbeatIntervalEnvironmentVariable = "DEBEZIUM_SOURCE_HEARTBEAT_INTERVAL_MS";
    public const string HeartbeatActionQueryEnvironmentVariable = "DEBEZIUM_SOURCE_HEARTBEAT_ACTION_QUERY";

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
        // A MongoDB service's database is created on the mongo resource instead; its captured collections go to the one MongoDB Debezium.
        // A service without a database (ServiceStore.None, Notifications) gets neither a database nor a migrate resource, and captures nothing.
        if (spec.Store == ServiceStore.None && spec.CapturedTables.Length > 0)
        {
            throw new ArgumentException($"{spec.Name}: a service without a database has no Debezium capture (CapturedTables must be empty).", nameof(spec));
        }

        if (spec.SecondaryMongoCapturedCollections is { Length: > 0 } && spec.SecondaryMongoDatabase is null)
        {
            throw new ArgumentException($"{spec.Name}: SecondaryMongoCapturedCollections need a SecondaryMongoDatabase.", nameof(spec));
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
            if (spec.Store == ServiceStore.Postgres)
            {
                migrate.WithEnvironment("Database__MaxPoolSize", shared.Limits.Postgres.PoolSizePerService.ToString(CultureInfo.InvariantCulture));
            }
        }

        var api = builder.AddProject<TProject>(spec.Name + ApiSuffix)
            // No launchSettings.json: Aspire picks a free port, so dev runs and test runs never clash.
            .WithHttpEndpoint(name: "http")
            // The Angular dev panel asks for the "behind the scenes" response headers (X-Cache, X-Policy, ...).
            .WithEnvironment("DevPanel__Enabled", "true")
            // The nightly demo reset's endpoint (plan 06 ruling 4; off by default, 404): ProcureAi.DemoData calls it through the gateway.
            .WithEnvironment("DemoReset__Enabled", "true")
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

        if (spec.Store == ServiceStore.Postgres)
        {
            // Each service's Npgsql pool is capped so all of them fit the server's max_connections (Resources:Postgres).
            api.WithEnvironment("Database__MaxPoolSize", shared.Limits.Postgres.PoolSizePerService.ToString(CultureInfo.InvariantCulture));
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

        if (migrate is not null && spec.Store == ServiceStore.MongoDB && spec.CapturedTables.Length > 0)
        {
            api.WaitFor(builder.AddDebeziumForMongo([.. spec.CapturedTables.Select(c => $"{spec.Database}.{c}")], migrate, shared));
        }

        if (migrate is not null && spec.SecondaryMongoDatabase is not null && spec.SecondaryMongoCapturedCollections is { Length: > 0 } secondaryCollections)
        {
            api.WaitFor(builder.AddDebeziumForMongo([.. secondaryCollections.Select(c => $"{spec.SecondaryMongoDatabase}.{c}")], migrate, shared));
        }

        if (migrate is not null && spec.Store == ServiceStore.Postgres && spec.CapturedTables.Length > 0)
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
            .WithEnvironment(HeartbeatActionQueryEnvironmentVariable, HeartbeatActionQuery)
            .WithEnvironment("DEBEZIUM_SINK_RABBITMQ_CONNECTION_USERNAME", shared.RabbitMq.Resource.UserNameReference)
            .WithEnvironment("DEBEZIUM_SINK_RABBITMQ_CONNECTION_PASSWORD", shared.RabbitMq.Resource.PasswordParameter)
            .WithHttpHealthCheck("/q/health")
            .WaitForCompletion(migrate)
            .WaitFor(shared.RabbitMq)
            .WithContainerLimits(shared.Limits.Debezium);
        if (shared.DebeziumHeartbeatIntervalMs is { } heartbeatIntervalMs)
        {
            debezium.WithEnvironment(HeartbeatIntervalEnvironmentVariable, heartbeatIntervalMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

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

    /// <summary>
    /// Adds <paramref name="collections"/> (<c>&lt;database&gt;.&lt;collection&gt;</c>) to the one MongoDB Debezium Server, <c>debezium-mongo</c>
    /// (plan 06 ruling 16), creating it on the first call: change streams of the <c>mongo</c> replica set to the <c>cdc</c> exchange with
    /// routing keys <c>mongo.&lt;database&gt;.&lt;collection&gt;</c>. One MongoDB connector watches the whole replica set, so one instance serves
    /// every MongoDB service (unlike Postgres, one per database). It runs infra/debezium/application-mongo.properties with the same caps as
    /// the Postgres ones, connects straight to the one member as the least-privilege user <see cref="DebeziumMongoUser"/> (plan 07 Task 4; the
    /// password is <see cref="SharedResources.DebeziumMongoPassword"/>), and starts after every migration that adds collections (the seed is
    /// not captured) and after RabbitMQ. The caller's API waits for it (no change made through the app precedes the capture, as no data
    /// snapshot is taken).
    /// The migrate resource of the first call (Suppliers: it runs with the server's root rights) creates that user: it gets
    /// <c>Mongo__DebeziumPassword</c> and <c>Mongo__DebeziumDatabases__&lt;i&gt;</c> (every captured database, from later calls too).
    /// </summary>
    public static IResourceBuilder<ContainerResource> AddDebeziumForMongo(
        this IDistributedApplicationBuilder builder,
        string[] collections,
        IResourceBuilder<ProjectResource> migrate,
        SharedResources shared)
    {
        var existing = builder.Resources.OfType<ContainerResource>().SingleOrDefault(r => r.Name == DebeziumMongoName);
        if (existing is not null)
        {
            existing.Annotations.OfType<MongoCaptureAnnotation>().Single().Collections.AddRange(collections);
            return builder.CreateResourceBuilder(existing).WaitForCompletion(migrate);
        }

        var capture = new MongoCaptureAnnotation([.. collections]);
        var mongo = shared.Mongo.Resource;
#pragma warning disable ASPIREMONGODB001 // experimental, like WithReplicaSet in AppHost.cs: the name the set was initiated with
        var replicaSet = mongo.ReplicaSetName
            ?? throw new InvalidOperationException("MongoDB change data capture needs the mongo resource to be a replica set (WithReplicaSet).");
#pragma warning restore ASPIREMONGODB001
        var password = shared.DebeziumMongoPassword
            ?? throw new InvalidOperationException("MongoDB change data capture needs SharedResources.DebeziumMongoPassword (parameter debezium-mongo-password).");
        // Aspire initiates the single-member set with an address only it can reach, so Debezium connects straight to the member by its
        // container name (directConnection); the connector still insists on the replica set's name. No credentials in the string: the
        // connector takes them as mongodb.user / mongodb.password (no URI escaping), as the chart passes them from its Secret.
        var connectionString = ReferenceExpression.Create(
            $"mongodb://{mongo.Name}:{MongoContainerPort.ToString(System.Globalization.CultureInfo.InvariantCulture)}/?authSource=admin&replicaSet={replicaSet}&directConnection=true");
        var debezium = builder.AddContainer(DebeziumMongoName, DebeziumImage, DebeziumTag)
            .WithAnnotation(capture)
            .WithHttpEndpoint(targetPort: DebeziumHttpPort, name: "http")
            .WithBindMount("../../../infra/debezium/application-mongo.properties", "/debezium/config/application.properties", isReadOnly: true)
            .WithEnvironment("JAVA_OPTS", shared.Limits.Debezium.JavaOpts)
            .WithEnvironment("DEBEZIUM_SOURCE_MONGODB_CONNECTION_STRING", connectionString)
            .WithEnvironment("DEBEZIUM_SOURCE_MONGODB_USER", DebeziumMongoUser)
            .WithEnvironment("DEBEZIUM_SOURCE_MONGODB_PASSWORD", password)
            .WithEnvironment(context => context.EnvironmentVariables["DEBEZIUM_SOURCE_COLLECTION_INCLUDE_LIST"] = string.Join(',', capture.Collections))
            .WithEnvironment(SelfHealEnvironmentVariable, shared.DebeziumSelfHealLostOffsets ? "true" : "false")
            .WithEnvironment("DEBEZIUM_SINK_RABBITMQ_CONNECTION_USERNAME", shared.RabbitMq.Resource.UserNameReference)
            .WithEnvironment("DEBEZIUM_SINK_RABBITMQ_CONNECTION_PASSWORD", shared.RabbitMq.Resource.PasswordParameter)
            .WithHttpHealthCheck("/q/health")
            .WaitFor(shared.Mongo)
            .WaitForCompletion(migrate)
            .WaitFor(shared.RabbitMq)
            .WithContainerLimits(shared.Limits.Debezium);
        // The first MongoDB service's migration creates the user, with read on every captured database (evaluated when the resource starts,
        // so collections added by later services are included).
        migrate
            .WithEnvironment("Mongo__DebeziumPassword", password)
            .WithEnvironment(context =>
            {
                var databases = capture.Collections.Select(c => c.Split('.', 2)[0]).Distinct(StringComparer.Ordinal).ToArray();
                for (var i = 0; i < databases.Length; i++)
                {
                    context.EnvironmentVariables[$"Mongo__DebeziumDatabases__{i}"] = databases[i];
                }
            });
        if (shared.UseDataVolumes)
        {
            // The resume token must live as long as the oplog it points into; the mongo data volume is scoped per AppHost, so is this one.
            debezium.WithVolume(VolumeNameGenerator.Generate(debezium, "data"), "/debezium/data");
        }

        return debezium;
    }

    /// <summary>The collections <c>debezium-mongo</c> captures, collected from every service that adds some.</summary>
    private sealed class MongoCaptureAnnotation(List<string> collections) : IResourceAnnotation
    {
        public List<string> Collections { get; } = collections;
    }

    /// <summary>The Postgres role that reads the server's audit log (plan 06 ruling 14); Audit's <c>--migrate</c> creates it.</summary>
    public const string AuditLogReaderRole = "audit_log_reader";

    /// <summary>The connection-string name Audit reads the audit log with (<c>PgAudit:ConnectionName</c>).</summary>
    public const string AuditLogConnectionName = "auditlog";

    /// <summary>
    /// Gives a service's migrate and api resources <c>ConnectionStrings__auditlog</c>: <paramref name="database"/> on the Postgres server as
    /// <see cref="AuditLogReaderRole"/> with the password parameter. The migrate run (as the superuser of the database's own connection
    /// string) creates the role with this password; the api reads the log with it.
    /// </summary>
    public static IResourceBuilder<ProjectResource> WithAuditLogReader(
        this IResourceBuilder<ProjectResource> api,
        IDistributedApplicationBuilder builder,
        IResourceBuilder<PostgresServerResource> postgres,
        string database,
        IResourceBuilder<ParameterResource> password)
    {
        var endpoint = postgres.Resource.PrimaryEndpoint;
        var connectionString = ReferenceExpression.Create(
            $"Host={endpoint.Property(EndpointProperty.Host)};Port={endpoint.Property(EndpointProperty.Port)};Username={AuditLogReaderRole};Password={password.Resource};Database={database}");
        var migrateName = api.Resource.Name[..^ApiSuffix.Length] + MigrateSuffix;
        var migrate = builder.Resources.OfType<ProjectResource>().Single(r => r.Name == migrateName);
        builder.CreateResourceBuilder(migrate).WithEnvironment("ConnectionStrings__" + AuditLogConnectionName, connectionString);
        return api.WithEnvironment("ConnectionStrings__" + AuditLogConnectionName, connectionString);
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
