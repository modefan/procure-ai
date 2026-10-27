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
// Substituted by Keycloak into the ${KC_DEMO_PASSWORD} / ${KC_GATEWAY_SECRET} / ${KC_INTERNAL_SECRET} / ${KC_ACCESS_ADMIN_SECRET} placeholders of the realm import,
// so the committed realm file holds no passwords or secrets.
var keycloakDemoPassword = builder.AddParameter("keycloak-demo-password", secret: true);
var keycloakGatewaySecret = builder.AddParameter("keycloak-gateway-secret", secret: true);
// Secret of the confidential client `procure-internal`, with which services call each other (client credentials).
var keycloakInternalSecret = builder.AddParameter("keycloak-internal-secret", secret: true);
// Secret of `procure-access-admin`, the client with Keycloak admin rights (users, organizations). Only the Access service gets it.
var keycloakAccessAdminSecret = builder.AddParameter("keycloak-access-admin-secret", secret: true);
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
    .WithEnvironment("KC_INTERNAL_SECRET", keycloakInternalSecret)
    .WithEnvironment("KC_ACCESS_ADMIN_SECRET", keycloakAccessAdminSecret)
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

// The services, each added the same way: database, `<name>-migrate` (`--migrate`: schema, Wolverine message store, demo
// seed; runs first), `<name>-api` (waits for the migration) and, when it captures tables, `debezium-<db>`. See ServiceResources.cs.
// Access comes first: every other service calls it (CallsServices adds the reference and the wait).
var otlpEndpoint = builder.Configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"];
var keycloakAuthority = ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/procure");
var shared = new SharedResources(
    postgres, rabbitmq, redis, mongo, keycloak, keycloakAuthority, adminApiKeySha256.Resource, keycloakInternalSecret.Resource, keycloakAccessAdminSecret.Resource, otlpEndpoint, limits, debeziumPassword.Resource, useDataVolumes);

var accessApi = builder.AddProcureAiService<Projects.ProcureAi_Access_Api>(
    new ServiceSpec("access", ResourceNames.AccessDb, CapturedTables: ["permission_matrices", "permission_grants", "departments", "cost_centers", "tenant_users", "audit_entries"], GatewayCluster: "access", ApiRoutePrefix: "/api/access"),
    shared)
    // Self-registration (Task 4): only Access talks to the Keycloak admin API, as `procure-access-admin`. No other service gets this secret.
    .WithEnvironment("Keycloak__AdminBaseUrl", keycloak.GetEndpoint("http"))
    .WithEnvironment("Keycloak__Realm", "procure")
    .WithEnvironment("Keycloak__DemoOrganization", "demo")
    .WithEnvironment("Keycloak__AdminClientId", "procure-access-admin")
    .WithEnvironment("Keycloak__AdminClientSecret", keycloakAccessAdminSecret);
var budgetsApi = builder.AddProcureAiService<Projects.ProcureAi_Budgets_Api>(
    new ServiceSpec("budgets", ResourceNames.BudgetsDb, CapturedTables: ["budgets", "budget_reservations", "audit_entries"], GatewayCluster: "budgets", ApiRoutePrefix: "/api/budgets"),
    shared)
    // Every service but Access asks Access for permissions (and approvers, names): service discovery needs the reference.
    .CallsServices(accessApi);
// The approval saga's table (approval_sagas) is not captured: the steps carry what the audit needs.
var approvalsApi = builder.AddProcureAiService<Projects.ProcureAi_Approvals_Api>(
    new ServiceSpec("approvals", ResourceNames.ApprovalsDb, CapturedTables: ["approval_steps", "audit_entries"], GatewayCluster: "approvals", ApiRoutePrefix: "/api/approvals"),
    shared)
    .CallsServices(accessApi);
// Audit (Marten): stores every integration event. Nothing of its own is captured (no Debezium for auditdb).
var auditApi = builder.AddProcureAiService<Projects.ProcureAi_Audit_Api>(
    new ServiceSpec("audit", ResourceNames.AuditDb, CapturedTables: [], GatewayCluster: "audit", ApiRoutePrefix: "/api/audit"),
    shared)
    .CallsServices(accessApi);
// Requests checks with Approvals that a request can be approved before it submits it (503 when Approvals cannot be found).
var requestsApi = builder.AddProcureAiService<Projects.ProcureAi_Requests_Api>(
    new ServiceSpec("requests", ResourceNames.RequestsDb, CapturedTables: ["purchase_requests", "request_lines", "audit_entries"], GatewayCluster: "requests", ApiRoutePrefix: "/api/requests"),
    shared)
    .CallsServices(accessApi, approvalsApi);
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
    .WithReference(accessApi)
    .WithReference(budgetsApi)
    .WithReference(approvalsApi)
    .WithReference(auditApi)
    .WithEnvironment("Oidc__Authority", keycloakAuthority)
    // The local Keycloak container speaks plain http; the deployed authority is https and keeps the default (true).
    .WithEnvironment("Oidc__RequireHttpsMetadata", "false")
    .WithEnvironment("Oidc__ClientSecret", keycloakGatewaySecret)
    // The gateway asks Access for permissions as the internal client, like the services do.
    .WithEnvironment("Auth__Internal__ClientId", ServiceResources.InternalClientId)
    .WithEnvironment("Auth__Internal__ClientSecret", keycloakInternalSecret)
    .WithEnvironment("Auth__Internal__TokenUrl", ReferenceExpression.Create($"{keycloakAuthority}/protocol/openid-connect/token"))
    .WithEnvironment("ReverseProxy__Clusters__requests__Destinations__d1__Address", requestsApi.GetEndpoint("http"))
    .WithEnvironment("ReverseProxy__Clusters__access__Destinations__d1__Address", accessApi.GetEndpoint("http"))
    .WithEnvironment("ReverseProxy__Clusters__budgets__Destinations__d1__Address", budgetsApi.GetEndpoint("http"))
    .WithEnvironment("ReverseProxy__Clusters__approvals__Destinations__d1__Address", approvalsApi.GetEndpoint("http"))
    .WithEnvironment("ReverseProxy__Clusters__audit__Destinations__d1__Address", auditApi.GetEndpoint("http"))
    .WithEnvironment("ReverseProxy__Clusters__web__Destinations__d1__Address", web.GetEndpoint("http"))
    .WithOtlpEndpoint(otlpEndpoint)
    .WaitFor(keycloak)
    .WaitFor(redis)
    .WaitFor(requestsApi)
    .WaitFor(accessApi)
    .WaitFor(budgetsApi)
    .WaitFor(approvalsApi)
    .WaitFor(auditApi)
    .WaitFor(web)
    .WithHttpHealthCheck("/health/ready", endpointName: "https")
    .WithDotNetLimits(limits.DotNet);

builder.Build().Run();
