using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ProcureAi.SystemTests;

/// <summary>
/// The whole system (the AppHost: every service, its migration, its Debezium, the infrastructure, the gateway and the Angular
/// app), started once for all system tests. The tests call the services with the admin API key, on behalf of a demo user
/// (<c>X-On-Behalf-Of-User</c>) in tenant <c>acme</c>. The key is read from the user environment variable DEMO_ADMIN_API_KEY
/// (its SHA-256 is the AppHost user secret) and never printed. Without it the tests skip, unless REQUIRE_E2E is set.
/// </summary>
public sealed class SystemFixture : IAsyncLifetime
{
    public const string Tenant = "acme";

    /// <summary>Fixed Keycloak user IDs of the demo realm (infra/keycloak), also named by the Access seed.</summary>
    public const string Tom = "00000000-0000-4000-8000-000000000001";
    public const string Maria = "00000000-0000-4000-8000-000000000002";
    public const string Dept = "00000000-0000-4000-8000-000000000003";
    public const string Thandi = "00000000-0000-4000-8000-000000000004";

    public static readonly string[] Services = ["requests", "access", "budgets", "approvals", "audit", "suppliers", "purchasing", "invoicing"];

    /// <summary>What the receiver watches for: Purchasing's supplier circuit opened (Polly's metric, exported over OTLP).</summary>
    public const string SupplierCircuitOpened = "supplier-circuit-opened";

    /// <summary>
    /// The simulated supplier confirms an order after 1 s and invoices it 3 s after that (instead of 3 s and 20 s), so the tests see
    /// the invoice while nothing is received yet without waiting long.
    /// </summary>
    private static readonly string[] SimulatorSpeed = ["Simulators:Supplier:ConfirmDelay=00:00:01", "Simulators:Supplier:InvoiceDelay=00:00:03"];

    // A cold start pulls images, builds the Postgres image and runs npm install for the Angular app.
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(12);

    private IDistributedApplicationTestingBuilder? _builder;
    private DistributedApplication? _app;
    private string? _adminApiKey;
    private string? _simulatorApiKey;
    private string? _skipReason;

    /// <summary>The OTLP endpoint every service exports to during the run (the dashboard's stand-in).</summary>
    public OtlpReceiver Telemetry { get; private set; } = null!;

    public DistributedApplication App => _app ?? throw new InvalidOperationException("The system is not started.");

    public async ValueTask InitializeAsync()
    {
        _adminApiKey = Environment.GetEnvironmentVariable("DEMO_ADMIN_API_KEY")
                       ?? Environment.GetEnvironmentVariable("DEMO_ADMIN_API_KEY", EnvironmentVariableTarget.User);
        if (string.IsNullOrEmpty(_adminApiKey))
        {
            _skipReason = "System tests need the user environment variable DEMO_ADMIN_API_KEY (see DEMO PASSWORDS.txt). Not set, so they are skipped.";
            return;
        }

        Telemetry = await OtlpReceiver.StartAsync();
        // Polly's counter of resilience events, from Purchasing's supplier pipeline (client "supplier", handler "supplier": pipeline.name
        // "supplier-supplier"), for the event of its circuit breaker opening. Other pipelines (the Access client) do not match.
        Telemetry.WatchMetrics(
            SupplierCircuitOpened, "purchasing-api", "resilience.polly.strategy.events",
            ("pipeline.name", "supplier-supplier"), ("event.name", "OnCircuitOpened"));
        try
        {
            // Tests never keep data: no volumes, so every run starts from empty databases (and fresh Debezium offsets). The services
            // send their telemetry to the receiver (plain http, hence the unsecured-transport switch; there is no dashboard to protect).
            _builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.ProcureAi_AppHost>(
            [
                "Persistence:UseDataVolumes=false",
                .. SimulatorSpeed,
                $"ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL={Telemetry.Endpoint.GetLeftPart(UriPartial.Authority)}",
                "ASPIRE_ALLOW_UNSECURED_TRANSPORT=true",
            ]);
            // The key the services call the simulators with (AppHost user secret); the tests read the ERP's postings with it.
            _simulatorApiKey = _builder.Configuration["Parameters:simulator-api-key"];
            _app = await _builder.BuildAsync();
            await _app.StartAsync();
            using var cts = new CancellationTokenSource(StartupTimeout);
            // The gateway waits for every API, and every API for its migration, Debezium and the services it calls.
            await _app.ResourceNotifications.WaitForResourceHealthyAsync("gateway", cts.Token);
        }
        catch
        {
            // xUnit does not reliably call DisposeAsync after a failed InitializeAsync: never leave a started stack behind.
            await DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
            _app = null;
        }

        if (_builder is not null)
        {
            await _builder.DisposeAsync();
            _builder = null;
        }

        if (Telemetry is not null)
        {
            await Telemetry.DisposeAsync();
            Telemetry = null!;
        }
    }

    /// <summary>Skips the test when the key is missing, unless REQUIRE_E2E=1/true (CI), where that is a failure.</summary>
    public void RequireReady()
    {
        if (_skipReason is null)
        {
            return;
        }

        var require = Environment.GetEnvironmentVariable("REQUIRE_E2E");
        if (require is "1" || string.Equals(require, "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("REQUIRE_E2E is set but the system test prerequisites are missing. " + _skipReason);
        }

        Assert.Skip(_skipReason);
    }

    public DistributedApplicationModel Model => App.Services.GetRequiredService<DistributedApplicationModel>();

    /// <summary>A client of one service's own endpoint (not through the gateway).</summary>
    public ServiceClient Service(string service, string? onBehalfOf, string? tenant = Tenant) =>
        new(App.CreateHttpClient(service + "-api", "http"), _adminApiKey!, onBehalfOf, tenant);

    /// <summary>A client of the simulators' own endpoints (chaos switch, supplier hooks) with the admin key.</summary>
    public ServiceClient Simulators() => new(App.CreateHttpClient("simulators", "http"), _adminApiKey!, onBehalfOf: null, tenant: null);

    /// <summary>A client of the simulated ERP with the key the services use (<c>X-Api-Key</c> = the simulator key).</summary>
    public ServiceClient Erp() =>
        new(App.CreateHttpClient("simulators", "http"),
            _simulatorApiKey ?? throw new InvalidOperationException("The AppHost user secret Parameters:simulator-api-key is missing."),
            onBehalfOf: null, tenant: null);

    /// <summary>The admin key, for a raw call (through the gateway).</summary>
    public string AdminApiKey => _adminApiKey!;

    /// <summary>A client of the gateway (https, ASP.NET Core dev certificate).</summary>
    public HttpClient Gateway()
    {
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
        return new HttpClient(handler) { BaseAddress = App.GetEndpoint("gateway", "https") };
    }
}

/// <summary>Calls one service with the admin key, on behalf of a user in a tenant; every POST gets its own Idempotency-Key.</summary>
public sealed class ServiceClient(HttpClient http, string apiKey, string? onBehalfOf, string? tenant) : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, string? ifMatch = null)
    {
        using var message = new HttpRequestMessage(method, path);
        message.Headers.Add("X-Api-Key", apiKey);
        if (onBehalfOf is not null)
        {
            message.Headers.Add("X-On-Behalf-Of-User", onBehalfOf);
        }

        if (tenant is not null)
        {
            message.Headers.Add("X-Tenant-Id", tenant);
        }

        if (method == HttpMethod.Post)
        {
            message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        if (ifMatch is not null)
        {
            message.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (body is not null)
        {
            message.Content = JsonContent.Create(body, options: Json);
        }

        return await http.SendAsync(message);
    }

    /// <summary>Sends and reads the JSON answer; fails with the status and body when the status is not the expected one.</summary>
    public async Task<(T Body, string? ETag)> SendAsync<T>(
        HttpMethod method, string path, System.Net.HttpStatusCode expected, object? body = null, string? ifMatch = null)
    {
        using var response = await SendAsync(method, path, body, ifMatch);
        var text = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != expected)
        {
            throw new HttpStatusException(method, path, response.StatusCode, $"expected {(int)expected}, got {(int)response.StatusCode}: {text}");
        }

        return (JsonSerializer.Deserialize<T>(text, Json)!, response.Headers.ETag?.ToString());
    }

    public Task<(T Body, string? ETag)> GetAsync<T>(string path) => SendAsync<T>(HttpMethod.Get, path, System.Net.HttpStatusCode.OK);

    public void Dispose() => http.Dispose();
}

/// <summary>An unexpected HTTP status from a service; polling retries the transient ones (502, 503, 504) until its deadline.</summary>
public sealed class HttpStatusException(HttpMethod method, string path, System.Net.HttpStatusCode status, string detail)
    : Xunit.Sdk.XunitException($"{method} {path}: {detail}")
{
    public System.Net.HttpStatusCode Status { get; } = status;

    public bool IsTransient => Status is System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.GatewayTimeout;
}

[CollectionDefinition(Name)]
public sealed class SystemCollection : ICollectionFixture<SystemFixture>
{
    public const string Name = "system";
}
