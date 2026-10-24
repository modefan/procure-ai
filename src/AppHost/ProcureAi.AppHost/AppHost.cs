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
var debeziumPassword = builder.AddParameter("debezium-password", secret: true);
// SHA-256 (hex) of the one admin API key; services compare in constant time. Consumed from Task 3 on.
builder.AddParameter("admin-api-key-sha256", secret: true);

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

builder.Build().Run();
