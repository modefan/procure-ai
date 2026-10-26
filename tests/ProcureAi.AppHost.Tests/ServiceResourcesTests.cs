using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
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
