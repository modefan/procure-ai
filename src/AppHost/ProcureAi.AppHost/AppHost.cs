using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ProcureAi.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

var limits = ResourcesOptions.Load(builder.Configuration);
var useDataVolumes = builder.Configuration.GetValue("Persistence:UseDataVolumes", true);
// Off unless configured (appsettings.json turns it on for local runs): a Debezium whose stored offset is gone drops it and
// streams from a new slot, leaving a CDC gap. Deployments must opt in explicitly. See ServiceResources.AddDebeziumFor.
var debeziumSelfHeal = builder.Configuration.GetValue("Debezium:SelfHealLostOffsets", false);

// Parameters. Secrets come from AppHost user secrets (never the repo); see DEMO PASSWORDS.txt.
var keycloakAdmin = builder.AddParameter("keycloak-admin");
var keycloakAdminPassword = builder.AddParameter("keycloak-admin-password", secret: true);
// Substituted by Keycloak into the ${KC_DEMO_PASSWORD} / ${KC_GATEWAY_SECRET} / ${KC_INTERNAL_SECRET} / ${KC_ACCESS_ADMIN_SECRET} /
// ${KC_DEMO_LOGIN_SECRET} / ${KC_DASHBOARD_SECRET} placeholders of the realm import,
// so the committed realm file holds no passwords or secrets.
var keycloakDemoPassword = builder.AddParameter("keycloak-demo-password", secret: true);
var keycloakGatewaySecret = builder.AddParameter("keycloak-gateway-secret", secret: true);
// Secret of the confidential client `procure-internal`, with which services call each other (client credentials).
var keycloakInternalSecret = builder.AddParameter("keycloak-internal-secret", secret: true);
// Secret of `procure-access-admin`, the client with Keycloak admin rights (users, organizations). Only the Access service gets it.
var keycloakAccessAdminSecret = builder.AddParameter("keycloak-access-admin-secret", secret: true);
// Secret of `procure-demo-login`, the gateway's password-grant client for the one-click demo login (plan 06 ruling 17). Generated on
// the first run and kept in the AppHost user secrets (Parameters:keycloak-demo-login-secret); Keycloak and the gateway read the same value.
var keycloakDemoLoginSecret = builder.AddParameter(
    "keycloak-demo-login-secret", new GenerateParameterDefault { MinLength = 32, Special = false }, secret: true, persist: true);
// Secret of `aspire-dashboard`, the confidential client the deployed (standalone) Aspire dashboard signs users in with (plan 07 ruling 11).
// The AppHost's own dashboard does not use it; Keycloak gets it so the realm import resolves ${KC_DASHBOARD_SECRET} here as in the cluster
// (the cluster's Secret procure-keycloak is made from this same user secret). Generated on the first run, like the demo login secret.
var keycloakDashboardSecret = builder.AddParameter(
    "keycloak-dashboard-secret", new GenerateParameterDefault { MinLength = 32, Special = false }, secret: true, persist: true);
var debeziumPassword = builder.AddParameter("debezium-password", secret: true);
// SHA-256 (hex) of the one admin API key; services compare in constant time.
var adminApiKeySha256 = builder.AddParameter("admin-api-key-sha256", secret: true);
// The admin API key itself, for the demo data seeder only (it calls the gateway with it, on behalf of the demo users; plan 06 ruling 1).
// Its SHA-256 above is what the services compare against; both come from the AppHost user secrets.
var adminApiKey = builder.AddParameter("admin-api-key", secret: true);
// The simulated supplier and ERP: the key the services call them with, and the secret the supplier signs its webhooks with.
var simulatorApiKey = builder.AddParameter("simulator-api-key", secret: true);
var supplierWebhookSecret = builder.AddParameter("supplier-webhook-secret", secret: true);
// The password of `audit_log_reader`, the Postgres role Audit reads the server's audit log with (plan 06 ruling 14). Generated on the first
// run and kept in the AppHost user secrets; Audit's --migrate sets it on the role every run, so changing it needs nothing else.
var auditLogReaderPassword = builder.AddParameter(
    "audit-log-reader-password", new GenerateParameterDefault { MinLength = 32, Special = false }, secret: true, persist: true);

// Postgres: custom image (pgvector + pgaudit), logical WAL for Debezium CDC.
// The image's VOLUME: a named volume mounted exactly there replaces the anonymous one. Moving to pg18 (VOLUME /var/lib/postgresql,
// PGDATA /var/lib/postgresql/18/docker) means changing this mount target and PGDATA together (or back to WithDataVolume()).
const string PostgresVolumeTarget = "/var/lib/postgresql/data";
const string PostgresDataDirectory = PostgresVolumeTarget + "/pgdata";
var postgres = builder.AddPostgres(ResourceNames.Postgres)
    .WithDockerfile("../../../infra/postgres")
    .WithEnvironment("DEBEZIUM_PASSWORD", debeziumPassword)
    .WithArgs(
        "-c", "wal_level=logical",
        "-c", "shared_preload_libraries=pgaudit",
        "-c", "pgaudit.log=ddl,role",
        // csvlog, one file per weekday (log/postgresql-Mon.csv ... in the data directory), overwritten a week later: Audit reads
        // today's file through file_fdw for the pgaudit summary (plan 06 ruling 14). stderr goes to log/postgresql-<Dy>.log, which the image's
        // entrypoint streams to the console, so the server log still reaches the dashboard.
        "-c", "logging_collector=on",
        "-c", "log_destination=stderr,csvlog",
        "-c", "log_filename=postgresql-%a.log",
        "-c", "log_truncate_on_rotation=on",
        "-c", "log_rotation_age=1d",
        "-c", $"shared_buffers={limits.Postgres.SharedBuffers}",
        "-c", $"work_mem={limits.Postgres.WorkMem}",
        "-c", $"max_connections={limits.Postgres.MaxConnections}")
    // PGDATA in a subfolder of the mount (as the image docs advise for mounted volumes): initdb needs an empty folder,
    // and volumes made by earlier versions hold an empty "data" folder.
    .WithEnvironment("PGDATA", PostgresDataDirectory)
    .WithContainerLimits(limits.Postgres);
if (useDataVolumes)
{
    // Not WithDataVolume(): Aspire 13.6 mounts that at /var/lib/postgresql (the Postgres 18 layout), but this pg17 image
    // declares /var/lib/postgresql/data as a VOLUME, so the data went to an anonymous volume and every new Postgres container
    // started empty (while Debezium's offsets survived and it refused to start). Same volume name as WithDataVolume().
    // The mount target and PGDATA must change together (e.g. when moving to pg18; see PostgresVolumeTarget).
    postgres.WithVolume(VolumeNameGenerator.Generate(postgres, "data"), PostgresVolumeTarget);
}
// MongoDB: single-node replica set (needed for change streams / CDC). Aspire's WithReplicaSet generates the
// key file, initiates the set and only reports the server healthy once it is PRIMARY.
#pragma warning disable ASPIREMONGODB001 // experimental Aspire API; replaces ~30 lines of hand-rolled key file and rs.initiate
// TLS (Aspire's developer certificate) is preferred, not required: the services connect with TLS, but debezium-mongo (Java, which does
// not trust the developer certificate) connects on the container network without it (plan 06 ruling 16; SCRAM authentication still applies).
var mongo = builder.AddMongoDB(ResourceNames.Mongo)
    .WithReplicaSet(ResourceNames.MongoReplicaSet)
    .WithTlsMode(MongoDBTlsMode.PreferTls)
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
    .WithEnvironment("KC_DEMO_LOGIN_SECRET", keycloakDemoLoginSecret)
    .WithEnvironment("KC_DASHBOARD_SECRET", keycloakDashboardSecret)
    // KC_APP_BASE_URL and KC_DASHBOARD_BASE_URL stay unset: the realm's defaults (https://localhost) are the local values.
    .WithEnvironment("JAVA_OPTS_KC_HEAP", limits.Keycloak.JavaHeapOpts)
    .WithBindMount("../../../infra/keycloak", "/opt/keycloak/data/import", isReadOnly: true)
    .WithArgs("start-dev", "--import-realm")
    .WithHttpHealthCheck("/health/ready", endpointName: "management")
    .WithContainerLimits(limits.Keycloak);

var mailpit = builder.AddContainer(ResourceNames.Mailpit, "axllent/mailpit", "v1.32.0")
    .WithHttpEndpoint(targetPort: 8025, name: "http")
    .WithEndpoint(targetPort: 1025, name: "smtp", scheme: "tcp")
    .WithHttpHealthCheck("/livez")
    .WithContainerLimits(limits.Mailpit);

// The services, each added the same way: database, `<name>-migrate` (`--migrate`: schema, Wolverine message store, demo
// seed; runs first), `<name>-api` (waits for the migration) and, when it captures tables, `debezium-<db>` (Postgres) or the shared
// `debezium-mongo` (MongoDB collections). See ServiceResources.cs.
// Access comes first: every other service calls it (CallsServices adds the reference and the wait).
var otlpEndpoint = builder.Configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"];
var keycloakAuthority = ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/procure");
var shared = new SharedResources(
    postgres, rabbitmq, redis, mongo, keycloak, keycloakAuthority, adminApiKeySha256.Resource, keycloakInternalSecret.Resource, keycloakAccessAdminSecret.Resource, otlpEndpoint, limits, debeziumPassword.Resource, useDataVolumes, debeziumSelfHeal);

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
    new ServiceSpec("budgets", ResourceNames.BudgetsDb, CapturedTables: ["budgets", "budget_reservations", "budget_spends", "audit_entries"], GatewayCluster: "budgets", ApiRoutePrefix: "/api/budgets"),
    shared)
    // Every service but Access asks Access for permissions (and approvers, names): service discovery needs the reference.
    .CallsServices(accessApi);
// The approval saga's table (approval_sagas) is not captured: the steps carry what the audit needs.
var approvalsApi = builder.AddProcureAiService<Projects.ProcureAi_Approvals_Api>(
    new ServiceSpec("approvals", ResourceNames.ApprovalsDb, CapturedTables: ["approval_steps", "audit_entries"], GatewayCluster: "approvals", ApiRoutePrefix: "/api/approvals"),
    shared)
    .CallsServices(accessApi);
// Audit (Marten): stores every integration event. Nothing of its own is captured (no Debezium for auditdb). It also summarises the
// server's pgaudit log as `audit_log_reader` (its --migrate creates the role and the file_fdw table).
var auditApi = builder.AddProcureAiService<Projects.ProcureAi_Audit_Api>(
    new ServiceSpec("audit", ResourceNames.AuditDb, CapturedTables: [], GatewayCluster: "audit", ApiRoutePrefix: "/api/audit"),
    shared)
    .CallsServices(accessApi)
    .WithAuditLogReader(builder, postgres, ResourceNames.AuditDb, auditLogReaderPassword);
// Suppliers (MongoDB): the catalog of suppliers and items per tenant. It publishes no events, so it has no message store. Its suppliers are
// captured by the one MongoDB Debezium (debezium-mongo, plan 06 ruling 16): a supplier changed outside the app (mongosh) raises an alert.
var suppliersApi = builder.AddProcureAiService<Projects.ProcureAi_Suppliers_Api>(
    new ServiceSpec("suppliers", ResourceNames.SuppliersDb, CapturedTables: ["suppliers"], GatewayCluster: "suppliers", ApiRoutePrefix: "/api/suppliers", Store: ServiceStore.MongoDB),
    shared)
    .CallsServices(accessApi);
// Requests checks with Approvals that a request can be approved before it submits it, and takes line descriptions and prices from
// the Suppliers catalog (503 when either cannot be found).
var requestsApi = builder.AddProcureAiService<Projects.ProcureAi_Requests_Api>(
    new ServiceSpec("requests", ResourceNames.RequestsDb, CapturedTables: ["purchase_requests", "request_lines", "audit_entries"], GatewayCluster: "requests", ApiRoutePrefix: "/api/requests"),
    shared)
    .CallsServices(accessApi, approvalsApi, suppliersApi);
// The simulators: someone else's supplier and ERP (slow, sometimes failing, with a chaos switch), in one process. No database, no
// messaging: they hold state in memory and answer by signed webhook, sent straight to Purchasing and Invoicing (wired below, once those
// exist). They depend on no service, so nothing waits for them: a webhook to a receiver that is not up yet is retried by the simulator.
// How fast the supplier confirms and invoices comes from the AppHost's Simulators:Supplier section (system tests set seconds).
var simulatorSupplier = builder.Configuration.GetSection("Simulators:Supplier");
var simulators = builder.AddProject<Projects.ProcureAi_Simulators>(ResourceNames.Simulators)
    .WithHttpEndpoint(name: "http")
    .WithEnvironment("DevPanel__Enabled", "true")
    .WithEnvironment("Simulators__ApiKey", simulatorApiKey)
    .WithEnvironment("Simulators__Webhooks__Secret", supplierWebhookSecret)
    .WithEnvironment("Simulators__Supplier__ConfirmDelay", simulatorSupplier["ConfirmDelay"] ?? throw new InvalidOperationException("Simulators:Supplier:ConfirmDelay is missing."))
    .WithEnvironment("Simulators__Supplier__InvoiceDelay", simulatorSupplier["InvoiceDelay"] ?? throw new InvalidOperationException("Simulators:Supplier:InvoiceDelay is missing."))
    .WithEnvironment("Auth__AdminApiKeySha256", adminApiKeySha256)
    // The simulators' part of the nightly demo reset (plan 06 ruling 4; off by default, 404), like every service's.
    .WithEnvironment("DemoReset__Enabled", "true")
    .WithHttpHealthCheck("/health/ready")
    .WithDotNetLimits(limits.DotNet);
// Purchasing: orders of approved requests, sent to the supplier (the simulator) through its anti-corruption layer with the simulator
// key, confirmed by the supplier's signed webhooks (the secret both sides share). It does not wait for the simulator: an unreachable
// supplier is an outage it retries through.
var purchasingApi = builder.AddProcureAiService<Projects.ProcureAi_Purchasing_Api>(
    new ServiceSpec("purchasing", ResourceNames.PurchasingDb, CapturedTables: ["purchase_orders", "order_lines", "goods_receipts", "receipt_lines", "audit_entries"], GatewayCluster: "purchasing", ApiRoutePrefix: "/api/purchasing"),
    shared)
    .CallsServices(accessApi)
    .WithEnvironment("SupplierClient__BaseUrl", simulators.GetEndpoint("http"))
    .WithEnvironment("SupplierClient__ApiKey", simulatorApiKey)
    .WithEnvironment("Webhooks__Sources__supplier__Secret", supplierWebhookSecret);
// The Assistant (plan 05): Postgres assistantdb (pgvector, outbox and inbox; Debezium captures the documents and audit rows) plus MongoDB
// assistantchatdb (conversations, invoice scans; debezium-mongo captures the conversations, values masked by Audit). Its tools read requests, approvals, budgets and suppliers as the signed-in user (token
// relay), so it references and waits for them. The model provider is configuration: without AI settings here it runs on the Fake provider
// (no key, no account); with AppHost user secrets AI:Provider, AI:Endpoint, AI:ApiKey, AI:Chat:Model, AI:Embedding:Model, AI:Guard:Model
// it uses that provider. Each is passed only when set, so nothing is required.
var assistantApi = builder.AddProcureAiService<Projects.ProcureAi_Assistant_Api>(
    new ServiceSpec("assistant", ResourceNames.AssistantDb, CapturedTables: ["policy_documents", "audit_entries"], GatewayCluster: "assistant", ApiRoutePrefix: "/api/assistant", SecondaryMongoDatabase: ResourceNames.AssistantChatDb,
        SecondaryMongoCapturedCollections: ["conversations"]),
    shared)
    .CallsServices(accessApi, requestsApi, approvalsApi, budgetsApi, suppliersApi)
    .WithOptionalAiSettings(builder);
// Invoicing: the supplier's invoices (signed webhooks, the same secret) and finance's, matched against its copy of Purchasing's
// orders (order_views: a read copy, so not captured here), paid and exported to the ERP (the simulator) through its anti-corruption
// layer with the simulator key. Like Purchasing it does not wait for the simulator. An invoice finance enters from a scan keeps the scan the
// Assistant stored (read through its internal endpoint, plan 05 Task 6), so it references and waits for the Assistant.
var invoicingApi = builder.AddProcureAiService<Projects.ProcureAi_Invoicing_Api>(
    new ServiceSpec("invoicing", ResourceNames.InvoicingDb, CapturedTables: ["invoices", "invoice_lines", "payments", "payment_lines", "audit_entries"], GatewayCluster: "invoicing", ApiRoutePrefix: "/api/invoicing"),
    shared)
    .CallsServices(accessApi, assistantApi)
    .WithEnvironment("ErpClient__BaseUrl", simulators.GetEndpoint("http"))
    .WithEnvironment("ErpClient__ApiKey", simulatorApiKey)
    .WithEnvironment("Webhooks__Sources__supplier__Secret", supplierWebhookSecret);

// Notifications: live updates over SignalR (the gateway proxies /hubs/** to it over WebSockets), fed by a buffered queue on the
// services' exchanges, scaled out through the Redis backplane. No database (plan 04 ruling 1): no migrate resource, no Debezium.
// It emails approvers and requesters through Mailpit's SMTP endpoint (the web UI on its http endpoint shows what was sent); the links in
// the emails point at the gateway (set below, once the gateway exists).
var mailpitSmtp = mailpit.GetEndpoint("smtp");
var notificationsApi = builder.AddProcureAiService<Projects.ProcureAi_Notifications_Api>(
    new ServiceSpec("notifications", Database: "", CapturedTables: [], GatewayCluster: "notifications", ApiRoutePrefix: "/api/notifications", Store: ServiceStore.None),
    shared)
    .CallsServices(accessApi)
    .WithEnvironment("Notifications__Smtp__Host", mailpitSmtp.Property(EndpointProperty.Host))
    .WithEnvironment("Notifications__Smtp__Port", mailpitSmtp.Property(EndpointProperty.Port))
    .WithEnvironment("Notifications__Smtp__From", "no-reply@procure-ai.local")
    .WaitFor(mailpit);

// The system status screen (plan 06 ruling 9) lists every service API; one that wrote no status snapshot within the TTL shows as Down.
// The gateway and the simulators write none (the gateway is the door the screen comes through; the simulator shows as the supplier chaos
// switch and the breakers).
ProjectResource[] statusServices =
    [accessApi.Resource, budgetsApi.Resource, approvalsApi.Resource, auditApi.Resource, suppliersApi.Resource, requestsApi.Resource,
     purchasingApi.Resource, assistantApi.Resource, invoicingApi.Resource, notificationsApi.Resource];
for (var i = 0; i < statusServices.Length; i++)
{
    notificationsApi.WithEnvironment($"Status__ExpectedServices__{i}", statusServices[i].Name);
}

// The supplier calls the services directly, not through the gateway (exposing the webhooks publicly is plan 07/08): order webhooks to
// Purchasing, invoice webhooks to Invoicing, both signed with the shared secret. Endpoint references only, no wait: the simulator's own
// delivery retries cover a receiver that is still starting.
simulators
    .WithEnvironment("Simulators__Webhooks__PurchasingUrl", ReferenceExpression.Create($"{purchasingApi.GetEndpoint("http")}/api/v1/purchasing/webhooks/supplier"))
    .WithEnvironment("Simulators__Webhooks__InvoicingUrl", ReferenceExpression.Create($"{invoicingApi.GetEndpoint("http")}/api/v1/invoicing/webhooks/supplier"));

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
// proxies /api to the services and /hubs to Notifications.
// External HTTPS endpoint: the session cookie is __Host- prefixed and so needs https (the ASP.NET Core dev
// certificate). Keycloak's gateway client accepts https://localhost:* callbacks.
var gateway = builder.AddProject<Projects.ProcureAi_Gateway>(ResourceNames.Gateway)
    .WithHttpsEndpoint(name: "https")
    .WithEnvironment("DevPanel__Enabled", "true")
    .WithExternalHttpEndpoints()
    .WithReference(redis)
    .WithReference(requestsApi)
    .WithReference(accessApi)
    .WithReference(budgetsApi)
    .WithReference(approvalsApi)
    .WithReference(auditApi)
    .WithReference(suppliersApi)
    .WithReference(purchasingApi)
    .WithReference(invoicingApi)
    .WithReference(notificationsApi)
    .WithReference(assistantApi)
    .WithEnvironment("Oidc__Authority", keycloakAuthority)
    // The local Keycloak container speaks plain http; the deployed authority is https and keeps the default (true).
    .WithEnvironment("Oidc__RequireHttpsMetadata", "false")
    .WithEnvironment("Oidc__ClientSecret", keycloakGatewaySecret)
    // One-click demo login on the welcome page (off by default in the gateway; on for local runs): the password grant on its own
    // client, with the demo users' password. The browser never sees either.
    .WithEnvironment("DemoLogin__Enabled", "true")
    .WithEnvironment("DemoLogin__ClientSecret", keycloakDemoLoginSecret)
    .WithEnvironment("DemoLogin__Password", keycloakDemoPassword)
    // The gateway asks Access for permissions as the internal client, like the services do.
    .WithEnvironment("Auth__Internal__ClientId", ServiceResources.InternalClientId)
    .WithEnvironment("Auth__Internal__ClientSecret", keycloakInternalSecret)
    .WithEnvironment("Auth__Internal__TokenUrl", ReferenceExpression.Create($"{keycloakAuthority}/protocol/openid-connect/token"))
    // The admin key's SHA-256, like every service: during the demo reset's maintenance window only calls with the valid key write
    // (ProcureAi.DemoData's reset and seed), and only they may set or lift the window (plan 07 Task 0b).
    .WithEnvironment("Auth__AdminApiKeySha256", adminApiKeySha256)
    .WithEnvironment("ReverseProxy__Clusters__requests__Destinations__d1__Address", requestsApi.GetEndpoint("http"))
    .WithEnvironment("ReverseProxy__Clusters__access__Destinations__d1__Address", accessApi.GetEndpoint("http"))
    .WithEnvironment("ReverseProxy__Clusters__budgets__Destinations__d1__Address", budgetsApi.GetEndpoint("http"))
    .WithEnvironment("ReverseProxy__Clusters__approvals__Destinations__d1__Address", approvalsApi.GetEndpoint("http"))
    .WithEnvironment("ReverseProxy__Clusters__audit__Destinations__d1__Address", auditApi.GetEndpoint("http"))
    .WithEnvironment("ReverseProxy__Clusters__suppliers__Destinations__d1__Address", suppliersApi.GetEndpoint("http"))
    .WithEnvironment("ReverseProxy__Clusters__purchasing__Destinations__d1__Address", purchasingApi.GetEndpoint("http"))
    .WithEnvironment("ReverseProxy__Clusters__invoicing__Destinations__d1__Address", invoicingApi.GetEndpoint("http"))
    .WithEnvironment("ReverseProxy__Clusters__notifications__Destinations__d1__Address", notificationsApi.GetEndpoint("http"))
    .WithEnvironment("ReverseProxy__Clusters__assistant__Destinations__d1__Address", assistantApi.GetEndpoint("http"))
    .WithEnvironment("ReverseProxy__Clusters__web__Destinations__d1__Address", web.GetEndpoint("http"))
    .WithOtlpEndpoint(otlpEndpoint)
    .WaitFor(keycloak)
    .WaitFor(redis)
    .WaitFor(requestsApi)
    .WaitFor(accessApi)
    .WaitFor(budgetsApi)
    .WaitFor(approvalsApi)
    .WaitFor(auditApi)
    .WaitFor(suppliersApi)
    .WaitFor(purchasingApi)
    .WaitFor(invoicingApi)
    .WaitFor(notificationsApi)
    .WaitFor(assistantApi)
    .WaitFor(web)
    .WithHttpHealthCheck("/health/ready", endpointName: "https")
    .WithDotNetLimits(limits.DotNet);

// The demo data (plan 06 rulings 1-3): once the gateway is healthy, `seed` drives the scenario requests of acme and globex to every state
// through the gateway with the admin key, as the demo users. It ends when done (exit code 0) and is safe to run again: it resumes what is
// there and creates nothing twice. The gateway serves the development certificate locally, hence the untrusted-certificate switch.
// The dashboard command "Reset demo data" (plan 06 ruling 4) starts the same resource once more with `reset` (clear every service, then seed);
// any other start runs `seed` again. The nightly 02:00 loop is `schedule` (plan 07 turns `reset` into a Kubernetes CronJob instead).
var demoDataRun = new DemoDataRun();
builder.AddProject<Projects.ProcureAi_DemoData>(ResourceNames.DemoData)
    .WithArgs(context => context.Args.Add(demoDataRun.Next()))
    .WithEnvironment("DemoData__GatewayUrl", gateway.GetEndpoint("https"))
    .WithEnvironment("DemoData__SimulatorUrl", simulators.GetEndpoint("http"))
    .WithEnvironment("DemoData__AdminApiKey", adminApiKey)
    .WithEnvironment("DemoData__AllowUntrustedServerCertificate", "true")
    .WaitFor(gateway)
    .WithDotNetLimits(limits.DotNet)
    .WithCommand(
        DemoDataRun.ResetCommand,
        "Reset demo data",
        demoDataRun.ResetAsync,
        new CommandOptions
        {
            Description = "Deletes every service's data (the users who registered in the Demo company stay) and seeds the demo data again.",
            ConfirmationMessage = "Delete all demo data and seed it again? Everything reviewers changed is lost.",
            IconName = "ArrowCounterclockwise",
        });

// The app as users open it: the links in the notification emails. An endpoint reference only (the gateway waits for Notifications, not
// the other way round).
notificationsApi.WithEnvironment("Notifications__Smtp__AppBaseUrl", gateway.GetEndpoint("https"));

builder.Build().Run();
