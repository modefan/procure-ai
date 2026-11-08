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
/// <param name="UseDataVolumes">Whether containers keep their data in named volumes (offsets of Debezium).</param>
/// <param name="DebeziumSelfHealLostOffsets">Config <c>Debezium:SelfHealLostOffsets</c>: when a Debezium Server's stored offset is no longer available
/// (slot gone, database reset), drop it and stream from a new slot instead of failing. Leaves a CDC gap; local runs only.</param>
/// <param name="OtlpEndpoint">The Aspire dashboard's OTLP endpoint (the Serilog OpenTelemetry sink has no environment fallback).</param>
/// <param name="DebeziumMongoPassword">Password of the least-privilege MongoDB user <c>debezium</c> (secret parameter
/// <c>debezium-mongo-password</c>) that <c>debezium-mongo</c> connects as and infra/mongo/users.js creates; required once a service captures
/// MongoDB collections.</param>
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

    /// <summary>The least-privilege MongoDB user <c>debezium-mongo</c> connects as (infra/mongo/users.js creates it).</summary>
    public const string DebeziumMongoUser = "debezium";

    /// <summary>The port MongoDB listens on inside its container (the <c>mongo</c> resource's target port).</summary>
    private const int MongoContainerPort = 27017;

    /// <summary>The port Postgres listens on inside its container.</summary>
    private const int PostgresContainerPort = 5432;

    /// <summary>
    /// The one-shot container that runs infra/postgres/roles.sql as the superuser on every AppHost start (plan 08 ruling 6): every service's
    /// role <c>&lt;service&gt;_app</c> owning its database, every Debezium's <c>&lt;service&gt;_cdc</c>, Audit's log reader. Every Postgres
    /// migration waits for it to finish.
    /// </summary>
    public const string PostgresRolesName = "postgres-roles";

    /// <summary>The one-shot container that runs infra/mongo/users.js as root on every start (one MongoDB user per database and the Debezium user).</summary>
    public const string MongoUsersName = "mongo-users";

    /// <summary>
    /// The image the roles script runs in: the base of infra/postgres/Dockerfile (pulled for that build anyway), with psql 17. The script itself
    /// is bind-mounted, so the AppHost builds no second image; the chart's db-roles Job runs the same files from our postgres image.
    /// </summary>
    private const string PostgresRolesImage = "pgvector/pgvector";
    private const string PostgresRolesTag = "pg17";

    /// <summary>The role a service's migration and API connect to its Postgres database (or MongoDB) as: <c>&lt;service&gt;_app</c>.</summary>
    public static string AppRole(string service) => service + "_app";

    /// <summary>The replication role of a service's Debezium Server: <c>&lt;service&gt;_cdc</c>.</summary>
    public static string CdcRole(string service) => service + "_cdc";

    /// <summary>The environment variable roles.sql / users.js read a password from: <c>DB_&lt;SERVICE&gt;[_&lt;SUFFIX&gt;]_PASSWORD</c>.</summary>
    public static string PasswordVariable(string service, string? suffix = null) =>
        "DB_" + service.ToUpperInvariant() + (suffix is null ? "" : "_" + suffix) + "_PASSWORD";

    /// <summary>A generated (32 letters and digits), persisted secret parameter, kept in the AppHost user secrets like the other passwords.</summary>
    private static IResourceBuilder<ParameterResource> AddPasswordParameter(IDistributedApplicationBuilder builder, string name) =>
        builder.Resources.OfType<ParameterResource>().SingleOrDefault(p => p.Name == name) is { } existing
            ? builder.CreateResourceBuilder(existing)
            : builder.AddParameter(name, new GenerateParameterDefault { MinLength = 32, Special = false }, secret: true, persist: true);
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
        // Aspire creates the database on the server; postgres-roles (infra/postgres/roles.sql, before every migration) makes the service's
        // own role <name>_app its owner and the only one besides its Debezium's <name>_cdc that may connect (plan 08 ruling 6). The services
        // connect as <name>_app (password parameter db-<name>-password), never as the superuser; the migration creates the publication and
        // grants (CdcMigrations.EnableCdc).
        // A MongoDB service's database is created on the mongo resource instead (mongo-users: infra/mongo/users.js gives it its own user
        // <name>_app); its captured collections go to the one MongoDB Debezium.
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

        // Not WithReference(database): that passes the server's superuser (or root) and its password (also as <DB>_USERNAME / _PASSWORD).
        // The connection string is the service's own role's; the setup container that creates the role waits for the database and the
        // migration waits for the setup container.
        ReferenceExpression? connectionString = null;
        IResourceBuilder<ContainerResource>? setup = null;
        if (database is not null)
        {
            var password = AddPasswordParameter(builder, $"db-{spec.Name}-password");
            if (spec.Store == ServiceStore.Postgres)
            {
                setup = builder.GetOrAddPostgresRoles(shared);
                connectionString = PostgresConnectionString(shared.Postgres, AppRole(spec.Name), password, spec.Database);
            }
            else
            {
                setup = builder.GetOrAddMongoUsers(shared);
                connectionString = MongoConnectionString(shared.Mongo, AppRole(spec.Name), password, spec.Database);
            }

            setup.WithEnvironment(PasswordVariable(spec.Name), password).WaitFor(database);
        }

        IResourceBuilder<ProjectResource>? migrate = null;
        if (database is not null && connectionString is not null && setup is not null)
        {
            migrate = builder.AddProject<TProject>(spec.Name + MigrateSuffix)
                .WithArgs("--migrate")
                .WithEnvironment("DemoSeed__Enabled", "true")
                .WithEnvironment("Service__Store", spec.Store.ToString())
                .WithEnvironment("ConnectionStrings__" + spec.Database, connectionString)
                .WithReference(shared.RabbitMq)
                .WithReference(shared.Redis)
                .WaitFor(database)
                .WaitForCompletion(setup)
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
            // The gateway on this PC forwards the scheme the browser used (https), so Swagger UI's servers are https (plan 08b Task 2).
            // Loopback only (the gateway and Aspire's endpoint proxy); the service takes only the scheme (ForwardedHeaders:Headers).
            // Listing them also ends Aspire's ASPNETCORE_FORWARDEDHEADERS_ENABLED default of trusting every address.
            .WithEnvironment("ForwardedHeaders__KnownProxies__0", "127.0.0.1")
            .WithEnvironment("ForwardedHeaders__KnownProxies__1", "::1")
            .WaitFor(shared.Keycloak)
            .WaitFor(shared.RabbitMq)
            .WaitFor(shared.Redis)
            .WithHttpHealthCheck("/health/ready")
            .WithDotNetLimits(shared.Limits.DotNet);

        if (connectionString is not null && migrate is not null)
        {
            api.WithEnvironment("ConnectionStrings__" + spec.Database, connectionString).WaitForCompletion(migrate);
        }

        if (spec.Store == ServiceStore.Postgres)
        {
            // Each service's Npgsql pool is capped so all of them fit the server's max_connections (Resources:Postgres).
            api.WithEnvironment("Database__MaxPoolSize", shared.Limits.Postgres.PoolSizePerService.ToString(CultureInfo.InvariantCulture));
        }

        if (spec.SecondaryMongoDatabase is not null && migrate is not null)
        {
            // A second database on MongoDB (the Assistant's chat history): both resources get its connection string and its name
            // (Service__SecondaryMongoConnectionName), so the readiness check pings it and --migrate creates its indexes. As the service's own
            // MongoDB user <name>_app (password parameter db-<name>-mongo-password), which mongo-users creates first.
            var secondary = shared.Mongo.AddDatabase(spec.SecondaryMongoDatabase);
            var mongoPassword = AddPasswordParameter(builder, $"db-{spec.Name}-mongo-password");
            var mongoUsers = builder.GetOrAddMongoUsers(shared)
                .WithEnvironment(PasswordVariable(spec.Name, "MONGO"), mongoPassword)
                .WaitFor(secondary);
            var secondaryConnection = MongoConnectionString(shared.Mongo, AppRole(spec.Name), mongoPassword, spec.SecondaryMongoDatabase);
            migrate.WaitForCompletion(mongoUsers);
            foreach (var resource in new[] { migrate, api })
            {
                resource.WithEnvironment("ConnectionStrings__" + spec.SecondaryMongoDatabase, secondaryConnection).WaitFor(secondary)
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
            api.WaitFor(builder.AddDebeziumFor(spec.Name, spec.Database, spec.CapturedTables, migrate, shared));
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
    /// Routing keys are <c>&lt;database&gt;.public.&lt;table&gt;</c>. It replicates as the database's own role <c>&lt;service&gt;_cdc</c>
    /// (password parameter <c>db-&lt;service&gt;-cdc-password</c>), which postgres-roles creates (plan 08 ruling 6).
    /// </summary>
    public static IResourceBuilder<ContainerResource> AddDebeziumFor(
        this IDistributedApplicationBuilder builder,
        string service,
        string database,
        string[] tables,
        IResourceBuilder<ProjectResource> migrate,
        SharedResources shared)
    {
        var cdcPassword = AddPasswordParameter(builder, $"db-{service}-cdc-password");
        builder.GetOrAddPostgresRoles(shared).WithEnvironment(PasswordVariable(service, "CDC"), cdcPassword);
        var debezium = builder.AddContainer(DebeziumPrefix + database, DebeziumImage, DebeziumTag)
            .WithHttpEndpoint(targetPort: DebeziumHttpPort, name: "http")
            .WithBindMount("../../../infra/debezium/application.properties", "/debezium/config/application.properties", isReadOnly: true)
            .WithEnvironment("JAVA_OPTS", shared.Limits.Debezium.JavaOpts)
            .WithEnvironment("DEBEZIUM_SOURCE_DATABASE_USER", CdcRole(service))
            .WithEnvironment("DEBEZIUM_SOURCE_DATABASE_PASSWORD", cdcPassword)
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
    /// mongo-users (infra/mongo/users.js, as root) creates that user: it gets the password as <c>DEBEZIUM_MONGO_PASSWORD</c> and every
    /// captured database (from later calls too) as <c>DEBEZIUM_MONGO_DATABASES</c> (plan 08 Task 4; Suppliers' migration did it before).
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
        // mongo-users creates the user, with read on every captured database (evaluated when the container starts, so collections added by
        // later services are included).
        builder.GetOrAddMongoUsers(shared)
            .WithEnvironment("DEBEZIUM_MONGO_PASSWORD", password)
            .WithEnvironment(context => context.EnvironmentVariables["DEBEZIUM_MONGO_DATABASES"] =
                string.Join(',', capture.Collections.Select(c => c.Split('.', 2)[0]).Distinct(StringComparer.Ordinal)));
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
    /// <see cref="AuditLogReaderRole"/> with the password parameter. postgres-roles (infra/postgres/roles-pgaudit.sql, as the superuser) creates
    /// the role and its log tables with this password (<c>DB_AUDIT_LOG_READER_PASSWORD</c>); the api reads the log with it.
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
        builder.CreateResourceBuilder(builder.Resources.OfType<ContainerResource>().Single(r => r.Name == PostgresRolesName))
            .WithEnvironment("DB_AUDIT_LOG_READER_PASSWORD", password);
        return api.WithEnvironment("ConnectionStrings__" + AuditLogConnectionName, connectionString);
    }

    /// <summary>
    /// The <see cref="PostgresRolesName"/> container, added on the first call: psql in <see cref="PostgresRolesImage"/> runs the bind-mounted
    /// infra/postgres/roles.sql (with roles-database.sql and roles-pgaudit.sql) against <c>postgres</c> as its superuser, then exits. It runs
    /// on every start, so a role, a password or an ownership change reaches an existing data volume too. The services add their passwords
    /// (<c>DB_&lt;SERVICE&gt;[_CDC]_PASSWORD</c>) and a wait for their database; every Postgres migration waits for it to complete.
    /// </summary>
    public static IResourceBuilder<ContainerResource> GetOrAddPostgresRoles(this IDistributedApplicationBuilder builder, SharedResources shared)
    {
        if (builder.Resources.OfType<ContainerResource>().SingleOrDefault(r => r.Name == PostgresRolesName) is { } existing)
        {
            return builder.CreateResourceBuilder(existing);
        }

        var postgres = shared.Postgres.Resource;
        var roles = builder.AddContainer(PostgresRolesName, PostgresRolesImage, PostgresRolesTag)
            .WithBindMount("../../../infra/postgres/roles.sql", "/procureai/roles.sql", isReadOnly: true)
            .WithBindMount("../../../infra/postgres/roles-database.sql", "/procureai/roles-database.sql", isReadOnly: true)
            .WithBindMount("../../../infra/postgres/roles-pgaudit.sql", "/procureai/roles-pgaudit.sql", isReadOnly: true)
            .WithEntrypoint("psql")
            .WithArgs(
                "-v", "ON_ERROR_STOP=1", "-h", postgres.Name, "-p", PostgresContainerPort.ToString(CultureInfo.InvariantCulture),
                "-d", "postgres", "-f", "/procureai/roles.sql")
            // The superuser's name and password reach psql as PGUSER / PGPASSWORD, never as arguments.
            .WithEnvironment("PGUSER", postgres.UserNameReference)
            .WithEnvironment("PGPASSWORD", postgres.PasswordParameter)
            .WaitFor(shared.Postgres)
            .WithContainerLimits(shared.Limits.DbSetup);
        return roles;
    }

    /// <summary>
    /// The <see cref="MongoUsersName"/> container, added on the first call: mongosh in the mongo resource's own image runs the bind-mounted
    /// infra/mongo/users.js against the replica set's one member as root (from <c>MONGO_USER</c> / <c>MONGO_PASSWORD</c>), then exits; on every
    /// start. The MongoDB services add their passwords and a wait for their database; their migrations wait for it to complete.
    /// </summary>
    public static IResourceBuilder<ContainerResource> GetOrAddMongoUsers(this IDistributedApplicationBuilder builder, SharedResources shared)
    {
        if (builder.Resources.OfType<ContainerResource>().SingleOrDefault(r => r.Name == MongoUsersName) is { } existing)
        {
            return builder.CreateResourceBuilder(existing);
        }

        var mongo = shared.Mongo.Resource;
        var image = mongo.Annotations.OfType<ContainerImageAnnotation>().Last();
        // Plain TCP on the container network (the server prefers TLS but accepts it without, as debezium-mongo connects); straight to the member.
        var uri = $"mongodb://{mongo.Name}:{MongoContainerPort.ToString(CultureInfo.InvariantCulture)}/?directConnection=true&serverSelectionTimeoutMS=30000";
        var users = builder.AddContainer(MongoUsersName, image.Image, image.Tag ?? "latest")
            .WithBindMount("../../../infra/mongo/users.js", "/procureai/users.js", isReadOnly: true)
            .WithEntrypoint("mongosh")
            .WithArgs("--quiet", "--norc", uri, "/procureai/users.js")
            .WithEnvironment("MONGO_USER", mongo.UserNameReference)
            .WithEnvironment("MONGO_PASSWORD", mongo.PasswordParameter ?? throw new InvalidOperationException("The mongo resource has no password parameter."))
            .WithEnvironment("HOME", "/tmp")
            .WaitFor(shared.Mongo)
            .WithContainerLimits(shared.Limits.DbSetup);
        if (image.Registry is { } registry)
        {
            users.WithImageRegistry(registry);
        }

        return users;
    }

    /// <summary><paramref name="database"/> on the Postgres server as <paramref name="role"/> (Npgsql keywords, as Aspire's own connection string).</summary>
    private static ReferenceExpression PostgresConnectionString(
        IResourceBuilder<PostgresServerResource> postgres, string role, IResourceBuilder<ParameterResource> password, string database)
    {
        var endpoint = postgres.Resource.PrimaryEndpoint;
        return ReferenceExpression.Create(
            $"Host={endpoint.Property(EndpointProperty.Host)};Port={endpoint.Property(EndpointProperty.Port)};Username={role};Password={password.Resource};Database={database}");
    }

    /// <summary>
    /// <paramref name="database"/> on MongoDB as <paramref name="user"/> (a user of the admin database): Aspire's own connection string for the
    /// database with this user and password instead of root, including its TLS switch (the last part of the server's connection string).
    /// </summary>
    private static ReferenceExpression MongoConnectionString(
        IResourceBuilder<MongoDBServerResource> mongo, string user, IResourceBuilder<ParameterResource> password, string database)
    {
        var endpoint = mongo.Resource.PrimaryEndpoint;
        var server = mongo.Resource.ConnectionStringExpression;
        // Aspire 13.6: mongodb://admin:{0}@{1}/?authSource=admin&authMechanism=SCRAM-SHA-256&directConnection=true{2}, where {2} is the TLS
        // part (an expression that yields "&tls=true" while the server uses TLS). Fail loudly if that shape changes.
        if (server.ValueProviders.Count == 0 || !server.Format.EndsWith($"{{{server.ValueProviders.Count - 1}}}", StringComparison.Ordinal)
            || !server.Format.Contains("directConnection=true{", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The mongo resource's connection string is not shaped as expected (Aspire changed it): {server.Format}");
        }

        var tls = server.ValueProviders[^1];
        var builder = new ReferenceExpressionBuilder();
        builder.AppendLiteral($"mongodb://{user}:");
        builder.AppendValueProvider(password.Resource);
        builder.AppendLiteral("@");
        builder.AppendValueProvider(endpoint.Property(EndpointProperty.Host));
        builder.AppendLiteral(":");
        builder.AppendValueProvider(endpoint.Property(EndpointProperty.Port));
        builder.AppendLiteral($"/{database}?authSource=admin&authMechanism=SCRAM-SHA-256&directConnection=true");
        builder.AppendValueProvider(tls);
        return builder.Build();
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
