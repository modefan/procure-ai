using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ProcureAi.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

var limits = ResourcesOptions.Load(builder.Configuration);
var useDataVolumes = builder.Configuration.GetValue("Persistence:UseDataVolumes", true);

// Parameters. Secrets come from AppHost user secrets (never the repo); see DEMO PASSWORDS.txt.
var keycloakAdmin = builder.AddParameter("keycloak-admin");
var keycloakAdminPassword = builder.AddParameter("keycloak-admin-password", secret: true);
// Substituted by Keycloak into the ${KC_DEMO_PASSWORD} / ${KC_GATEWAY_SECRET} placeholders of the realm import,
// so the committed realm file holds no passwords or secrets.
var keycloakDemoPassword = builder.AddParameter("keycloak-demo-password", secret: true);
var keycloakGatewaySecret = builder.AddParameter("keycloak-gateway-secret", secret: true);
var debeziumPassword = builder.AddParameter("debezium-password", secret: true);
// SHA-256 (hex) of the one admin API key; services compare in constant time.
var adminApiKeySha256 = builder.AddParameter("admin-api-key-sha256", secret: true);

// Postgres: custom image (pgvector + pgaudit), logical WAL for Debezium CDC.
var postgres = builder.AddPostgres(ResourceNames.Postgres)
    .WithDockerfile("../../../infra/postgres")
    .WithEnvironment("DEBEZIUM_PASSWORD", debeziumPassword)
    .WithArgs(
        "-c", "wal_level=logical",
        "-c", "shared_preload_libraries=pgaudit",
        "-c", "pgaudit.log=ddl,role",
        "-c", $"shared_buffers={limits.Postgres.SharedBuffers}",
        "-c", $"work_mem={limits.Postgres.WorkMem}")
    .WithContainerLimits(limits.Postgres);
if (useDataVolumes)
{
    postgres.WithDataVolume();
}
// The init script in the image already creates this database (and its publication) on first start;
// AddDatabase registers it as a resource others can reference.
var requestsDb = postgres.AddDatabase(ResourceNames.RequestsDb);

// MongoDB: single-node replica set (needed for change streams / CDC). Aspire's WithReplicaSet generates the
// key file, initiates the set and only reports the server healthy once it is PRIMARY.
#pragma warning disable ASPIREMONGODB001 // experimental Aspire API; replaces ~30 lines of hand-rolled key file and rs.initiate
var mongo = builder.AddMongoDB(ResourceNames.Mongo)
    .WithReplicaSet(ResourceNames.MongoReplicaSet)
#pragma warning restore ASPIREMONGODB001
    .WithArgs("--wiredTigerCacheSizeGB", limits.Mongo.WiredTigerCacheGb.ToString(System.Globalization.CultureInfo.InvariantCulture))
    .WithContainerLimits(limits.Mongo);
if (useDataVolumes)
{
    mongo.WithDataVolume();
}
// Eviction policy volatile-ttl (not volatile-lru): when Redis is full, drop the keys closest to expiry first, i.e. the
// 1 s cache entries, and keep the long-lived 24 h idempotency records and BFF session tickets (config: Resources:Redis).
var redis = builder.AddRedis(ResourceNames.Redis)
    .WithArgs(
        "--maxmemory", limits.Redis.MaxMemory,
        "--maxmemory-policy", limits.Redis.MaxMemoryPolicy)
    .WithContainerLimits(limits.Redis);

var rabbitmq = builder.AddRabbitMQ(ResourceNames.RabbitMq)
    .WithManagementPlugin()
    // Set through an Erlang argument: copying a conf.d file in made RabbitMQ intermittently fail to read its cookie (eacces).
    .WithEnvironment("RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS", $"-rabbit vm_memory_high_watermark {{absolute,\"{limits.RabbitMq.VmMemoryHighWatermarkAbsolute}\"}}")
    .WithContainerLimits(limits.RabbitMq);
// Debezium's RabbitMQ sink only publishes; the topic exchange it targets must already exist. A health check on
// rabbitmq declares it, so WaitFor(rabbitmq) (documented to wait for all health checks to be Healthy) guarantees
// the order and a failure shows as rabbitmq Unhealthy. (Not load_definitions: that would also need users and a
// password hash in a file.)
var rabbitManagement = rabbitmq.GetEndpoint("management");
builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
    CdcExchangeHealthCheck.Name,
    sp => new CdcExchangeHealthCheck(
        rabbitManagement,
        rabbitmq.Resource,
        ResourceNames.CdcExchange,
        sp.GetRequiredService<ResourceLoggerService>().GetLogger(rabbitmq.Resource)),
    failureStatus: null,
    tags: null));
rabbitmq.WithHealthCheck(CdcExchangeHealthCheck.Name);
var keycloak = builder.AddContainer(ResourceNames.Keycloak, "quay.io/keycloak/keycloak", "26.8.1")
    .WithHttpEndpoint(targetPort: 8080, name: "http")
    .WithHttpEndpoint(targetPort: 9000, name: "management")
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", keycloakAdmin)
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", keycloakAdminPassword)
    .WithEnvironment("KC_HEALTH_ENABLED", "true")
    .WithEnvironment("KC_DEMO_PASSWORD", keycloakDemoPassword)
    .WithEnvironment("KC_GATEWAY_SECRET", keycloakGatewaySecret)
    .WithEnvironment("JAVA_OPTS_KC_HEAP", limits.Keycloak.JavaHeapOpts)
    .WithBindMount("../../../infra/keycloak", "/opt/keycloak/data/import", isReadOnly: true)
    .WithArgs("start-dev", "--import-realm")
    .WithHttpHealthCheck("/health/ready", endpointName: "management")
    .WithContainerLimits(limits.Keycloak);

builder.AddContainer(ResourceNames.Mailpit, "axllent/mailpit", "v1.32.0")
    .WithHttpEndpoint(targetPort: 8025, name: "http")
    .WithEndpoint(targetPort: 1025, name: "smtp", scheme: "tcp")
    .WithHttpHealthCheck("/livez")
    .WithContainerLimits(limits.Mailpit);

var debezium = builder.AddContainer(ResourceNames.Debezium, "quay.io/debezium/server", "3.7.0.Final")
    .WithHttpEndpoint(targetPort: 8080, name: "http")
    .WithBindMount("../../../infra/debezium/application.properties", "/debezium/config/application.properties", isReadOnly: true)
    .WithEnvironment("JAVA_OPTS", limits.Debezium.JavaOpts)
    .WithEnvironment("DEBEZIUM_SOURCE_DATABASE_PASSWORD", debeziumPassword)
    .WithEnvironment("DEBEZIUM_SINK_RABBITMQ_CONNECTION_USERNAME", rabbitmq.Resource.UserNameReference)
    .WithEnvironment("DEBEZIUM_SINK_RABBITMQ_CONNECTION_PASSWORD", rabbitmq.Resource.PasswordParameter)
    .WithHttpHealthCheck("/q/health")
    .WaitFor(requestsDb)
    .WaitFor(rabbitmq)
    .WithContainerLimits(limits.Debezium);
if (useDataVolumes)
{
    debezium.WithVolume("debezium-data", "/debezium/data");
}

// Requests service. The migration runs first, as its own short-lived resource from the same project
// (`--migrate` migrates the database and Wolverine's message store and exits); the API waits for it to
// finish. A separate resource is the simplest ordering Aspire offers (WaitForCompletion), it mirrors the
// Kubernetes job / Argo CD PreSync hook of the later deployment, and the API itself never changes schema at startup.
var otlpEndpoint = builder.Configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"];
var keycloakAuthority = ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/procure");

var requestsMigrate = builder.AddProject<Projects.ProcureAi_Requests_Api>(ResourceNames.RequestsMigrate)
    .WithArgs("--migrate")
    .WithReference(requestsDb)
    .WithReference(rabbitmq)
    .WithReference(redis)
    .WaitFor(requestsDb)
    .WithDotNetLimits(limits.DotNet);

var requestsApi = builder.AddProject<Projects.ProcureAi_Requests_Api>(ResourceNames.RequestsApi)
    // No launchSettings.json: Aspire picks a free port, so dev runs and test runs never clash.
    .WithHttpEndpoint(name: "http")
    .WithReference(requestsDb)
    .WithReference(rabbitmq)
    .WithReference(redis)
    .WithEnvironment("Auth__Authority", keycloakAuthority)
    // The local Keycloak container speaks plain http; the deployed authority is https and keeps the default (true).
    .WithEnvironment("Auth__RequireHttpsMetadata", "false")
    .WithEnvironment("Auth__OpenApi__AuthorizationUrl", ReferenceExpression.Create($"{keycloakAuthority}/protocol/openid-connect/auth"))
    .WithEnvironment("Auth__OpenApi__TokenUrl", ReferenceExpression.Create($"{keycloakAuthority}/protocol/openid-connect/token"))
    .WithEnvironment("Auth__AdminApiKeySha256", adminApiKeySha256)
    // The Serilog OpenTelemetry sink has no environment-variable fallback, so it is pointed at the dashboard here.
    .WithEnvironment(context =>
    {
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            context.EnvironmentVariables["Serilog__WriteTo__OpenTelemetry__Args__endpoint"] = otlpEndpoint;
        }
    })
    .WaitFor(keycloak)
    .WaitFor(rabbitmq)
    .WaitFor(redis)
    .WaitForCompletion(requestsMigrate)
    .WithHttpHealthCheck("/health/ready")
    .WithDotNetLimits(limits.DotNet);

// The Angular app, as the dev server (ng serve) with a capped Node heap. Nobody opens it directly: the browser only
// talks to the gateway, which proxies everything that is not /bff or /api to it. That keeps the session cookie and the
// XSRF cookie on one origin. Aspire picks a free port and passes it to `ng serve --port` (no external endpoint).
var web = builder.AddJavaScriptApp(ResourceNames.Web, "../../Web/procure-ai-web", "start")
    .WithHttpEndpoint(name: "http", env: "PORT")
    .WithNodeLimits(limits.Angular);
web.WithArgs(context =>
{
    // `--` ends npm's own options: everything after it goes to `ng serve`.
    context.Args.Add("--");
    context.Args.Add("--port");
    context.Args.Add(web.GetEndpoint("http").Property(EndpointProperty.Port));
});

// The gateway: the only door in. Serves the Angular app (proxied to 'web'), signs users in against Keycloak and
// proxies /api to the services.
// External HTTPS endpoint: the session cookie is __Host- prefixed and so needs https (the ASP.NET Core dev
// certificate). Keycloak's gateway client accepts https://localhost:* callbacks.
builder.AddProject<Projects.ProcureAi_Gateway>(ResourceNames.Gateway)
    .WithHttpsEndpoint(name: "https")
    .WithExternalHttpEndpoints()
    .WithReference(redis)
    .WithReference(requestsApi)
    .WithEnvironment("Oidc__Authority", keycloakAuthority)
    // The local Keycloak container speaks plain http; the deployed authority is https and keeps the default (true).
    .WithEnvironment("Oidc__RequireHttpsMetadata", "false")
    .WithEnvironment("Oidc__ClientSecret", keycloakGatewaySecret)
    .WithEnvironment("ReverseProxy__Clusters__requests__Destinations__d1__Address", requestsApi.GetEndpoint("http"))
    .WithEnvironment("ReverseProxy__Clusters__web__Destinations__d1__Address", web.GetEndpoint("http"))
    .WithEnvironment(context =>
    {
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            context.EnvironmentVariables["Serilog__WriteTo__OpenTelemetry__Args__endpoint"] = otlpEndpoint;
        }
    })
    .WaitFor(keycloak)
    .WaitFor(redis)
    .WaitFor(requestsApi)
    .WaitFor(web)
    .WithHttpHealthCheck("/health/ready", endpointName: "https")
    .WithDotNetLimits(limits.DotNet);

builder.Build().Run();
