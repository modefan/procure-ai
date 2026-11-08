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
        apiEnv.ShouldContainKeyAndValue("DevPanel__Enabled", "true");
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

    /// <summary>Keycloak's own connections to the Postgres server (it keeps its data elsewhere; budgeted at 20 to be safe).</summary>
    private const int KeycloakConnections = 20;

    /// <summary>
    /// How many Npgsql pools one service opens, each capped at <c>Database:MaxPoolSize</c> (checked in the Npgsql, Wolverine.Postgresql 6.53
    /// and Marten 9.54 assemblies, plan 07 Task 0c fix round): <c>new NpgsqlConnection(cs)</c> (readiness, status, the same string in EF Core)
    /// shares one pool per connection string, but <c>PostgresqlBackedPersistence.BuildMessageStore</c> calls <c>NpgsqlDataSource.Create</c>
    /// (its own pool), Marten's <c>Connection(cs)</c> builds its own <c>NpgsqlDataSourceBuilder</c> data source (Audit's Wolverine outbox
    /// uses Marten's), and EF Core with <c>UseVector()</c> (Assistant) builds a data source of its own besides the shared one.
    /// </summary>
    private static readonly Dictionary<string, int> PoolsPerService = new()
    {
        ["access-api"] = 2,      // plain/EF Core (one shared pool) + Wolverine's message store
        ["approvals-api"] = 2,   // plain/EF Core + Wolverine
        ["budgets-api"] = 2,     // plain/EF Core + Wolverine
        ["purchasing-api"] = 2,  // plain/EF Core + Wolverine
        ["invoicing-api"] = 2,   // plain/EF Core + Wolverine
        ["requests-api"] = 2,    // plain/EF Core + Wolverine
        ["audit-api"] = 2,       // plain connections + Marten (its Wolverine store is Marten's)
        ["assistant-api"] = 3,   // plain + EF Core with UseVector() (own data source; its cap: AssistantServiceTests.Vector_data_source_carries_the_max_pool_size) + Wolverine
    };

    /// <summary>
    /// There is deliberately no default: a Postgres service missing from <see cref="PoolsPerService"/> fails the test, so a new service or
    /// an added pool cannot leave the budget check green by accident.
    /// </summary>
    private static int PoolsOf(string service) =>
        PoolsPerService.TryGetValue(service, out var pools)
            ? pools
            : throw new Xunit.Sdk.XunitException(
                $"'{service}' uses Postgres but has no entry in PoolsPerService. Count its Npgsql pools (plain/EF Core, Wolverine's message store, " +
                "Marten, an EF Core UseVector() data source), add them, and check that Pool_sizes_fit_max_connections still holds.");

    /// <summary>
    /// The worst case: every pool of every Postgres API full at once, plus Debezium, Keycloak and the reserved connections. Assumptions
    /// (plan 08 Task 6, carry-over 30): one replica per service (values.yaml has no replicas above 1), and no overlap of old and new pods:
    /// a deploy's migration Jobs (Sync hooks, all at once) run while the old API pods still hold their pools, and the chart's maxSurge 0
    /// (envs/local) replaces a pod only after the old one is gone. A migration opens at most its plain/EF Core pool, so the overlap would add
    /// up to one pool per service; measured on the cluster, idle pools stay far below their cap (Npgsql prunes idle connections). If replicas
    /// ever exceed 1, or a surge is allowed, add the extra pods (and the migrations) to this sum. Wolverine's listener connections come from
    /// its message store's pool and are inside its cap.
    /// </summary>
    [Fact]
    public async Task Pool_sizes_fit_max_connections()
    {
        await using var scope = await ModelScope.BuildAsync();
        var postgres = ResourcesOptions.Load(new ConfigurationBuilder().AddJsonFile("apphost.appsettings.json").Build()).Postgres;
        var postgresApis = new List<(string Name, Dictionary<string, object> Env)>();
        foreach (var api in scope.Model.Resources.OfType<ProjectResource>().Where(r => r.Name.EndsWith("-api", StringComparison.Ordinal)))
        {
            var env = await EnvironmentOfAsync(api);
            if (env.TryGetValue("Service__Store", out var store) && store.ToString() == "Postgres")
            {
                postgresApis.Add((api.Name, env));
            }
        }

        postgresApis.ShouldNotBeEmpty();
        foreach (var (name, env) in postgresApis)
        {
            env.ShouldContainKeyAndValue("Database__MaxPoolSize", postgres.PoolSizePerService.ToString(System.Globalization.CultureInfo.InvariantCulture), name);
        }

        // One replication connection per Debezium Server on a Postgres database (the MongoDB one does not connect to Postgres).
        var debeziumServers = scope.Model.Resources.OfType<ContainerResource>()
            .Count(r => r.Name.StartsWith("debezium-", StringComparison.Ordinal) && r.Name != "debezium-mongo");
        debeziumServers.ShouldBeGreaterThan(0);

        var pools = postgresApis.Sum(a => PoolsOf(a.Name));
        var worstCase = pools * postgres.PoolSizePerService + debeziumServers + KeycloakConnections + postgres.ReservedConnections;
        worstCase.ShouldBeLessThanOrEqualTo(postgres.MaxConnections, $"{pools} pools x {postgres.PoolSizePerService} + {debeziumServers} Debezium + Keycloak + reserved");
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
            "public.permission_matrices,public.permission_grants,public.departments,public.cost_centers,public.tenant_users,public.system_settings,public.audit_entries");
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
            "DEBEZIUM_SOURCE_TABLE_INCLUDE_LIST", "public.budgets,public.budget_reservations,public.budget_spends,public.audit_entries");
        api.Annotations.OfType<WaitAnnotation>()
            .ShouldContain(w => w.Resource == migrate && w.WaitType == WaitType.WaitForCompletion);
        api.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == debezium && w.WaitType == WaitType.WaitUntilHealthy);
        gatewayEnv.ShouldContainKeyAndValue("DevPanel__Enabled", "true");
        gatewayEnv.ShouldContainKey("ReverseProxy__Clusters__budgets__Destinations__d1__Address");
        // The maintenance window of the demo reset lets only the valid admin key write (plan 07 Task 0b).
        gatewayEnv.ShouldContainKey("Auth__AdminApiKeySha256");
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
    public async Task Suppliers_service_gets_its_database_on_mongo_migrate_api_and_debezium_mongo_and_the_gateway_routes_to_it()
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
        // The suppliers are captured by the one MongoDB Debezium (plan 06 ruling 16), not by a Postgres-style one of their own.
        model.Resources.ShouldNotContain(r => r.Name == "debezium-suppliersdb");
        var debezium = model.Resources.OfType<ContainerResource>().Single(r => r.Name == "debezium-mongo");
        api.Annotations.OfType<WaitAnnotation>()
            .ShouldContain(w => w.Resource == migrate && w.WaitType == WaitType.WaitForCompletion);
        api.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == debezium && w.WaitType == WaitType.WaitUntilHealthy);
        gatewayEnv.ShouldContainKey("ReverseProxy__Clusters__suppliers__Destinations__d1__Address");
        gateway.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == api);
        foreach (var cap in CapResources)
        {
            apiEnv.ShouldContainKey(cap);
        }
    }

    [Fact]
    public async Task Purchasing_service_gets_its_database_debezium_the_supplier_settings_and_the_gateway_routes_to_it()
    {
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var migrate = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "purchasing-migrate");
        var api = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "purchasing-api");
        var debezium = model.Resources.OfType<ContainerResource>().Single(r => r.Name == "debezium-purchasingdb");
        var gateway = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "gateway");

        var apiEnv = await EnvironmentOfAsync(api);
        var debeziumEnv = await EnvironmentOfAsync(debezium);
        var gatewayEnv = await EnvironmentOfAsync(gateway);

        apiEnv.ShouldContainKey("ConnectionStrings__purchasingdb");
        debeziumEnv.ShouldContainKeyAndValue(
            "DEBEZIUM_SOURCE_TABLE_INCLUDE_LIST",
            "public.purchase_orders,public.order_lines,public.goods_receipts,public.receipt_lines,public.audit_entries");
        // The supplier (the simulator): its address, the key Purchasing presents and the secret its webhooks are signed with (secret parameters).
        apiEnv.ShouldContainKey("SupplierClient__BaseUrl");
        apiEnv["SupplierClient__ApiKey"].ShouldBeOfType<ParameterResource>().Name.ShouldBe("simulator-api-key");
        apiEnv["Webhooks__Sources__supplier__Secret"].ShouldBeOfType<ParameterResource>().Name.ShouldBe("supplier-webhook-secret");
        api.Annotations.OfType<WaitAnnotation>()
            .ShouldContain(w => w.Resource == migrate && w.WaitType == WaitType.WaitForCompletion);
        api.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == debezium && w.WaitType == WaitType.WaitUntilHealthy);
        gatewayEnv.ShouldContainKey("ReverseProxy__Clusters__purchasing__Destinations__d1__Address");
        gateway.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == api);
        foreach (var cap in CapResources)
        {
            apiEnv.ShouldContainKey(cap);
        }
    }

    [Fact]
    public async Task Invoicing_service_gets_its_database_debezium_the_erp_settings_and_the_gateway_routes_to_it()
    {
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var migrate = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "invoicing-migrate");
        var api = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "invoicing-api");
        var debezium = model.Resources.OfType<ContainerResource>().Single(r => r.Name == "debezium-invoicingdb");
        var gateway = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "gateway");

        var apiEnv = await EnvironmentOfAsync(api);
        var debeziumEnv = await EnvironmentOfAsync(debezium);
        var gatewayEnv = await EnvironmentOfAsync(gateway);

        apiEnv.ShouldContainKey("ConnectionStrings__invoicingdb");
        // The order views are a read copy of Purchasing's orders (captured there), so they are not captured here.
        debeziumEnv.ShouldContainKeyAndValue(
            "DEBEZIUM_SOURCE_TABLE_INCLUDE_LIST",
            "public.invoices,public.invoice_lines,public.payments,public.payment_lines,public.audit_entries");
        // The ERP (the simulator): its address, the key Invoicing presents, and the secret the supplier's invoice webhooks are signed with.
        apiEnv.ShouldContainKey("ErpClient__BaseUrl");
        apiEnv["ErpClient__ApiKey"].ShouldBeOfType<ParameterResource>().Name.ShouldBe("simulator-api-key");
        apiEnv["Webhooks__Sources__supplier__Secret"].ShouldBeOfType<ParameterResource>().Name.ShouldBe("supplier-webhook-secret");
        api.Annotations.OfType<WaitAnnotation>()
            .ShouldContain(w => w.Resource == migrate && w.WaitType == WaitType.WaitForCompletion);
        api.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == debezium && w.WaitType == WaitType.WaitUntilHealthy);
        gatewayEnv.ShouldContainKey("ReverseProxy__Clusters__invoicing__Destinations__d1__Address");
        gateway.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == api);
        foreach (var cap in CapResources)
        {
            apiEnv.ShouldContainKey(cap);
        }
    }

    [Fact]
    public async Task Simulators_get_their_secrets_and_the_dotnet_limits_and_need_nothing_else()
    {
        await using var scope = await ModelScope.BuildAsync();
        var simulators = scope.Model.Resources.OfType<ProjectResource>().Single(r => r.Name == "simulators");

        var env = await EnvironmentOfAsync(simulators);

        // The simulated supplier and ERP: their API key and the webhook secret (secret parameters), and the admin key hash for the chaos switch.
        env.ShouldContainKeyAndValue("DevPanel__Enabled", "true");
        env.ShouldContainKey("Simulators__ApiKey");
        env.ShouldContainKey("Simulators__Webhooks__Secret");
        env.ShouldContainKey("Auth__AdminApiKeySha256");
        foreach (var cap in CapResources)
        {
            env.ShouldContainKey(cap);
        }

        // The supplier's webhooks go straight to the services' own endpoints (ruling 10): orders to Purchasing, invoices to Invoicing.
        var purchasingUrl = env["Simulators__Webhooks__PurchasingUrl"].ShouldBeOfType<ReferenceExpression>();
        purchasingUrl.ValueProviders.OfType<EndpointReference>().Single().Resource.Name.ShouldBe("purchasing-api");
        purchasingUrl.Format.ShouldEndWith("/api/v1/purchasing/webhooks/supplier");
        var invoicingUrl = env["Simulators__Webhooks__InvoicingUrl"].ShouldBeOfType<ReferenceExpression>();
        invoicingUrl.ValueProviders.OfType<EndpointReference>().Single().Resource.Name.ShouldBe("invoicing-api");
        invoicingUrl.Format.ShouldEndWith("/api/v1/invoicing/webhooks/supplier");
        // How fast the supplier confirms and invoices comes from the AppHost configuration (system tests use seconds).
        env.ShouldContainKeyAndValue("Simulators__Supplier__ConfirmDelay", "00:00:03");
        env.ShouldContainKeyAndValue("Simulators__Supplier__InvoiceDelay", "00:00:20");

        simulators.Annotations.OfType<HealthCheckAnnotation>().ShouldNotBeEmpty();
        // No database, no broker, no other service to wait for: it only answers calls and sends webhooks (and retries them).
        simulators.Annotations.OfType<WaitAnnotation>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Notifications_gets_the_services_the_status_screen_expects()
    {
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var api = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "notifications-api");
        var env = await EnvironmentOfAsync(api);

        var expected = env.Where(e => e.Key.StartsWith("Status__ExpectedServices__", StringComparison.Ordinal))
            .OrderBy(e => int.Parse(e.Key["Status__ExpectedServices__".Length..], System.Globalization.CultureInfo.InvariantCulture))
            .Select(e => e.Value.ToString()!)
            .ToList();
        // Every service API (each writes a status snapshot through ServiceHost); not the gateway, the simulators or the migrate runs.
        var apis = model.Resources.OfType<ProjectResource>().Select(r => r.Name).Where(n => n.EndsWith("-api", StringComparison.Ordinal)).Order().ToList();
        expected.Order().ShouldBe(apis);
        expected.Count.ShouldBe(10);
    }

    [Fact]
    public async Task Notifications_service_has_no_database_and_no_migrate_and_the_gateway_routes_to_it()
    {
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var api = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "notifications-api");
        var gateway = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "gateway");

        var apiEnv = await EnvironmentOfAsync(api);
        var gatewayEnv = await EnvironmentOfAsync(gateway);

        // No database (plan 04 ruling 1): no migrate resource, no database resource, no connection string for one, no Debezium.
        model.Resources.ShouldNotContain(r => r.Name == "notifications-migrate");
        model.Resources.ShouldNotContain(r => r.Name.StartsWith("notificationsdb", StringComparison.Ordinal));
        model.Resources.ShouldNotContain(r => r.Name.StartsWith("debezium-notifications", StringComparison.Ordinal));
        apiEnv.ShouldContainKeyAndValue("Service__Store", "None");
        apiEnv.Keys.ShouldNotContain(k => k.StartsWith("ConnectionStrings__notifications", StringComparison.OrdinalIgnoreCase));
        api.Annotations.OfType<WaitAnnotation>().ShouldNotContain(w => w.WaitType == WaitType.WaitForCompletion);
        // RabbitMQ (the live queue), Redis (the SignalR backplane) and Keycloak (tokens) like every service.
        apiEnv.ShouldContainKey("ConnectionStrings__rabbitmq");
        apiEnv.ShouldContainKey("ConnectionStrings__Redis");
        apiEnv.ShouldContainKey("Auth__Authority");
        apiEnv.ShouldContainKey("Auth__AdminApiKeySha256");
        foreach (var dependency in new[] { "rabbitmq", "redis", "keycloak" })
        {
            api.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource.Name == dependency, dependency);
        }

        api.Annotations.OfType<HealthCheckAnnotation>().ShouldNotBeEmpty();
        foreach (var cap in CapResources)
        {
            apiEnv.ShouldContainKey(cap);
        }

        // Email through Mailpit's SMTP endpoint, with links to the gateway (plan 04 Task 1b).
        apiEnv.ShouldContainKey("Notifications__Smtp__Host");
        apiEnv.ShouldContainKey("Notifications__Smtp__Port");
        apiEnv.ShouldContainKey("Notifications__Smtp__From");
        apiEnv.ShouldContainKey("Notifications__Smtp__AppBaseUrl");
        api.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource.Name == "mailpit");

        // The gateway proxies its API and the hub (/hubs/**) to it.
        gatewayEnv.ShouldContainKey("ReverseProxy__Clusters__notifications__Destinations__d1__Address");
        gateway.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == api);
    }

    /// <summary>
    /// The Assistant (plan 05 ruling 4): Postgres <c>assistantdb</c> (pgvector, outbox and inbox) as its main store with its own Debezium
    /// (documents and audit rows), MongoDB <c>assistantchatdb</c> as its second database, and calls to the services its tools read as the
    /// user. With no AI settings in the AppHost's configuration it runs on the Fake provider: no <c>AI__*</c> variable at all.
    /// </summary>
    [Fact]
    public async Task Assistant_service_resources()
    {
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var migrate = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "assistant-migrate");
        var api = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "assistant-api");
        var debezium = model.Resources.OfType<ContainerResource>().Single(r => r.Name == "debezium-assistantdb");
        var gateway = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "gateway");
        var mongo = model.Resources.OfType<MongoDBServerResource>().Single();

        var migrateEnv = await EnvironmentOfAsync(migrate);
        var apiEnv = await EnvironmentOfAsync(api);
        var debeziumEnv = await EnvironmentOfAsync(debezium);
        var gatewayEnv = await EnvironmentOfAsync(gateway);

        model.Resources.OfType<PostgresDatabaseResource>().ShouldContain(d => d.Name == "assistantdb");
        var chat = model.Resources.OfType<MongoDBDatabaseResource>().Single(d => d.Name == "assistantchatdb");
        chat.Parent.ShouldBeSameAs(mongo);
        model.Resources.OfType<PostgresDatabaseResource>().ShouldNotContain(d => d.Name == "assistantchatdb");
        foreach (var env in new[] { migrateEnv, apiEnv })
        {
            env.ShouldContainKey("ConnectionStrings__assistantdb");
            env.ShouldContainKey("ConnectionStrings__assistantchatdb");
            env.ShouldContainKeyAndValue("Service__Store", "Postgres");
            env.ShouldContainKeyAndValue("Service__SecondaryMongoConnectionName", "assistantchatdb");
        }

        migrateEnv.ShouldContainKeyAndValue("DemoSeed__Enabled", "true");
        debeziumEnv.ShouldContainKeyAndValue("DEBEZIUM_SOURCE_DATABASE_DBNAME", "assistantdb");
        debeziumEnv.ShouldContainKeyAndValue("DEBEZIUM_SOURCE_TABLE_INCLUDE_LIST", "public.policy_documents,public.audit_entries");
        api.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == migrate && w.WaitType == WaitType.WaitForCompletion);
        api.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == debezium && w.WaitType == WaitType.WaitUntilHealthy);
        migrate.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == chat, "the migration creates the chat database's indexes");
        api.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == chat);
        gatewayEnv.ShouldContainKey("ReverseProxy__Clusters__assistant__Destinations__d1__Address");
        gateway.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == api);
        foreach (var cap in CapResources)
        {
            apiEnv.ShouldContainKey(cap);
        }

        // The Fake provider by default: nothing to pass, nothing required.
        apiEnv.Keys.ShouldNotContain(k => k.StartsWith("AI__", StringComparison.OrdinalIgnoreCase));
        migrateEnv.Keys.ShouldNotContain(k => k.StartsWith("AI__", StringComparison.OrdinalIgnoreCase));
        model.Resources.OfType<ParameterResource>().ShouldNotContain(p => p.Name.StartsWith("ai-", StringComparison.Ordinal));
    }

    /// <summary>A real provider is configuration only: the AppHost's AI settings (user secrets) reach the API, the key as a secret parameter.</summary>
    [Fact]
    public async Task Assistant_gets_the_ai_settings_the_apphost_is_configured_with()
    {
        await using var scope = await ModelScope.BuildAsync(
        [
            "AI:Provider=AzureFoundry", "AI:Endpoint=https://example.openai.azure.com/openai/v1/", "AI:ApiKey=test-only-key",
            "AI:Chat:Model=chat", "AI:Embedding:Model=embeddings", "AI:Guard:Model=guard", "AI:Fake:StreamDelayMs=120",
            "AI:Chat:ReasoningEffort=None", "AI:Chat:MinOutputTokens=256", "AI:Chat:SupportsTemperature=false", "AI:DailyTokenCapPerTenant=150000",
            "AI:Prices:InputPerMillionUsd=0.25", "AI:Prices:OutputPerMillionUsd=2", "AI:Prices:EmbeddingPerMillionUsd=0.022",
        ]);
        var api = scope.Model.Resources.OfType<ProjectResource>().Single(r => r.Name == "assistant-api");

        var env = await EnvironmentOfAsync(api);

        env.ShouldContainKeyAndValue("AI__Provider", "AzureFoundry");
        env.ShouldContainKeyAndValue("AI__Endpoint", "https://example.openai.azure.com/openai/v1/");
        env.ShouldContainKeyAndValue("AI__Chat__Model", "chat");
        env.ShouldContainKeyAndValue("AI__Embedding__Model", "embeddings");
        env.ShouldContainKeyAndValue("AI__Guard__Model", "guard");
        env.ShouldContainKeyAndValue("AI__Fake__StreamDelayMs", "120");
        env.ShouldContainKeyAndValue("AI__Chat__ReasoningEffort", "None");
        env.ShouldContainKeyAndValue("AI__Chat__SupportsTemperature", "false");
        env.ShouldContainKeyAndValue("AI__Chat__MinOutputTokens", "256");
        env.ShouldContainKeyAndValue("AI__DailyTokenCapPerTenant", "150000");
        env.ShouldContainKeyAndValue("AI__Prices__InputPerMillionUsd", "0.25");
        env.ShouldContainKeyAndValue("AI__Prices__OutputPerMillionUsd", "2");
        env.ShouldContainKeyAndValue("AI__Prices__EmbeddingPerMillionUsd", "0.022");
        var key = env["AI__ApiKey"].ShouldBeOfType<ParameterResource>();
        key.Secret.ShouldBeTrue("the key is shown masked in the dashboard");
        // Only the Assistant gets the key.
        foreach (var other in scope.Model.Resources.OfType<ProjectResource>().Where(r => r.Name != "assistant-api"))
        {
            (await EnvironmentOfAsync(other)).Values.ShouldNotContain(v => ReferenceEquals(v, key), other.Name);
        }
    }

    [Fact]
    public void A_service_without_a_database_cannot_ask_for_debezium_tables()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "testcontainers.settings.json"), optional: false);
        var secret = builder.AddParameter("secret", secret: true);
        #pragma warning disable ASPIREMONGODB001
        var shared = new SharedResources(
            builder.AddPostgres("postgres"), builder.AddRabbitMQ("rabbitmq"), builder.AddRedis("redis"), builder.AddMongoDB("mongo").WithReplicaSet("rs0"),
            builder.AddContainer("keycloak", "keycloak"), ReferenceExpression.Create($"http://keycloak"),
            secret.Resource, secret.Resource, secret.Resource, null, ResourcesOptions.Load(builder.Configuration), UseDataVolumes: false);
        #pragma warning restore ASPIREMONGODB001

        // A valid database name, so only the "no capture without Postgres" rule can throw (an empty name would throw anyway).
        var refused = Should.Throw<ArgumentException>(() => builder.AddProcureAiService<MongoServiceProject>(
            new ServiceSpec("notifications", "notificationsdb", ["x"], "notifications", "/api/notifications", ServiceStore.None), shared));
        refused.Message.ShouldContain("CapturedTables must be empty");
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
            secret.Resource, secret.Resource, secret.Resource, null, limits, UseDataVolumes: false);

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
    public void A_second_mongo_database_is_only_for_a_postgres_service()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "testcontainers.settings.json"), optional: false);
        var secret = builder.AddParameter("secret", secret: true);
        #pragma warning disable ASPIREMONGODB001
        var shared = new SharedResources(
            builder.AddPostgres("postgres"), builder.AddRabbitMQ("rabbitmq"), builder.AddRedis("redis"), builder.AddMongoDB("mongo").WithReplicaSet("rs0"),
            builder.AddContainer("keycloak", "keycloak"), ReferenceExpression.Create($"http://keycloak"),
            secret.Resource, secret.Resource, secret.Resource, null, ResourcesOptions.Load(builder.Configuration), UseDataVolumes: false);
        #pragma warning restore ASPIREMONGODB001

        var refused = Should.Throw<ArgumentException>(() => builder.AddProcureAiService<MongoServiceProject>(
            new ServiceSpec("suppliers", "suppliersdb", [], "suppliers", "/api/suppliers", ServiceStore.MongoDB, SecondaryMongoDatabase: "otherdb"), shared));
        refused.Message.ShouldContain("SecondaryMongoDatabase");
    }

    /// <summary>
    /// A MongoDB service may capture collections (plan 06 ruling 16): they go to the one <c>debezium-mongo</c>, which waits for the service's
    /// migration, and the api waits for it (no change made through the app precedes the capture).
    /// </summary>
    [Fact]
    public async Task Mongo_service_with_captured_collections_gets_debezium_mongo()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "testcontainers.settings.json"), optional: false);
        var secret = builder.AddParameter("secret", secret: true);
        #pragma warning disable ASPIREMONGODB001
        var shared = new SharedResources(
            builder.AddPostgres("postgres"), builder.AddRabbitMQ("rabbitmq"), builder.AddRedis("redis"), builder.AddMongoDB("mongo").WithReplicaSet("rs0"),
            builder.AddContainer("keycloak", "keycloak"), ReferenceExpression.Create($"http://keycloak"),
            secret.Resource, secret.Resource, secret.Resource, null, ResourcesOptions.Load(builder.Configuration), UseDataVolumes: false,
            DebeziumMongoPassword: secret.Resource);
        #pragma warning restore ASPIREMONGODB001

        var api = builder.AddProcureAiService<MongoServiceProject>(
            new ServiceSpec("suppliers", "suppliersdb", ["suppliers"], "suppliers", "/api/suppliers", ServiceStore.MongoDB), shared);

        var debezium = builder.Resources.OfType<ContainerResource>().Single(r => r.Name.StartsWith("debezium-", StringComparison.Ordinal));
        debezium.Name.ShouldBe("debezium-mongo");
        var migrate = builder.Resources.OfType<ProjectResource>().Single(r => r.Name == "suppliers-migrate");
        (await EnvironmentOfAsync(debezium)).ShouldContainKeyAndValue("DEBEZIUM_SOURCE_COLLECTION_INCLUDE_LIST", "suppliersdb.suppliers");
        debezium.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == migrate && w.WaitType == WaitType.WaitForCompletion);
        api.Resource.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == debezium && w.WaitType == WaitType.WaitUntilHealthy);
    }

    /// <summary>
    /// The MongoDB change data capture (plan 06 ruling 16): one Debezium Server for both captured MongoDB databases, from
    /// application-mongo.properties, with the same caps as the Postgres ones; it starts after both migrations and RabbitMQ, and the two
    /// APIs whose collections it captures wait for it.
    /// </summary>
    [Fact]
    public async Task Debezium_mongo_resource_and_caps()
    {
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var debezium = model.Resources.OfType<ContainerResource>().Single(r => r.Name == "debezium-mongo");
        var env = await EnvironmentOfAsync(debezium);

        env.ShouldContainKeyAndValue("DEBEZIUM_SOURCE_COLLECTION_INCLUDE_LIST", "suppliersdb.suppliers,assistantchatdb.conversations");
        var connection = env["DEBEZIUM_SOURCE_MONGODB_CONNECTION_STRING"].ShouldBeOfType<ReferenceExpression>().ValueExpression;
        connection.ShouldStartWith("mongodb://");
        connection.ShouldContain("//mongo:27017/");
        connection.ShouldContain("replicaSet=rs0");
        connection.ShouldContain("directConnection=true");
        connection.ShouldNotContain("tls=true", Case.Insensitive, "Debezium (Java) does not trust the developer certificate");
        // So the server must accept connections without TLS (the services still use it).
        // (Aspire keeps the mode in an internal annotation, a record that prints its value.)
        model.Resources.OfType<MongoDBServerResource>().Single().Annotations
            .Single(a => a.GetType().Name == "MongoDBServerTlsModeAnnotation").ToString()!.ShouldContain("PreferTls");
        env.ShouldContainKeyAndValue("JAVA_OPTS", "-Xmx256m -XX:TieredStopAtLevel=1 -XX:ReservedCodeCacheSize=48m -Xss512k -XX:MaxMetaspaceSize=128m");
        env.ShouldContainKey(ServiceResources.SelfHealEnvironmentVariable);
        env.ShouldContainKey("DEBEZIUM_SINK_RABBITMQ_CONNECTION_USERNAME");
        env.ShouldContainKey("DEBEZIUM_SINK_RABBITMQ_CONNECTION_PASSWORD");
        var mounts = debezium.Annotations.OfType<ContainerMountAnnotation>().ToList();
        mounts.Single(m => m.Target == "/debezium/config/application.properties").Source!.ShouldEndWith("application-mongo.properties");
        mounts.Select(m => m.Target).ShouldContain("/debezium/data");
        debezium.Annotations.OfType<HealthCheckAnnotation>().ShouldNotBeEmpty();

        var waits = debezium.Annotations.OfType<WaitAnnotation>().ToList();
        waits.ShouldContain(w => w.Resource.Name == "suppliers-migrate" && w.WaitType == WaitType.WaitForCompletion);
        waits.ShouldContain(w => w.Resource.Name == "assistant-migrate" && w.WaitType == WaitType.WaitForCompletion);
        waits.ShouldContain(w => w.Resource.Name == "rabbitmq" && w.WaitType == WaitType.WaitUntilHealthy);
        foreach (var api in new[] { "suppliers-api", "assistant-api" })
        {
            model.Resources.OfType<ProjectResource>().Single(r => r.Name == api).Annotations.OfType<WaitAnnotation>()
                .ShouldContain(w => w.Resource == debezium && w.WaitType == WaitType.WaitUntilHealthy, api);
        }

        var args = new List<object>();
        foreach (var annotation in debezium.Annotations.OfType<ContainerRuntimeArgsCallbackAnnotation>())
        {
            await annotation.Callback(new ContainerRuntimeArgsCallbackContext(args, CancellationToken.None));
        }

        var texts = args.Select(a => a.ToString()!).ToList();
        texts[texts.IndexOf("--memory") + 1].ShouldBe("512m");
        // Eight Debezium Servers at 0.35 CPU would take the whole system past 8 CPUs, so every one is capped at 0.3 (ruling 16).
        texts[texts.IndexOf("--cpus") + 1].ShouldBe("0.3");
    }

    /// <summary>
    /// Plan 07 Task 4: debezium-mongo connects as the least-privilege MongoDB user <c>debezium</c> (password parameter
    /// <c>debezium-mongo-password</c>), not as root: the connection string carries no credentials. mongo-users (infra/mongo/users.js, as root;
    /// Suppliers' migration until plan 08 Task 4) creates the user with the same password and read rights on every captured MongoDB database.
    /// </summary>
    [Fact]
    public async Task Debezium_mongo_connects_as_the_debezium_user_that_mongo_users_creates()
    {
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var env = await EnvironmentOfAsync(model.Resources.OfType<ContainerResource>().Single(r => r.Name == "debezium-mongo"));

        env.ShouldContainKeyAndValue("DEBEZIUM_SOURCE_MONGODB_USER", "debezium");
        var password = env["DEBEZIUM_SOURCE_MONGODB_PASSWORD"].ShouldBeOfType<ParameterResource>();
        password.Name.ShouldBe("debezium-mongo-password");
        password.Secret.ShouldBeTrue();
        var connection = env["DEBEZIUM_SOURCE_MONGODB_CONNECTION_STRING"].ShouldBeOfType<ReferenceExpression>().ValueExpression;
        connection.ShouldStartWith("mongodb://mongo:27017/");
        connection.ShouldNotContain("@");

        var users = await EnvironmentOfAsync(model.Resources.OfType<ContainerResource>().Single(r => r.Name == ServiceResources.MongoUsersName));
        users["DEBEZIUM_MONGO_PASSWORD"].ShouldBeSameAs(password);
        users.ShouldContainKeyAndValue("DEBEZIUM_MONGO_DATABASES", "suppliersdb,assistantchatdb");
        var suppliers = await EnvironmentOfAsync(model.Resources.OfType<ProjectResource>().Single(r => r.Name == "suppliers-migrate"));
        suppliers.Keys.ShouldNotContain(k => k.StartsWith("Mongo__Debezium", StringComparison.Ordinal), "the migration no longer creates the user");

        // Nothing else gets the password.
        foreach (var resource in model.Resources.Where(r => r.Name is not ServiceResources.MongoUsersName and not "debezium-mongo"))
        {
            (await EnvironmentOfAsync(resource)).Values.ShouldNotContain(v => ReferenceEquals(v, password), resource.Name);
        }
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
    public async Task Platform_chart_images_equal_the_AppHost_images()
    {
        // Plan 07 Task 3: the platform chart runs the AppHost's images and tags. Mongo, Redis and RabbitMQ come from Aspire's package
        // defaults (an Aspire upgrade moves them), Mailpit and Keycloak from AppHost.cs literals; the chart's Keycloak is our image
        // built FROM the same Keycloak image. Postgres is built from infra/postgres in both, so it cannot drift.
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var values = File.ReadAllLines("platform.values.yaml");

        foreach (var (resource, section) in new[]
        {
            (ResourceNames.Mongo, "mongo"), (ResourceNames.Redis, "redis"), (ResourceNames.RabbitMq, "rabbitmq"), (ResourceNames.Mailpit, "mailpit"),
        })
        {
            ChartImage(values, section).ShouldBe(AppHostImage(model, resource), $"values.yaml {section}.image");
        }

        var keycloakBase = File.ReadAllLines("keycloak.Dockerfile")
            .Where(l => l.StartsWith("FROM ", StringComparison.Ordinal))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]).Distinct().ToArray();
        keycloakBase.ShouldBe([AppHostImage(model, ResourceNames.Keycloak)], "infra/keycloak/Dockerfile FROM");

        // Plan 07 Task 4: the chart's Debezium Servers run our image built FROM the Debezium Server image every AppHost Debezium runs.
        var debeziumBase = File.ReadAllLines("debezium.Dockerfile")
            .Where(l => l.StartsWith("FROM ", StringComparison.Ordinal))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]).Distinct().ToArray();
        var appHostDebezium = model.Resources.OfType<ContainerResource>().Where(r => r.Name.StartsWith("debezium-", StringComparison.Ordinal))
            .Select(r => AppHostImage(model, r.Name)).Distinct().ToArray();
        appHostDebezium.ShouldHaveSingleItem();
        debeziumBase.ShouldBe(appHostDebezium, "infra/debezium/Dockerfile FROM (quay.io/..., a fully qualified reference)");
        ChartImage(values, "debezium").ShouldBe("procure-ai-debezium", "values.yaml debezium.image");
    }

    /// <summary>The resource's image as a fully qualified reference (docker.io and library/ made explicit).</summary>
    private static string AppHostImage(DistributedApplicationModel model, string resourceName)
    {
        var image = model.Resources.Single(r => r.Name == resourceName).Annotations.OfType<ContainerImageAnnotation>().Last();
        var reference = string.IsNullOrEmpty(image.Registry) ? image.Image : $"{image.Registry}/{image.Image}";
        // As docker reads a reference: a first segment with a dot or colon is a registry; otherwise it is Docker Hub.
        var firstSegment = reference.Split('/')[0];
        if (!reference.Contains('/') || !(firstSegment.Contains('.') || firstSegment.Contains(':')))
        {
            reference = reference.Contains('/') ? $"docker.io/{reference}" : $"docker.io/library/{reference}";
        }

        return $"{reference}:{image.Tag}";
    }

    /// <summary>`image:` of a top-level section of the chart's values.yaml (`<section>:` then `  image: <ref>`).</summary>
    private static string ChartImage(string[] values, string section)
    {
        var start = Array.IndexOf(values, $"{section}:");
        start.ShouldBeGreaterThanOrEqualTo(0, $"values.yaml has no `{section}:` section");
        var line = values.Skip(start + 1).TakeWhile(l => l.StartsWith("  ", StringComparison.Ordinal))
            .Single(l => l.TrimStart().StartsWith("image:", StringComparison.Ordinal));
        return line.Split(':', 2)[1].Split('#')[0].Trim().Trim('"');
    }

    [Fact]
    public async Task Keycloak_gets_the_dashboard_client_secret_and_no_project_gets_it()
    {
        // Plan 07 ruling 11: the realm's aspire-dashboard client takes its secret from KC_DASHBOARD_SECRET. Only the deployed dashboard
        // (Kubernetes Secret procure-keycloak, from the same user secret) uses it; no AppHost project does.
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var keycloakEnv = await EnvironmentOfAsync(model.Resources.Single(r => r.Name == "keycloak"));

        var secret = (ParameterResource)keycloakEnv["KC_DASHBOARD_SECRET"];
        secret.Name.ShouldBe("keycloak-dashboard-secret");
        secret.Secret.ShouldBeTrue();
        foreach (var resource in model.Resources.OfType<ProjectResource>())
        {
            var env = await EnvironmentOfAsync(resource);
            env.Values.ShouldNotContain(v => ReferenceEquals(v, secret), resource.Name);
        }
    }

    [Fact]
    public async Task Gateway_gets_demo_login_on_with_the_demo_client_secret_and_password_and_no_other_project_gets_them()
    {
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var keycloakEnv = await EnvironmentOfAsync(model.Resources.Single(r => r.Name == "keycloak"));
        var gateway = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "gateway");

        var gatewayEnv = await EnvironmentOfAsync(gateway);

        // Plan 06 ruling 17: the gateway's demo login uses the password grant on its own Keycloak client.
        gatewayEnv.ShouldContainKeyAndValue("DemoLogin__Enabled", "true");
        var secret = (ParameterResource)keycloakEnv["KC_DEMO_LOGIN_SECRET"];
        secret.Name.ShouldBe("keycloak-demo-login-secret");
        secret.Secret.ShouldBeTrue();
        gatewayEnv["DemoLogin__ClientSecret"].ShouldBeSameAs(secret);
        gatewayEnv["DemoLogin__Password"].ShouldBeSameAs(keycloakEnv["KC_DEMO_PASSWORD"]);
        foreach (var resource in model.Resources.OfType<ProjectResource>().Where(r => r != gateway))
        {
            var env = await EnvironmentOfAsync(resource);
            env.Values.ShouldNotContain(v => ReferenceEquals(v, secret), resource.Name);
            env.Keys.ShouldNotContain(k => k.StartsWith("DemoLogin__", StringComparison.OrdinalIgnoreCase), resource.Name);
        }
    }

    /// <summary>
    /// Plan 06 ruling 1: the demo data seeder runs `seed` once the gateway is healthy, calls the gateway with the admin API key (the key
    /// itself, a secret parameter, which no other resource gets) and accepts the local development certificate.
    /// </summary>
    [Fact]
    public async Task Demo_data_seeds_through_the_gateway_with_the_admin_key_after_the_gateway_is_healthy()
    {
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var demoData = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "demo-data");
        var gateway = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "gateway");

        var env = await EnvironmentOfAsync(demoData);
        var args = new List<object>();
        foreach (var annotation in demoData.Annotations.OfType<CommandLineArgsCallbackAnnotation>())
        {
            await annotation.Callback(new CommandLineArgsCallbackContext(args));
        }

        args.ShouldBe(["seed"]);
        env["DemoData__GatewayUrl"].ShouldBeOfType<EndpointReference>().Resource.ShouldBe(gateway);
        var key = env["DemoData__AdminApiKey"].ShouldBeOfType<ParameterResource>();
        key.Name.ShouldBe("admin-api-key");
        key.Secret.ShouldBeTrue();
        env.ShouldContainKeyAndValue("DemoData__AllowUntrustedServerCertificate", "true");
        foreach (var cap in CapResources)
        {
            env.ShouldContainKey(cap);
        }

        demoData.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == gateway && w.WaitType == WaitType.WaitUntilHealthy);
        foreach (var resource in model.Resources.OfType<ProjectResource>().Where(r => r != demoData))
        {
            (await EnvironmentOfAsync(resource)).Values.ShouldNotContain(v => ReferenceEquals(v, key), resource.Name);
        }
    }

    [Fact]
    public async Task Every_service_api_has_the_demo_reset_switched_on()
    {
        // Plan 06 ruling 4: off by default in every service and the simulators (404), on for the local AppHost.
        await using var scope = await ModelScope.BuildAsync();
        var apis = scope.Model.Resources.OfType<ProjectResource>().Where(r => r.Name.EndsWith("-api", StringComparison.Ordinal)).ToList();
        apis.Count.ShouldBe(10);
        var resettable = apis.Append(scope.Model.Resources.OfType<ProjectResource>().Single(r => r.Name == "simulators")).ToList();
        foreach (var resource in resettable)
        {
            (await EnvironmentOfAsync(resource)).ShouldContainKeyAndValue("DemoReset__Enabled", "true", resource.Name);
        }

        foreach (var other in scope.Model.Resources.OfType<ProjectResource>().Where(r => !resettable.Contains(r)))
        {
            (await EnvironmentOfAsync(other)).ShouldNotContainKey("DemoReset__Enabled", other.Name);
        }
    }

    [Fact]
    public async Task Demo_data_has_a_reset_command_and_reaches_the_simulators()
    {
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var demoData = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "demo-data");
        var simulators = model.Resources.OfType<ProjectResource>().Single(r => r.Name == "simulators");

        var env = await EnvironmentOfAsync(demoData);
        env["DemoData__SimulatorUrl"].ShouldBeOfType<EndpointReference>().Resource.ShouldBe(simulators);
        var command = demoData.Annotations.OfType<ResourceCommandAnnotation>().Single(c => c.Name == "reset-demo-data");
        command.DisplayName.ShouldBe("Reset demo data");
        command.ConfirmationMessage.ShouldNotBeNullOrWhiteSpace("a reset deletes data: the dashboard asks first");
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
        // The API starts only once Debezium is healthy, so no app change precedes the capture (no data snapshot is taken).
        api.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == debezium && w.WaitType == WaitType.WaitUntilHealthy);
        debezium.Annotations.OfType<HealthCheckAnnotation>().ShouldNotBeEmpty();
        debezium.Annotations.OfType<ContainerMountAnnotation>().Select(m => m.Target).ShouldContain("/debezium/data");
        debezium.Annotations.OfType<ContainerMountAnnotation>().Select(m => m.Target).ShouldContain("/debezium/config/application.properties");
    }

    /// <summary>
    /// Self-healing of a lost Debezium offset is off in application.properties (fail loudly, deployments must opt in) and
    /// switched on by the AppHost for local runs from <c>Debezium:SelfHealLostOffsets</c> (true in its appsettings.json).
    /// </summary>
    [Theory]
    [InlineData(null, "true")]
    [InlineData("false", "false")]
    [InlineData("true", "true")]
    public async Task Every_debezium_gets_the_self_heal_switch_from_configuration(string? configured, string expected)
    {
        await using var scope = await ModelScope.BuildAsync(configured is null ? [] : [$"Debezium:SelfHealLostOffsets={configured}"]);
        var debeziums = scope.Model.Resources.OfType<ContainerResource>().Where(r => r.Name.StartsWith("debezium-", StringComparison.Ordinal)).ToList();

        debeziums.Count.ShouldBe(8);
        foreach (var debezium in debeziums)
        {
            (await EnvironmentOfAsync(debezium)).ShouldContainKeyAndValue(ServiceResources.SelfHealEnvironmentVariable, expected, debezium.Name);
        }
    }

    [Fact]
    public void Self_heal_is_off_in_application_properties_so_deployments_must_opt_in()
    {
        var properties = File.ReadAllLines(Path.Combine(RepoRoot(), "infra", "debezium", "application.properties"));

        properties.ShouldContain("debezium.source.snapshot.mode.configuration.based.snapshot.on.data.error=false");
        properties.ShouldContain("debezium.source.snapshot.mode.configuration.based.snapshot.data=false");
        ServiceResources.SelfHealEnvironmentVariable.ShouldBe(
            "debezium.source.snapshot.mode.configuration.based.snapshot.on.data.error".ToUpperInvariant().Replace('.', '_'),
            "the environment variable must override exactly that property");
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "infra", "debezium", "application.properties")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("repository root (infra/debezium) not found");
    }

    /// <summary>
    /// Postgres's data directory (PGDATA) must really be on the named data volume. Aspire 13.6's <c>WithDataVolume()</c>
    /// mounts it at <c>/var/lib/postgresql</c> (the Postgres 18 layout), but our pg17 image keeps PGDATA at
    /// <c>/var/lib/postgresql/data</c>, which the image declares as a VOLUME: Docker put an anonymous volume there, so every
    /// new Postgres container started with an empty database while Debezium's offsets survived, and Debezium failed to start.
    /// The image's VOLUME counts as an anonymous mount unless a named volume is mounted exactly there.
    /// </summary>
    [Fact]
    public async Task Postgres_data_directory_is_on_the_named_data_volume()
    {
        const string imageVolume = "/var/lib/postgresql/data"; // VOLUME of pgvector/pgvector:pg17 (and its PGDATA default)
        await using var scope = await ModelScope.BuildAsync();
        var postgres = scope.Model.Resources.OfType<ContainerResource>().Single(r => r.Name == "postgres");
        var env = await EnvironmentOfAsync(postgres);
        var pgdata = env.TryGetValue("PGDATA", out var value) ? value.ShouldBeOfType<string>() : imageVolume;

        var mounts = postgres.Annotations.OfType<ContainerMountAnnotation>()
            .Where(m => m.Type == ContainerMountType.Volume)
            .Select(m => (m.Target, Name: m.Source))
            .ToList();
        if (!mounts.Any(m => m.Target == imageVolume))
        {
            mounts.Add((imageVolume, null));
        }

        var holder = mounts
            .Where(m => pgdata == m.Target || pgdata.StartsWith(m.Target + "/", StringComparison.Ordinal))
            .MaxBy(m => m.Target.Length);
        holder.Name.ShouldNotBeNull($"PGDATA {pgdata} is on an anonymous volume, so the database is lost with the container");
        holder.Name.ShouldEndWith("-postgres-data");
    }

    /// <summary>
    /// The pgaudit summary (plan 06 ruling 14): Postgres writes csvlog, one file per weekday, next to every earlier server argument, and
    /// Audit's migrate and api get <c>ConnectionStrings__auditlog</c> as <c>audit_log_reader</c> with the generated password parameter.
    /// </summary>
    [Fact]
    public async Task Postgres_writes_csvlog_and_audit_reads_it_as_audit_log_reader()
    {
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var postgres = model.Resources.OfType<ContainerResource>().Single(r => r.Name == "postgres");
        var args = new List<object>();
        var context = new CommandLineArgsCallbackContext(args, postgres, CancellationToken.None);
        foreach (var annotation in postgres.Annotations.OfType<CommandLineArgsCallbackAnnotation>())
        {
            await annotation.Callback(context);
        }

        var settings = args.Select(a => a.ToString()!).ToList();
        foreach (var setting in new[]
                 {
                     "wal_level=logical", "shared_preload_libraries=pgaudit", "pgaudit.log=ddl,role", "logging_collector=on",
                     "log_destination=stderr,csvlog", "log_filename=postgresql-%a.log", "log_truncate_on_rotation=on", "log_rotation_age=1d",
                 })
        {
            settings.ShouldContain(setting);
            settings[settings.IndexOf(setting) - 1].ShouldBe("-c", setting);
        }

        var password = model.Resources.OfType<ParameterResource>().Single(p => p.Name == "audit-log-reader-password");
        password.Secret.ShouldBeTrue();
        foreach (var name in new[] { "audit-migrate", "audit-api" })
        {
            var env = await EnvironmentOfAsync(model.Resources.OfType<ProjectResource>().Single(r => r.Name == name));
            var connection = env["ConnectionStrings__auditlog"].ShouldBeOfType<ReferenceExpression>();
            connection.ValueExpression.ShouldContain("Username=audit_log_reader;", Case.Sensitive, name);
            connection.ValueExpression.ShouldContain("Password={audit-log-reader-password.value};", Case.Sensitive, name);
            connection.ValueExpression.ShouldEndWith("Database=auditdb", Case.Sensitive, name);
        }

        // No other service gets the reader's connection string.
        foreach (var project in model.Resources.OfType<ProjectResource>().Where(r => !r.Name.StartsWith("audit-", StringComparison.Ordinal)))
        {
            (await EnvironmentOfAsync(project)).ShouldNotContainKey("ConnectionStrings__auditlog", project.Name);
        }
    }

    /// <summary>
    /// Debezium's offsets (the last WAL position read) must live exactly as long as the Postgres data they point into.
    /// The Postgres volume is scoped per AppHost (<c>{app}-{sha of the AppHost path}-postgres-data</c>), so every clone or
    /// worktree gets its own database. A fixed offset volume name (<c>debezium-requestsdb-data</c>) was shared by all of
    /// them: a fresh worktree's empty database met the offsets of another checkout, and Debezium refused to start
    /// ("trying to read change stream starting at LSN ... no longer available on the server"), so no api ever started.
    /// </summary>
    [Fact]
    public async Task Debezium_offset_volumes_are_scoped_to_the_apphost_like_the_postgres_volume()
    {
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var postgres = model.Resources.OfType<ContainerResource>().Single(r => r.Name == "postgres");
        var postgresVolume = postgres.Annotations.OfType<ContainerMountAnnotation>()
            .Single(m => m.Type == ContainerMountType.Volume && m.Source!.EndsWith("-postgres-data", StringComparison.Ordinal)).Source!;
        var appHostPrefix = postgresVolume[..^"postgres-data".Length];

        var debeziums = model.Resources.OfType<ContainerResource>().Where(r => r.Name.StartsWith("debezium-", StringComparison.Ordinal)).ToList();
        debeziums.ShouldNotBeEmpty();
        foreach (var debezium in debeziums)
        {
            var offsets = debezium.Annotations.OfType<ContainerMountAnnotation>().Single(m => m.Target == "/debezium/data");
            offsets.Type.ShouldBe(ContainerMountType.Volume);
            offsets.Source.ShouldBe($"{appHostPrefix}{debezium.Name}-data", $"{debezium.Name} keeps its offsets next to this AppHost's database");
        }
    }

    /// <summary>
    /// Every outgoing service client (<c>AddProcureAiServiceClient(&lt;name&gt;-api)</c>) resolves its address through Aspire service
    /// discovery, which needs a reference on the calling resource; without one the call fails (a submit answered 503). Access is the
    /// only service that calls nobody: every other service asks Access for permissions, Requests also asks Approvals for the preview.
    /// The caller also waits for what it calls, so it never starts answering before its dependencies can.
    /// </summary>
    [Theory]
    [InlineData("requests-api", new[] { "access-api", "approvals-api", "suppliers-api" })]
    [InlineData("budgets-api", new[] { "access-api" })]
    [InlineData("approvals-api", new[] { "access-api" })]
    [InlineData("audit-api", new[] { "access-api" })]
    [InlineData("suppliers-api", new[] { "access-api" })]
    [InlineData("purchasing-api", new[] { "access-api" })]
    [InlineData("invoicing-api", new[] { "access-api", "assistant-api" })]
    [InlineData("notifications-api", new[] { "access-api" })]
    // The Assistant's tools read requests, approvals, budgets and suppliers as the user (plan 05 Task 5b).
    [InlineData("assistant-api", new[] { "access-api", "requests-api", "approvals-api", "budgets-api", "suppliers-api" })]
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
            if (SetupContainers.Contains(container.Name))
            {
                // The one-shot setup containers end before any migration starts (every migration waits for their completion), and every
                // Debezium Server waits for a migration: they never run next to the Debeziums, so they are not added to the running total.
                continue;
            }

            totalMemoryMb += long.Parse(texts[memory + 1].TrimEnd('m'), System.Globalization.CultureInfo.InvariantCulture);
            totalCpus += double.Parse(texts[cpus + 1], System.Globalization.CultureInfo.InvariantCulture);
        }

        foreach (var migrate in model.Resources.OfType<ProjectResource>().Where(r => r.Name.EndsWith(ServiceResources.MigrateSuffix, StringComparison.Ordinal)))
        {
            migrate.Annotations.OfType<WaitAnnotation>()
                .ShouldContain(w => SetupContainers.Contains(w.Resource.Name) && w.WaitType == WaitType.WaitForCompletion, migrate.Name);
        }

        foreach (var debezium in containers.Where(c => c.Name.StartsWith("debezium-", StringComparison.Ordinal)))
        {
            debezium.Annotations.OfType<WaitAnnotation>()
                .ShouldContain(w => w.Resource.Name.EndsWith(ServiceResources.MigrateSuffix, StringComparison.Ordinal) && w.WaitType == WaitType.WaitForCompletion, debezium.Name);
        }

        containers.Count(c => c.Name.StartsWith("debezium-", StringComparison.Ordinal)).ShouldBe(8, "one Debezium per captured Postgres database and one for MongoDB");
        totalMemoryMb.ShouldBeLessThanOrEqualTo(8 * 1024, "container memory caps in MB");
        totalCpus.ShouldBeLessThanOrEqualTo(8.0, "container CPU caps");
    }

    private static readonly string[] SetupContainers = [ServiceResources.PostgresRolesName, ServiceResources.MongoUsersName];

    /// <summary>The services with a Postgres database (their roles come from infra/postgres/roles.sql) and whether a Debezium captures it.</summary>
    private static readonly (string Service, string Database, bool Captured)[] PostgresServices =
    [
        ("access", "accessdb", true), ("approvals", "approvalsdb", true), ("assistant", "assistantdb", true), ("audit", "auditdb", false),
        ("budgets", "budgetsdb", true), ("invoicing", "invoicingdb", true), ("purchasing", "purchasingdb", true), ("requests", "requestsdb", true),
    ];

    /// <summary>
    /// Plan 08 ruling 6: every service connects to its database as its own role (<c>&lt;service&gt;_app</c>, password parameter
    /// <c>db-&lt;service&gt;-password</c>), never as the Postgres superuser or the MongoDB root user. No project resource gets the superuser's or
    /// root's password at all (not even through Aspire's <c>&lt;DB&gt;_PASSWORD</c> reference properties): only the servers themselves and the
    /// one-shot setup containers that create the roles.
    /// </summary>
    [Fact]
    public async Task Every_service_connects_as_its_own_role()
    {
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var superPasswords = new[] { "postgres-password", "mongo-password" };
        var checkedStrings = 0;
        foreach (var project in model.Resources.OfType<ProjectResource>())
        {
            var env = await EnvironmentOfAsync(project);
            var service = project.Name.Split('-')[0];
            foreach (var (name, value) in env.Where(kv => kv.Key.StartsWith("ConnectionStrings__", StringComparison.Ordinal)))
            {
                var database = name["ConnectionStrings__".Length..];
                if (database is "rabbitmq" or "redis" or ServiceResources.AuditLogConnectionName)
                {
                    continue;
                }

                var expression = value.ShouldBeOfType<ReferenceExpression>($"{project.Name} {name}").ValueExpression;
                if (expression.StartsWith("mongodb://", StringComparison.Ordinal))
                {
                    expression.ShouldStartWith($"mongodb://{service}_app:{{db-{service}-{(database == "assistantchatdb" ? "mongo-" : "")}password.value}}@", Case.Sensitive, $"{project.Name} {name}");
                    expression.ShouldContain("authSource=admin", Case.Sensitive);
                }
                else
                {
                    expression.ShouldContain($"Username={service}_app;", Case.Sensitive, $"{project.Name} {name}");
                    expression.ShouldContain($"Password={{db-{service}-password.value}};", Case.Sensitive, $"{project.Name} {name}");
                    expression.ShouldEndWith($"Database={database}", Case.Sensitive, $"{project.Name} {name}");
                }

                expression.ShouldNotContain("Username=postgres", Case.Insensitive);
                expression.ShouldNotContain("admin:", Case.Sensitive);
                checkedStrings++;
            }

            foreach (var (name, value) in env)
            {
                foreach (var password in superPasswords)
                {
                    var text = value switch
                    {
                        IManifestExpressionProvider provider => provider.ValueExpression,
                        _ => value.ToString() ?? "",
                    };
                    text.ShouldNotContain($"{{{password}.value}}", Case.Sensitive, $"{project.Name} {name} must not carry the server's own password");
                }
            }
        }

        // 8 Postgres services and 2 MongoDB databases, each on its migrate and its api.
        checkedStrings.ShouldBe(20);
    }

    /// <summary>
    /// postgres-roles runs infra/postgres/roles.sql (bind-mounted, as the superuser from PGUSER / PGPASSWORD) once the server and every
    /// service database exist, and before every Postgres migration; it gets every role's password, each the very parameter the service or
    /// its Debezium logs in with. roles.sql names every Postgres service of the AppHost (a new service must be added there too).
    /// </summary>
    [Fact]
    public async Task Postgres_roles_runs_roles_sql_with_every_password_before_every_migration()
    {
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var roles = model.Resources.OfType<ContainerResource>().Single(r => r.Name == ServiceResources.PostgresRolesName);
        var env = await EnvironmentOfAsync(roles);
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "infra", "postgres", "roles.sql")).Replace("\r\n", "\n", StringComparison.Ordinal);

        roles.Annotations.OfType<ContainerMountAnnotation>().Select(m => m.Target).ShouldBe(
            ["/procureai/roles.sql", "/procureai/roles-database.sql", "/procureai/roles-pgaudit.sql"], ignoreOrder: true);
        roles.Annotations.OfType<ContainerMountAnnotation>().ShouldAllBe(m => m.IsReadOnly);
        env["PGPASSWORD"].ShouldBeOfType<ParameterResource>().Name.ShouldBe("postgres-password");
        env.ShouldContainKey("PGUSER");
        var args = new List<object>();
        foreach (var annotation in roles.Annotations.OfType<CommandLineArgsCallbackAnnotation>())
        {
            await annotation.Callback(new CommandLineArgsCallbackContext(args, roles, CancellationToken.None));
        }

        string.Join(' ', args.Select(a => a.ToString())).ShouldBe("-v ON_ERROR_STOP=1 -h postgres -p 5432 -d postgres -f /procureai/roles.sql");
        roles.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource.Name == "postgres" && w.WaitType == WaitType.WaitUntilHealthy);
        env["DB_AUDIT_LOG_READER_PASSWORD"].ShouldBeOfType<ParameterResource>().Name.ShouldBe("audit-log-reader-password");

        foreach (var (service, database, captured) in PostgresServices)
        {
            script.ShouldContain($"\\set service {service}\n\\set database {database}\n\\set capture {(captured ? "true" : "false")}\n", Case.Sensitive, $"roles.sql block of {service}");
            var app = env[$"DB_{service.ToUpperInvariant()}_PASSWORD"].ShouldBeOfType<ParameterResource>();
            app.Name.ShouldBe($"db-{service}-password");
            app.Secret.ShouldBeTrue();
            script.ShouldContain($"\\getenv app_password DB_{service.ToUpperInvariant()}_PASSWORD");
            roles.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource.Name == database, $"postgres-roles waits for {database}");
            var migrate = model.Resources.OfType<ProjectResource>().Single(r => r.Name == service + ServiceResources.MigrateSuffix);
            migrate.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource == roles && w.WaitType == WaitType.WaitForCompletion, migrate.Name);

            if (captured)
            {
                var cdc = env[$"DB_{service.ToUpperInvariant()}_CDC_PASSWORD"].ShouldBeOfType<ParameterResource>();
                cdc.Name.ShouldBe($"db-{service}-cdc-password");
                cdc.Secret.ShouldBeTrue();
                script.ShouldContain($"\\getenv cdc_password DB_{service.ToUpperInvariant()}_CDC_PASSWORD");
                var debezium = await EnvironmentOfAsync(model.Resources.OfType<ContainerResource>().Single(r => r.Name == "debezium-" + database));
                debezium.ShouldContainKeyAndValue("DEBEZIUM_SOURCE_DATABASE_USER", $"{service}_cdc");
                debezium["DEBEZIUM_SOURCE_DATABASE_PASSWORD"].ShouldBeSameAs(cdc, $"debezium-{database} logs in with the password roles.sql sets");
            }
            else
            {
                env.ShouldNotContainKey($"DB_{service.ToUpperInvariant()}_CDC_PASSWORD");
            }
        }

        env.Keys.Count(k => k.StartsWith("DB_", StringComparison.Ordinal)).ShouldBe(PostgresServices.Length + PostgresServices.Count(s => s.Captured) + 1);
    }

    /// <summary>
    /// mongo-users runs infra/mongo/users.js (bind-mounted, mongosh in the mongo resource's image, as root from MONGO_USER / MONGO_PASSWORD)
    /// before the MongoDB migrations, with each MongoDB user's password parameter.
    /// </summary>
    [Fact]
    public async Task Mongo_users_runs_users_js_with_every_password_before_the_mongo_migrations()
    {
        await using var scope = await ModelScope.BuildAsync();
        var model = scope.Model;
        var users = model.Resources.OfType<ContainerResource>().Single(r => r.Name == ServiceResources.MongoUsersName);
        var env = await EnvironmentOfAsync(users);

        users.Annotations.OfType<ContainerMountAnnotation>().Single().Target.ShouldBe("/procureai/users.js");
        AppHostImage(model, ServiceResources.MongoUsersName).ShouldBe(AppHostImage(model, ResourceNames.Mongo));
        env["MONGO_PASSWORD"].ShouldBeOfType<ParameterResource>().Name.ShouldBe("mongo-password");
        env.ShouldContainKey("MONGO_USER");
        env["DB_SUPPLIERS_PASSWORD"].ShouldBeOfType<ParameterResource>().Name.ShouldBe("db-suppliers-password");
        env["DB_ASSISTANT_MONGO_PASSWORD"].ShouldBeOfType<ParameterResource>().Name.ShouldBe("db-assistant-mongo-password");
        users.Annotations.OfType<WaitAnnotation>().ShouldContain(w => w.Resource.Name == ResourceNames.Mongo && w.WaitType == WaitType.WaitUntilHealthy);
        foreach (var migrate in new[] { "suppliers-migrate", "assistant-migrate" })
        {
            model.Resources.OfType<ProjectResource>().Single(r => r.Name == migrate).Annotations.OfType<WaitAnnotation>()
                .ShouldContain(w => w.Resource == users && w.WaitType == WaitType.WaitForCompletion, migrate);
        }

        var script = File.ReadAllText(Path.Combine(RepoRoot(), "infra", "mongo", "users.js"));
        script.ShouldContain("ensureUser('suppliers_app', required('DB_SUPPLIERS_PASSWORD'), [{ role: 'readWrite', db: 'suppliersdb' }]);");
        script.ShouldContain("ensureUser('assistant_app', required('DB_ASSISTANT_MONGO_PASSWORD'), [{ role: 'readWrite', db: 'assistantchatdb' }]);");
    }

    /// <summary>
    /// MongoDB 8 exits at start on Linux kernel 6.19+ (SERVER-121912; GitHub's runners) unless glibc keeps rseq: the AppHost's mongo sets
    /// the same workaround as the test fixture and the platform chart (its helm-unittest checks the chart).
    /// </summary>
    [Fact]
    public async Task Mongo_starts_on_kernel_6_19_and_newer()
    {
        await using var scope = await ModelScope.BuildAsync();
        var mongo = scope.Model.Resources.OfType<ContainerResource>().Single(r => r.Name == ResourceNames.Mongo);
        var env = await EnvironmentOfAsync(mongo);

        env["GLIBC_TUNABLES"].ShouldBe("glibc.pthread.rseq=1");
    }

    /// <summary>
    /// The chart's db-roles Job mounts its MongoDB users script from the chart (a chart cannot read files outside its folder): it must be the
    /// very infra/mongo/users.js the AppHost runs (line endings aside). Postgres needs no copy: the Job runs roles.sql from our image.
    /// </summary>
    [Fact]
    public void Chart_mongo_users_script_is_infra_mongo_users_js()
    {
        static string Read(params string[] path) =>
            File.ReadAllText(Path.Combine([RepoRoot(), .. path])).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

        Read("deploy", "charts", "procure-platform", "files", "mongo-users.js").ShouldBe(Read("infra", "mongo", "users.js"));
    }

    /// <summary>
    /// The built (never started) AppHost and its model. Must be disposed: an undisposed one still started its complete
    /// stack of containers in the background while the test process lived on (for the InfrastructureStartsTests fixture),
    /// so every test here leaked a full stack: five stacks per run, which exhausted the CI runner until the job timed out.
    /// </summary>
    internal sealed class ModelScope : IAsyncDisposable
    {
        private readonly IDistributedApplicationTestingBuilder _builder;
        private readonly DistributedApplication _app;

        private ModelScope(IDistributedApplicationTestingBuilder builder, DistributedApplication app)
        {
            _builder = builder;
            _app = app;
        }

        public DistributedApplicationModel Model => _app.Services.GetRequiredService<DistributedApplicationModel>();

        public static async Task<ModelScope> BuildAsync(string[]? args = null)
        {
            var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.ProcureAi_AppHost>(args ?? []);
            return new ModelScope(builder, await builder.BuildAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            await _builder.DisposeAsync();
        }
    }

    /// <summary>The environment variables the resource's callbacks set (secret parameters stay unresolved, so no secrets are needed).</summary>
    internal static async Task<Dictionary<string, object>> EnvironmentOfAsync(IResource resource)
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
