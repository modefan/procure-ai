using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace ProcureAi.AppHost.Tests;

/// <summary>
/// The AppHost model only (built, never started: no containers). Every service is added with
/// <c>AddProcureAiService</c>, so every service gets the same migrate and api resources.
/// </summary>
public sealed class ServiceResourcesTests
{
    private static readonly string[] CapResources = ["DOTNET_gcServer", "DOTNET_GCConserveMemory", "DOTNET_GCHeapHardLimit"];

    [Fact]
    public async Task Requests_service_gets_migrate_and_api_resources_with_limits()
    {
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var migrate = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "requests-migrate");
        var api = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "requests-api");

        var migrateEnv = await EnvironmentOfAsync(migrate);
        var apiEnv = await EnvironmentOfAsync(api);

        // The api carries every setting a service needs, including the hash of the admin key (a secret parameter).
        apiEnv.ShouldContainKey("Auth__AdminApiKeySha256");
        apiEnv.ShouldContainKey("Auth__Authority");
        apiEnv.ShouldContainKey("Auth__RequireHttpsMetadata");
        apiEnv.ShouldContainKey("Auth__OpenApi__AuthorizationUrl");
        apiEnv.ShouldContainKey("Auth__OpenApi__TokenUrl");
        apiEnv.ShouldContainKeyAndValue("Auth__InternalClientId", "procure-internal");
        apiEnv.ShouldContainKeyAndValue("Auth__Internal__ClientId", "procure-internal");
        apiEnv.ShouldContainKey("Auth__Internal__ClientSecret");
        apiEnv.ShouldContainKey("Auth__Internal__TokenUrl");
        apiEnv.ShouldContainKey("ConnectionStrings__requestsdb");
        apiEnv.ShouldContainKey("ConnectionStrings__rabbitmq");
        apiEnv.ShouldContainKey("ConnectionStrings__Redis");

        // The migrate step runs --migrate, seeds the demo data, and has the memory caps like the api.
        migrate.Annotations.OfType<CommandLineArgsCallbackAnnotation>().ShouldNotBeEmpty();
        migrateEnv.ShouldContainKeyAndValue("DemoSeed__Enabled", "true");
        foreach (var cap in CapResources)
        {
            migrateEnv.ShouldContainKey(cap);
            apiEnv.ShouldContainKey(cap);
        }

        // The api starts only after the migration finished, and reports readiness on /health/ready.
        api.Annotations.OfType<WaitAnnotation>()
            .ShouldContain(w => w.Resource == migrate && w.WaitType == WaitType.WaitForCompletion);
        api.Annotations.OfType<HealthCheckAnnotation>().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Access_service_gets_its_database_migrate_api_and_debezium()
    {
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var migrate = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "access-migrate");
        var api = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "access-api");
        var debezium = model.Resources.OfType<ContainerResource>().Single(r => r.Name == "debezium-accessdb");

        var migrateEnv = await EnvironmentOfAsync(migrate);
        var apiEnv = await EnvironmentOfAsync(api);
        var debeziumEnv = await EnvironmentOfAsync(debezium);

        apiEnv.ShouldContainKey("ConnectionStrings__accessdb");
        migrateEnv.ShouldContainKeyAndValue("DemoSeed__Enabled", "true");
        debeziumEnv.ShouldContainKeyAndValue("DEBEZIUM_SOURCE_DATABASE_DBNAME", "accessdb");
        debeziumEnv.ShouldContainKeyAndValue(
            "DEBEZIUM_SOURCE_TABLE_INCLUDE_LIST",
            "public.permission_matrices,public.permission_grants,public.departments,public.cost_centers,public.tenant_users,public.audit_entries");
        api.Annotations.OfType<WaitAnnotation>()
            .ShouldContain(w => w.Resource == migrate && w.WaitType == WaitType.WaitForCompletion);
        api.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == debezium && w.WaitType == WaitType.WaitUntilHealthy);
    }

    [Fact]
    public async Task Budgets_service_gets_its_database_migrate_api_and_debezium_and_the_gateway_routes_to_it()
    {
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var migrate = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "budgets-migrate");
        var api = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "budgets-api");
        var debezium = model.Resources.OfType<ContainerResource>().Single(r => r.Name == "debezium-budgetsdb");
        var gateway = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "gateway");

        var migrateEnv = await EnvironmentOfAsync(migrate);
        var apiEnv = await EnvironmentOfAsync(api);
        var debeziumEnv = await EnvironmentOfAsync(debezium);
        var gatewayEnv = await EnvironmentOfAsync(gateway);

        apiEnv.ShouldContainKey("ConnectionStrings__budgetsdb");
        migrateEnv.ShouldContainKeyAndValue("DemoSeed__Enabled", "true");
        debeziumEnv.ShouldContainKeyAndValue("DEBEZIUM_SOURCE_DATABASE_DBNAME", "budgetsdb");
        debeziumEnv.ShouldContainKeyAndValue(
            "DEBEZIUM_SOURCE_TABLE_INCLUDE_LIST", "public.budgets,public.budget_reservations,public.audit_entries");
        api.Annotations.OfType<WaitAnnotation>()
            .ShouldContain(w => w.Resource == migrate && w.WaitType == WaitType.WaitForCompletion);
        api.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == debezium && w.WaitType == WaitType.WaitUntilHealthy);
        gatewayEnv.ShouldContainKey("ReverseProxy__Clusters__budgets__Destinations__d1__Address");
        gateway.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == api);
        foreach (var cap in CapResources)
        {
            apiEnv.ShouldContainKey(cap);
        }
    }

    [Fact]
    public async Task Approvals_service_gets_its_database_migrate_api_and_debezium_and_the_gateway_routes_to_it()
    {
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var migrate = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "approvals-migrate");
        var api = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "approvals-api");
        var debezium = model.Resources.OfType<ContainerResource>().Single(r => r.Name == "debezium-approvalsdb");
        var gateway = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "gateway");

        var apiEnv = await EnvironmentOfAsync(api);
        var debeziumEnv = await EnvironmentOfAsync(debezium);
        var gatewayEnv = await EnvironmentOfAsync(gateway);

        apiEnv.ShouldContainKey("ConnectionStrings__approvalsdb");
        apiEnv.ShouldContainKeyAndValue("Auth__InternalClientId", "procure-internal");
        debeziumEnv.ShouldContainKeyAndValue("DEBEZIUM_SOURCE_DATABASE_DBNAME", "approvalsdb");
        // The saga table is not captured: the steps and the audit rows are.
        debeziumEnv.ShouldContainKeyAndValue("DEBEZIUM_SOURCE_TABLE_INCLUDE_LIST", "public.approval_steps,public.audit_entries");
        api.Annotations.OfType<WaitAnnotation>()
            .ShouldContain(w => w.Resource == migrate && w.WaitType == WaitType.WaitForCompletion);
        api.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == debezium && w.WaitType == WaitType.WaitUntilHealthy);
        gatewayEnv.ShouldContainKey("ReverseProxy__Clusters__approvals__Destinations__d1__Address");
        gateway.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == api);
        foreach (var cap in CapResources)
        {
            apiEnv.ShouldContainKey(cap);
        }
    }

    [Fact]
    public async Task Audit_service_gets_its_database_migrate_and_api_without_debezium_and_the_gateway_routes_to_it()
    {
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var migrate = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "audit-migrate");
        var api = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "audit-api");
        var gateway = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "gateway");

        var apiEnv = await EnvironmentOfAsync(api);
        var migrateEnv = await EnvironmentOfAsync(migrate);
        var gatewayEnv = await EnvironmentOfAsync(gateway);

        model.Resources.OfType<PostgresDatabaseResource>().ShouldContain(d => d.Name == "auditdb");
        apiEnv.ShouldContainKey("ConnectionStrings__auditdb");
        migrateEnv.ShouldContainKey("ConnectionStrings__auditdb");
        apiEnv.ShouldContainKeyAndValue("Auth__InternalClientId", "procure-internal");
        // Audit stores what the others publish; its own documents are not captured.
        model.Resources.ShouldNotContain(r => r.Name == "debezium-auditdb");
        api.Annotations.OfType<WaitAnnotation>()
            .ShouldContain(w => w.Resource == migrate && w.WaitType == WaitType.WaitForCompletion);
        gatewayEnv.ShouldContainKey("ReverseProxy__Clusters__audit__Destinations__d1__Address");
        gateway.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == api);
        foreach (var cap in CapResources)
        {
            apiEnv.ShouldContainKey(cap);
        }
    }

    [Fact]
    public async Task Suppliers_service_gets_its_database_on_mongo_migrate_and_api_without_debezium_and_the_gateway_routes_to_it()
    {
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var migrate = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "suppliers-migrate");
        var api = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "suppliers-api");
        var gateway = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "gateway");

        var apiEnv = await EnvironmentOfAsync(api);
        var migrateEnv = await EnvironmentOfAsync(migrate);
        var gatewayEnv = await EnvironmentOfAsync(gateway);

        model.Resources.OfType<MongoDBDatabaseResource>().ShouldContain(d => d.Name == "suppliersdb");
        model.Resources.OfType<PostgresDatabaseResource>().ShouldNotContain(d => d.Name == "suppliersdb");
        apiEnv.ShouldContainKey("ConnectionStrings__suppliersdb");
        apiEnv.ShouldContainKeyAndValue("Service__Store", "MongoDB");
        migrateEnv.ShouldContainKey("ConnectionStrings__suppliersdb");
        migrateEnv.ShouldContainKeyAndValue("DemoSeed__Enabled", "true");
        model.Resources.ShouldNotContain(r => r.Name == "debezium-suppliersdb");
        api.Annotations.OfType<WaitAnnotation>()
            .ShouldContain(w => w.Resource == migrate && w.WaitType == WaitType.WaitForCompletion);
        gatewayEnv.ShouldContainKey("ReverseProxy__Clusters__suppliers__Destinations__d1__Address");
        gateway.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == api);
        foreach (var cap in CapResources)
        {
            apiEnv.ShouldContainKey(cap);
        }
    }

    /// <summary>A stand-in project for a MongoDB service (the real one is Suppliers; this one exercises the template on its own).</summary>
    private sealed class MongoServiceProject : IProjectMetadata
    {
        public string ProjectPath { get; } = Path.Combine(RepoRoot(), "src", "Services", "Audit", "ProcureAi.Audit.Api", "ProcureAi.Audit.Api.csproj");

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcureAi.slnx")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("ProcureAi.slnx not found above the test output folder.");
        }
    }

    [Fact]
    public async Task Mongo_service_gets_database_on_mongo_and_no_debezium()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "testcontainers.settings.json"), optional: false);
        var limits = ResourcesOptions.Load(builder.Configuration);
        var postgres = builder.AddPostgres("postgres");
        #pragma warning disable ASPIREMONGODB001
        var mongo = builder.AddMongoDB("mongo").WithReplicaSet("rs0");
        #pragma warning restore ASPIREMONGODB001
        var rabbit = builder.AddRabbitMQ("rabbitmq");
        var redis = builder.AddRedis("redis");
        var keycloak = builder.AddContainer("keycloak", "keycloak");
        var secret = builder.AddParameter("secret", secret: true);
        var shared = new SharedResources(
            postgres, rabbit, redis, mongo, keycloak, ReferenceExpression.Create($"http://keycloak/realms/procure"),
            secret.Resource, secret.Resource, secret.Resource, null, limits, secret.Resource, UseDataVolumes: false);

        var api = builder.AddProcureAiService<MongoServiceProject>(
            new ServiceSpec("suppliers", "suppliersdb", CapturedTables: [], GatewayCluster: "suppliers", ApiRoutePrefix: "/api/suppliers", Store: ServiceStore.MongoDB),
            shared);

        var model = builder.Resources;
        var migrate = model.OfType<ProjectResource>().Single(r => r.Name == "suppliers-migrate");
        var apiResource = model.OfType<ProjectResource>().Single(r => r.Name == "suppliers-api");
        apiResource.ShouldBeSameAs(api.Resource);

        // The database lives on the mongo resource, not on Postgres.
        model.OfType<MongoDBDatabaseResource>().Single(d => d.Name == "suppliersdb").Parent.ShouldBeSameAs(mongo.Resource);
        model.OfType<PostgresDatabaseResource>().ShouldNotContain(d => d.Name == "suppliersdb");
        // Both resources get the connection string and tell the shared host that the store is MongoDB.
        foreach (var resource in new[] { migrate, apiResource })
        {
            var env = await EnvironmentOfAsync(resource);
            env.ShouldContainKey("ConnectionStrings__suppliersdb");
            env.ShouldContainKeyAndValue("Service__Store", "MongoDB");
        }

        // Nothing to capture with Debezium (it reads the Postgres WAL), and the api still waits for the migration.
        model.ShouldNotContain(r => r.Name.StartsWith("debezium-", StringComparison.Ordinal));
        apiResource.Annotations.OfType<WaitAnnotation>()
            .ShouldContain(w => w.Resource == migrate && w.WaitType == WaitType.WaitForCompletion);
        await Task.CompletedTask;
    }

    [Fact]
    public void A_mongo_service_cannot_ask_for_debezium_tables()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "testcontainers.settings.json"), optional: false);
        var secret = builder.AddParameter("secret", secret: true);
        #pragma warning disable ASPIREMONGODB001
        var shared = new SharedResources(
            builder.AddPostgres("postgres"), builder.AddRabbitMQ("rabbitmq"), builder.AddRedis("redis"), builder.AddMongoDB("mongo").WithReplicaSet("rs0"),
            builder.AddContainer("keycloak", "keycloak"), ReferenceExpression.Create($"http://keycloak"),
            secret.Resource, secret.Resource, secret.Resource, null, ResourcesOptions.Load(builder.Configuration), secret.Resource, UseDataVolumes: false);
        #pragma warning restore ASPIREMONGODB001

        Should.Throw<ArgumentException>(() => builder.AddProcureAiService<MongoServiceProject>(
            new ServiceSpec("suppliers", "suppliersdb", ["suppliers"], "suppliers", "/api/suppliers", ServiceStore.MongoDB), shared));
    }

    [Fact]
    public async Task Keycloak_gets_the_client_secrets_and_only_keycloak_gets_the_access_admin_secret()
    {
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var keycloak = model.Resources.Single(r => r.Name == "keycloak");

        var keycloakEnv = await EnvironmentOfAsync(keycloak);

        keycloakEnv.ShouldContainKey("KC_INTERNAL_SECRET");
        keycloakEnv.ShouldContainKey("KC_ACCESS_ADMIN_SECRET");
        // The access-admin client holds Keycloak admin rights; only the Access API may be handed its secret.
        var accessAdmin = (ParameterResource)keycloakEnv["KC_ACCESS_ADMIN_SECRET"];
        foreach (var resource in model.Resources.OfType<ProjectResource>())
        {
            var env = await EnvironmentOfAsync(resource);
            if (resource.Name == "access-api")
            {
                env["Keycloak__AdminClientSecret"].ShouldBeSameAs(accessAdmin);
                env["Keycloak__AdminClientId"].ShouldBe("procure-access-admin");
                continue;
            }

            env.Values.ShouldNotContain(v => ReferenceEquals(v, accessAdmin), resource.Name);
            env.Keys.ShouldNotContain(k => k.StartsWith("Keycloak__", StringComparison.OrdinalIgnoreCase), resource.Name);
        }
    }

    [Fact]
    public async Task Captured_database_gets_its_own_debezium_server_after_the_migration()
    {
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var migrate = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "requests-migrate");
        var debezium = model.Resources.OfType<ContainerResource>().Single(r => r.Name == "debezium-requestsdb");
        var api = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "requests-api");

        var env = await EnvironmentOfAsync(debezium);

        env.ShouldContainKeyAndValue("DEBEZIUM_SOURCE_DATABASE_DBNAME", "requestsdb");
        env.ShouldContainKeyAndValue("DEBEZIUM_SOURCE_TOPIC_PREFIX", "requestsdb");
        env.ShouldContainKeyAndValue("DEBEZIUM_SOURCE_SLOT_NAME", "debezium_requestsdb");
        env.ShouldContainKeyAndValue(
            "DEBEZIUM_SOURCE_TABLE_INCLUDE_LIST", "public.purchase_requests,public.request_lines,public.audit_entries");
        env.ShouldContainKey("JAVA_OPTS");
        env.ShouldContainKey("DEBEZIUM_SOURCE_DATABASE_PASSWORD");
        // The publication is created by the migration, so Debezium waits for it to finish.
        debezium.Annotations.OfType<WaitAnnotation>()
            .ShouldContain(w => w.Resource == migrate && w.WaitType == WaitType.WaitForCompletion);
        // The API starts only once Debezium is healthy, so no app change precedes the capture (snapshot.mode=no_data).
        api.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == debezium && w.WaitType == WaitType.WaitUntilHealthy);
        debezium.Annotations.OfType<HealthCheckAnnotation>().ShouldNotBeEmpty();
        debezium.Annotations.OfType<ContainerMountAnnotation>()
            .ShouldContain(m => m.Target == "/debezium/data" && m.Source == "debezium-requestsdb-data");
        debezium.Annotations.OfType<ContainerMountAnnotation>().Select(m => m.Target).ShouldContain("/debezium/config/application.properties");
    }

    /// <summary>
    /// Every outgoing service client (<c>AddProcureAiServiceClient(&lt;name&gt;-api)</c>) resolves its address through Aspire service
    /// discovery, which needs a reference on the calling resource; without one the call fails (a submit answered 503). Access is the
    /// only service that calls nobody: every other service asks Access for permissions, Requests also asks Approvals for the preview.
    /// The caller also waits for what it calls, so it never starts answering before its dependencies can.
    /// </summary>
    [Theory]
    [InlineData("requests-api", new[] { "access-api", "approvals-api" })]
    [InlineData("budgets-api", new[] { "access-api" })]
    [InlineData("approvals-api", new[] { "access-api" })]
    [InlineData("audit-api", new[] { "access-api" })]
    [InlineData("suppliers-api", new[] { "access-api" })]
    [InlineData("access-api", new string[0])]
    public async Task Every_service_references_and_waits_for_the_services_it_calls(string service, string[] calls)
    {
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var api = model.Resources.OfType<ProjectResource>().Single(r => r.Name == service);

        var env = await EnvironmentOfAsync(api);
        var referenced = env.Keys
            .Where(k => k.StartsWith("services__", StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Split("__")[1])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();

        referenced.ShouldBe(calls.Order(StringComparer.Ordinal).ToArray(), $"services {service} can find");
        foreach (var callee in calls)
        {
            api.Annotations.OfType<WaitAnnotation>().ShouldContain(
                w => w.Resource.Name == callee && w.WaitType == WaitType.WaitUntilHealthy, $"{service} waits for {callee}");
        }
    }

    /// <summary>
    /// The whole system's containers together stay within the budget for Claude's work on the shared PC (CLAUDE.md:
    /// containers at most 8 GB and 8 CPUs). Sums the <c>--memory</c> / <c>--cpus</c> caps of every container resource.
    /// </summary>
    [Fact]
    public async Task Container_caps_of_the_whole_system_stay_within_8_GB_and_8_CPUs()
    {
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var totalMemoryMb = 0L;
        var totalCpus = 0.0;
        var containers = model.Resources.OfType<ContainerResource>().ToList();

        foreach (var container in containers)
        {
            var args = new List<object>();
            var context = new ContainerRuntimeArgsCallbackContext(args, CancellationToken.None);
            foreach (var annotation in container.Annotations.OfType<ContainerRuntimeArgsCallbackAnnotation>())
            {
                await annotation.Callback(context);
            }

            var texts = args.Select(a => a.ToString()!).ToList();
            var memory = texts.IndexOf("--memory");
            var cpus = texts.IndexOf("--cpus");
            memory.ShouldBeGreaterThanOrEqualTo(0, $"{container.Name} has a memory cap");
            cpus.ShouldBeGreaterThanOrEqualTo(0, $"{container.Name} has a CPU cap");
            totalMemoryMb += long.Parse(texts[memory + 1].TrimEnd('m'), System.Globalization.CultureInfo.InvariantCulture);
            totalCpus += double.Parse(texts[cpus + 1], System.Globalization.CultureInfo.InvariantCulture);
        }

        containers.Count(c => c.Name.StartsWith("debezium-", StringComparison.Ordinal)).ShouldBe(4, "one Debezium per captured database");
        totalMemoryMb.ShouldBeLessThanOrEqualTo(8 * 1024, "container memory caps in MB");
        totalCpus.ShouldBeLessThanOrEqualTo(8.0, "container CPU caps");
    }

    /// <summary>
    /// The built (never started) AppHost and its model. Must be disposed: an undisposed one still started its complete
    /// stack of containers in the background while the test process lived on (for the InfrastructureStartsTests fixture),
    /// so every test here leaked a full stack: five stacks per run, which exhausted the CI runner until the job timed out.
    /// </summary>
    private sealed class ModelScope : IAsyncDisposable
    {
        private readonly IDistributedApplicationTestingBuilder _builder;
        private readonly DistributedApplication _app;

        private ModelScope(IDistributedApplicationTestingBuilder builder, DistributedApplication app)
        {
            _builder = builder;
            _app = app;
        }

        public DistributedApplicationModel Model => _app.Services.GetRequiredService<DistributedApplicationModel>();

        public static async Task<ModelScope> BuildAsync()
        {
            var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.ProcureAi_AppHost>([]);
            return new ModelScope(builder, await builder.BuildAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            await _builder.DisposeAsync();
        }
    }

    /// <summary>The environment variables the resource's callbacks set (secret parameters stay unresolved, so no secrets are needed).</summary>
    private static async Task<Dictionary<string, object>> EnvironmentOfAsync(IResource resource)
    {
        var variables = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var context = new EnvironmentCallbackContext(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), resource, variables);
        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await annotation.Callback(context);
        }

        return variables;
    }
}
