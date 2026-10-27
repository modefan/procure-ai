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

    public static readonly string[] Services = ["requests", "access", "budgets", "approvals", "audit"];

    // A cold start pulls images, builds the Postgres image and runs npm install for the Angular app.
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(12);

    private IDistributedApplicationTestingBuilder? _builder;
    private DistributedApplication? _app;
    private string? _adminApiKey;
    private string? _skipReason;

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

        // Tests never keep data: no volumes, so every run starts from empty databases (and fresh Debezium offsets).
        _builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.ProcureAi_AppHost>(["Persistence:UseDataVolumes=false"]);
        try
        {
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
