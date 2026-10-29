using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Purchasing;
using ProcureAi.Invoicing.Application.Dtos;
using ProcureAi.Invoicing.Infrastructure.Erp;
using Shouldly;
using Wolverine;

namespace ProcureAi.Invoicing.IntegrationTests;

/// <summary>The Invoicing database migrated by the real <c>--migrate</c> step (no demo seed: invoices come from suppliers and finance).</summary>
public sealed class InvoicingInfraFixture()
    : ServiceInfraFixture("invoicingdb", "invoices", "invoice_lines", "payments", "payment_lines", "audit_entries")
{
    public const string ApiAssembly = "ProcureAi.Invoicing.Api.dll";

    // The test output folder holds the simulator's appsettings.json too; the Api must read its own.
    protected override Task MigrateAsync(string connectionString) =>
        ApiMigrateProcess.RunAsync(
            ApiAssembly, "invoicingdb", connectionString, RabbitConnectionString, RedisConnectionString, demoSeed: false,
            contentRoot: "src/Services/Invoicing/ProcureAi.Invoicing.Api");
}

/// <summary>One call Invoicing made to the ERP, with the ERP's answer.</summary>
public sealed record ErpCall(string? PaymentReference, int Status, JsonElement? Body);

/// <summary>Invoicing's way to the ERP in the tests: forwards every call to the in-process simulator and records it.</summary>
public sealed class ErpTraffic(Func<HttpMessageHandler> erp) : HttpMessageHandler
{
    private readonly ConcurrentQueue<ErpCall> _calls = new();

    public IReadOnlyList<ErpCall> Of(Guid paymentId) => [.. _calls.Where(c => c.PaymentReference == paymentId.ToString())];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        JsonElement? body = null;
        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var copy = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
            {
                copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            request.Content = copy;
            body = bytes.Length > 0 ? JsonDocument.Parse(bytes).RootElement.Clone() : null;
        }

        using var invoker = new HttpMessageInvoker(erp(), disposeHandler: false);
        var response = await invoker.SendAsync(request, cancellationToken);
        var reference = body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty("paymentReference", out var r) ? r.GetString() : null;
        _calls.Enqueue(new ErpCall(reference, (int)response.StatusCode, body));
        return response;
    }
}

/// <summary>The real Invoicing API on a test server, against the shared infrastructure, the stub Access and the simulator's ERP.</summary>
public sealed class InvoicingApiFactory(InvoicingInfraFixture infra, StubAccessServer access, ErpTraffic erp, List<MetricSnapshot> metrics)
    : ServiceApiFactory<Program>("invoicingdb", infra.DbConnectionString, infra.RabbitConnectionString, infra.RedisConnectionString)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        foreach (var (key, value) in access.Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.UseSetting("ErpClient:BaseUrl", "http://erp.test/");
        builder.UseSetting("ErpClient:ApiKey", SimulatorHost.SimulatorKey);
        builder.UseSetting("ErpClient:RetryDelay", "00:00:00.050");
        builder.UseSetting("ErpClient:CircuitBreaker:SamplingDuration", "00:00:10");
        builder.UseSetting("ErpClient:CircuitBreaker:BreakDuration", "00:00:01");
        builder.UseSetting("Webhooks:Sources:supplier:Secret", SimulatorHost.Secret);
        // The shipped waits are seconds to minutes; the tests wait for a few attempts.
        builder.UseSetting("Invoicing:ErpExport:InitialDelay", "00:00:00.200");
        builder.UseSetting("Invoicing:ErpExport:MaxDelay", "00:00:01");
        builder.UseSetting("Invoicing:OrderViewRetry:InitialDelay", "00:00:00.200");
        builder.UseSetting("Invoicing:OrderViewRetry:MaxDelay", "00:00:01");
        builder.UseSetting("RateLimiting:PermitLimit", "100000");

        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient(ErpSimulatorGateway.ClientName).ConfigurePrimaryHttpMessageHandler(() => erp);
            // Scheduled retries (ERP backoff, missing order views) are picked up by Wolverine's polling; poll often in the tests.
            services.ConfigureWolverine(options => options.Durability.ScheduledJobPollingTime = TimeSpan.FromMilliseconds(250));
            // A second exporter on the service's own meter provider: what reaches it is what the OTLP exporter gets.
            services.AddOpenTelemetry().WithMetrics(meters => meters.AddInMemoryExporter(metrics));
        });
    }
}

/// <summary>
/// One running Invoicing API and one simulator for the whole collection: the simulator's ERP takes the payments, its supplier
/// invoices orders it is sent (shortly after, signed, to <see cref="InvoicingWebhookUrl"/>, quoting the supplier code the
/// order named; its configured fallback code is left at the shipped value, so a forgotten code shows up as a supplier mismatch). Purchasing is played by the tests, which publish its events to the <c>purchasing</c> exchange. Tests use a tenant of their own.
/// </summary>
public sealed class InvoicingApp : IAsyncLifetime
{
    public const string InvoicingWebhookUrl = "http://invoicing.test/api/v1/invoicing/webhooks/supplier";
    public const string WebhookPath = "/api/v1/invoicing/webhooks/supplier";
    public const string Supplier = "contoso-hardware";
    public const string Finance = "fiona";
    public const string Requester = "rita";

    public static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(30);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private InvoicingApiFactory _factory = null!;

    public InvoicingInfraFixture Infra { get; } = new();

    public StubAccessServer Access { get; private set; } = null!;

    public SimulatorHost Simulator { get; private set; } = null!;

    public ErpTraffic Erp { get; private set; } = null!;

    public WebhookTraffic Webhooks { get; private set; } = null!;

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
        Simulator = new SimulatorHost(Webhooks, new Dictionary<string, string?>
        {
            ["Simulators:Webhooks:InvoicingUrl"] = InvoicingWebhookUrl,
            ["Simulators:Supplier:AutoInvoice"] = "true",
            ["Simulators:Supplier:InvoiceDelay"] = "00:00:00.300",
        });
        Erp = new ErpTraffic(() => Simulator.Server.CreateHandler());
        _factory = new InvoicingApiFactory(Infra, Access, Erp, ExportedMetrics);
        Client = _factory.CreateClient();
        Simulator.CreateClient().Dispose();
        Events = await RabbitRecorder.StartAsync(Infra.RabbitConnectionString, [Exchanges.Invoicing], []);
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

    public static string Token(string tenant, string role = "finance", string user = Finance) => TestAuth.CreateToken(user, tenant, role);

    /// <summary>Publishes an event to Purchasing's exchange through the broker, as Purchasing does, with the tenant in the headers.</summary>
    public Task PublishAsync(object message, string tenant) =>
        Bus.EndpointFor(new Uri($"rabbitmq://exchange/{Exchanges.Purchasing}"))
            .SendAsync(message, new DeliveryOptions().WithHeader(CallerHeaders.TenantId, tenant).WithHeader(CallerHeaders.UserId, "service-purchasing"))
            .AsTask();

    public static PurchaseOrderCreated NewOrder(
        string tenant, string requester = Requester, string costCenter = "CC-100", int quantity = 3, decimal price = 25_200m, string supplier = Supplier)
    {
        var excl = quantity * price;
        var vat = Math.Round(excl * 0.15m, 2, MidpointRounding.AwayFromZero);
        return new PurchaseOrderCreated(
            Guid.CreateVersion7(), Guid.NewGuid(), tenant, "PO-1001", "PR-1043", supplier, costCenter, requester,
            [new OrderLineItem(1, "LAP-14", "Laptop 14 inch", quantity, price)], excl, vat, excl + vat, DateTimeOffset.UtcNow);
    }

    /// <summary>Publishes <c>PurchaseOrderCreated</c> and waits until Invoicing has its copy of the order.</summary>
    public async Task<PurchaseOrderCreated> CreateOrderAsync(
        string tenant, string requester = Requester, string costCenter = "CC-100", int quantity = 3, decimal price = 25_200m)
    {
        var order = NewOrder(tenant, requester, costCenter, quantity, price);
        await PublishAsync(order, tenant);
        await EventuallyAsync(async () => await ScalarAsync<long>("SELECT count(*) FROM order_views WHERE id = @id", ("id", order.OrderId)) == 1, "the order view");
        return order;
    }

    /// <summary>Publishes a delivery of <paramref name="quantity"/> of line 1 and waits until the order view counts it.</summary>
    public async Task ReceiveAsync(PurchaseOrderCreated order, int quantity, bool wait = true)
    {
        var before = await ReceivedAsync(order.OrderId);
        await PublishAsync(
            new GoodsReceived(order.OrderId, order.RequestId, order.TenantId, Guid.NewGuid(), [new ReceivedLine(1, quantity)], "gary", DateTimeOffset.UtcNow),
            order.TenantId);
        if (wait)
        {
            await EventuallyAsync(async () => await ReceivedAsync(order.OrderId) == before + quantity, "the receipt on the order view");
        }
    }

    public async Task<int> ReceivedAsync(Guid orderId) =>
        await ScalarAsync<int?>("SELECT received FROM order_view_lines WHERE order_id = @id AND line_no = 1", ("id", orderId)) ?? 0;

    /// <summary>The supplier's invoice event, as the simulator sends it: line 1 at <paramref name="unitPrice"/>, totals in cents.</summary>
    public static byte[] InvoiceEvent(
        Guid orderId, string number, int quantity, decimal unitPrice = 25_200m, string? eventId = null, string supplier = Supplier, string sku = "LAP-14")
    {
        var cents = (long)(unitPrice * 100m);
        var net = quantity * cents;
        var vat = (long)Math.Round(net * 0.15m, MidpointRounding.AwayFromZero);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            eventId = eventId ?? Guid.NewGuid().ToString(),
            type = "invoice.issued",
            invoiceNumber = number,
            supplierCode = supplier,
            externalReference = orderId.ToString(),
            issueDate = "2026-10-27",
            lines = new[] { new { lineNumber = 1, articleNumber = sku, quantity, netPriceCents = cents } },
            netTotalCents = net,
            vatCents = vat,
            grossTotalCents = net + vat,
        });
    }

    /// <summary>Posts a webhook body as the supplier would, signed with the shared secret at <paramref name="at"/> (now by default).</summary>
    public Task<HttpResponseMessage> PostWebhookAsync(byte[] body, DateTimeOffset? at = null, string secret = SimulatorHost.Secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, WebhookPath) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        WebhookTestSigner.Sign(request, secret, at ?? DateTimeOffset.UtcNow);
        return Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Posts a webhook invoice and waits until it is stored; returns its ID.</summary>
    public async Task<Guid> WebhookInvoiceAsync(Guid orderId, string number, int quantity, decimal unitPrice = 25_200m)
    {
        using var response = await PostWebhookAsync(InvoiceEvent(orderId, number, quantity, unitPrice));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await InvoiceIdAsync(orderId, number))!.Value;
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

    /// <summary>The invoice as the API returns it, and its version as the If-Match tag (the read itself is tagged by its body). Asserts 200.</summary>
    public async Task<(InvoiceDto Invoice, string ETag)> GetInvoiceAsync(Guid invoiceId, string token)
    {
        using var response = await SendHttpAsync(Request(HttpMethod.Get, $"/api/v1/invoicing/invoices/{invoiceId}", token));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var invoice = (await response.Content.ReadFromJsonAsync<InvoiceDto>(Json))!;
        return (invoice, $"\"{invoice.Version}\"");
    }

    /// <summary>Pays the invoice (all payable when <paramref name="lines"/> is null) with the version given.</summary>
    public Task<HttpResponseMessage> PayAsync(Guid invoiceId, string token, string etag, object? lines = null) =>
        SendHttpAsync(Request(HttpMethod.Post, $"/api/v1/invoicing/invoices/{invoiceId}/payments", token, etag, lines is null ? null : new { lines }));

    /// <summary>Waits until the invoice has the status (the read is not cached: straight from the database).</summary>
    public Task WaitForStatusAsync(Guid invoiceId, string status) =>
        EventuallyAsync(async () => await ScalarAsync<string>("SELECT status FROM invoices WHERE id = @id", ("id", invoiceId)) == status, $"invoice status {status}");

    public async Task<Guid?> InvoiceIdAsync(Guid orderId, string number) =>
        await ScalarAsync<Guid?>("SELECT id FROM invoices WHERE order_id = @o AND supplier_invoice_number = @n", ("o", orderId), ("n", number));

    /// <summary>A single value straight from the database (no cache, no tenant filter); null when there is no row.</summary>
    public async Task<T?> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Infra.DbConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Infra.DbConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    public Task<long> DeadLettersAsync() => DeadLetters.CountAsync(Infra.DbConnectionString, Infra.RabbitConnectionString);

    /// <summary>Sets the simulated ERP's chaos mode (admin key).</summary>
    public async Task ErpChaosAsync(string mode)
    {
        using var client = Simulator.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestAuth.ApiKey);
        var latency = "00:00:00";
        using var response = await client.PutAsJsonAsync("api/v1/simulators/chaos", new
        {
            supplier = new { mode = "Normal", failureRate = 0.1, minLatency = latency, maxLatency = latency },
            erp = new { mode, failureRate = 0.1, minLatency = latency, maxLatency = latency },
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The metrics exported so far (flushed first).</summary>
    public IReadOnlyList<MetricSnapshot> Metrics()
    {
        Services.GetRequiredService<MeterProvider>().ForceFlush().ShouldBeTrue();
        lock (ExportedMetrics)
        {
            return [.. ExportedMetrics];
        }
    }

    public Task<IReadOnlyList<RecordedMessage>> WaitForAsync(Guid requestId, string kind, int count = 1) =>
        Events.WaitForAsync(requestId, kind, EventTimeout, count);

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

[CollectionDefinition(Name)]
public sealed class InvoicingCollection : ICollectionFixture<InvoicingApp>
{
    public const string Name = "invoicing-infra";
}
