using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ProcureAi.Gateway.IntegrationTests;

/// <summary>A clock the tests move by hand (token refresh depends on the time).</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// The real gateway on an in-memory test server, reached as https://localhost:5001 (the realm accepts
/// <c>https://localhost:*</c> callbacks, and the <c>__Host-</c> session cookie needs https), with Keycloak and Redis
/// from containers and a real Kestrel stub as the downstream services and web app.
/// </summary>
public sealed class GatewayHost : IAsyncDisposable
{
    public static readonly Uri BaseAddress = new("https://localhost:5001");

    private readonly TestServer _server;

    private GatewayHost(WebApplication app, StubDownstream stub, StubAccessServer access, ManualTimeProvider clock, string keyPrefix)
    {
        App = app;
        Stub = stub;
        Access = access;
        Clock = clock;
        KeyPrefix = keyPrefix;
        _server = app.GetTestServer();
    }

    public WebApplication App { get; }

    public StubDownstream Stub { get; }

    /// <summary>The Access service the gateway asks for permissions.</summary>
    public StubAccessServer Access { get; }

    public ManualTimeProvider Clock { get; }

    public string KeyPrefix { get; }

    public static async Task<GatewayHost> StartAsync(
        KeycloakFixture keycloak, RedisFixture redis, Dictionary<string, string?>? extra = null)
    {
        var stub = await StubDownstream.StartAsync();
        var access = await StubAccessServer.StartAsync();
        var clock = new ManualTimeProvider();
        var keyPrefix = $"test-{Guid.NewGuid():N}:";
        var settings = new Dictionary<string, string?>
        {
            ["Oidc:Authority"] = $"{keycloak.BaseUrl}/realms/{KeycloakFixture.Realm}",
            ["Oidc:ClientSecret"] = keycloak.GatewaySecret,
            ["Oidc:RequireHttpsMetadata"] = "false",
            ["ConnectionStrings:Redis"] = redis.ConnectionString,
            ["Session:RedisKeyPrefix"] = keyPrefix,
            ["RateLimiting:PermitLimit"] = "100000",
            ["ReverseProxy:Clusters:requests:Destinations:d1:Address"] = stub.Url,
            ["ReverseProxy:Clusters:budgets:Destinations:d1:Address"] = stub.Url,
            ["ReverseProxy:Clusters:web:Destinations:d1:Address"] = stub.Url,
        };
        foreach (var (key, value) in access.Settings)
        {
            settings[key] = value;
        }

        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

        var app = GatewayApp.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(settings);
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.AddSingleton<IStartupFilter, ClientAddressFilter>();
        });
        try
        {
            await app.StartAsync();
        }
        catch
        {
            await stub.DisposeAsync();
            await access.DisposeAsync();
            throw;
        }

        return new GatewayHost(app, stub, access, clock, keyPrefix);
    }

    /// <summary>A browser: its own cookie jar, does not follow redirects.</summary>
    public HttpClient NewBrowser() =>
        new(new CookieJarHandler(_server.CreateHandler(), stripSecure: false)) { BaseAddress = BaseAddress };

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        await Stub.DisposeAsync();
        await Access.DisposeAsync();
    }

    /// <summary>The in-memory server has no connection, so no client address; give every request one, like Kestrel would.</summary>
    private sealed class ClientAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress ??= IPAddress.Parse("203.0.113.10");
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
