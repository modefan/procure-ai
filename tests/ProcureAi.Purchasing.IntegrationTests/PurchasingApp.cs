using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Messaging.Caching;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.BuildingBlocks.Web.Webhooks;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Purchasing;
using ProcureAi.Contracts.Requests;
using ProcureAi.Purchasing.Application.Dtos;
using ProcureAi.Purchasing.Infrastructure;
using ProcureAi.Purchasing.Infrastructure.Supplier;
using Shouldly;
using Wolverine;

namespace ProcureAi.Purchasing.IntegrationTests;

/// <summary>The Purchasing database migrated by the real <c>--migrate</c> step (no demo seed: orders come from approved requests).</summary>
public sealed class PurchasingInfraFixture()
    : ServiceInfraFixture("purchasingdb", "purchase_orders", "order_lines", "goods_receipts", "receipt_lines", "audit_entries")
{
    public const string ApiAssembly = "ProcureAi.Purchasing.Api.dll";

    // The test output folder holds the simulator's appsettings.json too; the Api must read its own.
    protected override Task MigrateAsync(string connectionString) =>
        ApiMigrateProcess.RunAsync(
            ApiAssembly, "purchasingdb", connectionString, RabbitConnectionString, RedisConnectionString, demoSeed: false,
            contentRoot: "src/Services/Purchasing/ProcureAi.Purchasing.Api");
}

/// <summary>The real Purchasing API on a test server, against the shared infrastructure, the stub Access and the simulator.</summary>
public sealed class PurchasingApiFactory(
    PurchasingInfraFixture infra, StubAccessServer access, SupplierTraffic supplier, TestHooks hooks, List<MetricSnapshot> metrics)
    : ServiceApiFactory<Program>("purchasingdb", infra.DbConnectionString, infra.RabbitConnectionString, infra.RedisConnectionString)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        foreach (var (key, value) in access.Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.UseSetting("SupplierClient:BaseUrl", "http://supplier.test/");
        builder.UseSetting("SupplierClient:ApiKey", SimulatorHost.SimulatorKey);
        builder.UseSetting("SupplierClient:RetryDelay", "00:00:00.050");
        builder.UseSetting("SupplierClient:CircuitBreaker:SamplingDuration", "00:00:10");
        builder.UseSetting("SupplierClient:CircuitBreaker:BreakDuration", "00:00:01");
        builder.UseSetting("Webhooks:Sources:supplier:Secret", SimulatorHost.Secret);
        // The shipped backoff is 5 s doubling to 5 min; the tests wait for a few attempts.
        builder.UseSetting("Purchasing:SupplierDelivery:InitialDelay", "00:00:00.200");
        builder.UseSetting("Purchasing:SupplierDelivery:MaxDelay", "00:00:01");
        builder.UseSetting("Purchasing:WaitingOrdersRefresh", "00:00:01");
        builder.UseSetting("RateLimiting:PermitLimit", "100000");

        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient(SupplierSimulatorGateway.ClientName).ConfigurePrimaryHttpMessageHandler(() => supplier);
            services.AddSingleton(hooks);
            // Registered by type (no factory lambdas), so Wolverine can still build the handlers' dependencies inline.
            services.Remove(services.Single(d => d.ServiceType == typeof(ITenantSequence)));
            services.AddScoped<TenantSequence<PurchasingDbContext>>();
            services.AddScoped<ITenantSequence, DelayedTenantSequence>();
            services.Remove(services.Single(d => d.ServiceType == typeof(ICacheInvalidator)));
            services.AddScoped<MessageCacheInvalidator>();
            services.AddScoped<ICacheInvalidator, HookedInvalidator>();
            // Scheduled retries (the supplier backoff) are picked up by Wolverine's polling; poll often in the tests.
            services.ConfigureWolverine(options => options.Durability.ScheduledJobPollingTime = TimeSpan.FromMilliseconds(250));
            // A second exporter on the service's own meter provider: what reaches it is what the OTLP exporter gets.
            services.AddOpenTelemetry().WithMetrics(meters => meters.AddInMemoryExporter(metrics));
        });
    }
}

/// <summary>
/// One running Purchasing API and one simulator (<see cref="SimulatorHost"/>, its webhooks to <see cref="PurchasingWebhookUrl"/>, no
/// automatic invoices: Invoicing is not part of these tests) for the whole collection, with the events Purchasing publishes and helpers to
/// send commands, call the API and post webhooks. Tests use a tenant of their own.
/// </summary>
public sealed class PurchasingApp : IAsyncLifetime
{
    public static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(30);

    public const string PurchasingWebhookUrl = "http://purchasing.test/api/v1/purchasing/webhooks/supplier";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private PurchasingApiFactory _factory = null!;

    public PurchasingInfraFixture Infra { get; } = new();

    public StubAccessServer Access { get; private set; } = null!;

    public SimulatorHost Simulator { get; private set; } = null!;

    public SupplierTraffic Supplier { get; private set; } = null!;

    public WebhookTraffic Webhooks { get; private set; } = null!;

    public TestHooks Hooks { get; } = new();

    public List<MetricSnapshot> ExportedMetrics { get; } = [];

    public HttpClient Client { get; private set; } = null!;

    public RabbitRecorder Events { get; private set; } = null!;

    public IServiceProvider Services => _factory.Services;

    public IMessageBus Bus => _factory.Services.GetRequiredService<IMessageBus>();

    public async ValueTask InitializeAsync()
    {
        await Infra.InitializeAsync();
        Access = await StubAccessServer.StartAsync();
        Webhooks = new WebhookTraffic(() => _factory.Server.CreateHandler());
        Simulator = new SimulatorHost(Webhooks, new Dictionary<string, string?> { ["Simulators:Webhooks:PurchasingUrl"] = PurchasingWebhookUrl });
        Supplier = new SupplierTraffic(() => Simulator.Server.CreateHandler());
        _factory = new PurchasingApiFactory(Infra, Access, Supplier, Hooks, ExportedMetrics);
        Client = _factory.CreateClient();
        Simulator.CreateClient().Dispose();
        Events = await RabbitRecorder.StartAsync(Infra.RabbitConnectionString, [Exchanges.Purchasing], []);
    }

    public async ValueTask DisposeAsync()
    {
        await Events.DisposeAsync();
        Client.Dispose();
        await _factory.DisposeAsync();
        await Simulator.DisposeAsync();
        await Access.DisposeAsync();
        await Infra.DisposeAsync();
    }

    public static string NewTenant() => $"t{Guid.NewGuid():N}"[..12];

    public static string Token(string tenant, string role = "requester", string user = "rita") => TestAuth.CreateToken(user, tenant, role);

    public static CreatePurchaseOrder NewOrder(
        string tenant, Guid? requestId = null, string requester = "rita", string costCenter = "CC-100", params (string Sku, int Quantity, decimal Price)[] lines) =>
        new(
            requestId ?? Guid.NewGuid(), tenant, "PR-1043", requester, costCenter, "contoso-hardware",
            lines.Length == 0
                ? [new RequestLineItem(1, "LAP-14", "Laptop 14 inch", 3, 25_200m)]
                : [.. lines.Select((l, i) => new RequestLineItem(i + 1, l.Sku, $"Item {l.Sku}", l.Quantity, l.Price))]);

    /// <summary>Sends a command to <c>purchasing-commands</c> through the broker, as the approval saga does, with the tenant in the headers.</summary>
    public Task SendAsync(object command, string tenant) =>
        Bus.EndpointFor(new Uri($"rabbitmq://queue/{Queues.PurchasingCommands}"))
            .SendAsync(command, new DeliveryOptions().WithHeader("x-tenant-id", tenant).WithHeader("x-user-id", "service-approvals")).AsTask();

    public Task<IReadOnlyList<RecordedMessage>> WaitForAsync(Guid requestId, string kind, int count = 1) =>
        Events.WaitForAsync(requestId, kind, EventTimeout, count);

    /// <summary>Creates the order through the queue and returns its ID once <c>PurchaseOrderCreated</c> was published.</summary>
    public async Task<Guid> CreateOrderAsync(CreatePurchaseOrder command)
    {
        await SendAsync(command, command.TenantId);
        var created = await WaitForAsync(command.RequestId, "PurchaseOrderCreated");
        return Guid.Parse(created.First(m => m.Kind == "PurchaseOrderCreated").Text("orderId")!);
    }

    /// <summary>Creates an order and waits until the simulator confirmed it by webhook.</summary>
    public async Task<(Guid OrderId, CreatePurchaseOrder Command)> CreateConfirmedOrderAsync(
        string tenant, string requester = "rita", string costCenter = "CC-100", params (string Sku, int Quantity, decimal Price)[] lines)
    {
        var command = NewOrder(tenant, null, requester, costCenter, lines);
        var orderId = await CreateOrderAsync(command);
        await WaitForAsync(command.RequestId, "PurchaseOrderConfirmed");
        return (orderId, command);
    }

    public static HttpRequestMessage Request(
        HttpMethod method, string url, string token, string? ifMatch = null, object? body = null, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (method == HttpMethod.Post)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    public Task<HttpResponseMessage> SendHttpAsync(HttpRequestMessage request) => Client.SendAsync(request, TestContext.Current.CancellationToken);

    /// <summary>The order as the API returns it, and its ETag. Asserts 200.</summary>
    public async Task<(PurchaseOrderDto Order, string ETag)> GetOrderAsync(Guid orderId, string token)
    {
        using var response = await SendHttpAsync(Request(HttpMethod.Get, $"/api/v1/purchasing/orders/{orderId}", token));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return ((await response.Content.ReadFromJsonAsync<PurchaseOrderDto>(Json))!, response.Headers.ETag!.Tag);
    }

    public static object Receipt(params (int LineNo, int Quantity)[] lines) =>
        new { lines = lines.Select(l => new { lineNo = l.LineNo, quantity = l.Quantity }), deliveryNote = "DN-77" };

    /// <summary>Sets the simulated supplier's chaos mode (admin key).</summary>
    public async Task ChaosAsync(string supplierMode)
    {
        using var client = Simulator.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestAuth.ApiKey);
        var latency = "00:00:00";
        using var response = await client.PutAsJsonAsync("api/v1/simulators/chaos", new
        {
            supplier = new { mode = supplierMode, failureRate = 0.1, minLatency = latency, maxLatency = latency },
            erp = new { mode = "Normal", failureRate = 0.1, minLatency = latency, maxLatency = latency },
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Posts a webhook body as the supplier would, signed with the shared secret at <paramref name="at"/> (now by default).</summary>
    public Task<HttpResponseMessage> PostWebhookAsync(byte[] body, DateTimeOffset? at = null, string secret = SimulatorHost.Secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/purchasing/webhooks/supplier") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        WebhookTestSigner.Sign(request, secret, at ?? DateTimeOffset.UtcNow);
        return Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static byte[] Acknowledgement(Guid orderId, string supplierOrderId, string? eventId = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            eventId = eventId ?? Guid.NewGuid().ToString(),
            type = "order.acknowledged",
            supplierOrderId,
            externalReference = orderId.ToString(),
            expectedDeliveryDate = "2026-11-05",
        });

    /// <summary>The order as stored, straight from the database (no cache, no tenant filter).</summary>
    public async Task<StoredOrder?> StoredAsync(Guid orderId)
    {
        await using var connection = new NpgsqlConnection(Infra.DbConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT o.status, o.supplier_attempts, o.last_supplier_error, o.supplier_reference, o.supplier_cancel_pending, o.xmin::text::bigint,
                   (SELECT count(*) FROM goods_receipts r WHERE r.order_id = o.id), o.number
            FROM purchase_orders o WHERE o.id = @id
            """,
            connection);
        command.Parameters.AddWithValue("id", orderId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new StoredOrder(
            reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetBoolean(4), reader.GetInt64(5), reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetInt32(7));
    }

    /// <summary>Every order row of a request, tombstones included.</summary>
    public async Task<long> OrderRowsAsync(string tenant, Guid requestId)
    {
        await using var connection = new NpgsqlConnection(Infra.DbConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM purchase_orders WHERE tenant_id = @t AND request_id = @r", connection);
        command.Parameters.AddWithValue("t", tenant);
        command.Parameters.AddWithValue("r", requestId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public Task<long> DeadLettersAsync() => DeadLetters.CountAsync(Infra.DbConnectionString, Infra.RabbitConnectionString);

    /// <summary>Polls until the condition holds (fails after the event timeout).</summary>
    public static async Task EventuallyAsync(Func<Task<bool>> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? EventTimeout);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    /// <summary>Lets messages in flight arrive, for asserting that something did NOT happen.</summary>
    public static Task SettleAsync() => Task.Delay(TimeSpan.FromSeconds(2));
}

public sealed record StoredOrder(
    string Status, int SupplierAttempts, string? LastSupplierError, string? SupplierReference, bool SupplierCancelPending, long Version,
    long Receipts, int? Number);

[CollectionDefinition(Name)]
public sealed class PurchasingCollection : ICollectionFixture<PurchasingApp>
{
    public const string Name = "purchasing-infra";
}
