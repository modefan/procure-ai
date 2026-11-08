using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
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
/// from containers and a real Kestrel stub as the downstream services and web app. With <c>kestrel: true</c> the gateway
/// listens on a real https port of its own (the ASP.NET Core development certificate) instead: the in-memory server cannot
/// upgrade a request to a WebSocket for YARP to proxy.
/// </summary>
public sealed class GatewayHost : IAsyncDisposable
{
    public static readonly Uri BaseAddress = new("https://localhost:5001");

    private readonly TestServer? _server;

    private GatewayHost(WebApplication app, StubDownstream stub, StubAccessServer access, ManualTimeProvider clock, string keyPrefix, bool kestrel)
    {
        App = app;
        Stub = stub;
        Access = access;
        Clock = clock;
        KeyPrefix = keyPrefix;
        if (kestrel)
        {
            var port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
            Address = new Uri($"https://localhost:{port}");
        }
        else
        {
            _server = app.GetTestServer();
            Address = BaseAddress;
        }
    }

    /// <summary>Where the gateway is reached: <see cref="BaseAddress"/>, or the real port when it runs on Kestrel.</summary>
    public Uri Address { get; }

    public WebApplication App { get; }

    public StubDownstream Stub { get; }

    /// <summary>The Access service the gateway asks for permissions.</summary>
    public StubAccessServer Access { get; }

    public ManualTimeProvider Clock { get; }

    public string KeyPrefix { get; }

    public static async Task<GatewayHost> StartAsync(
        KeycloakFixture keycloak, RedisFixture redis, Dictionary<string, string?>? extra = null, bool kestrel = false,
        Action<IServiceCollection>? configureServices = null)
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
            // Every host its own maintenance flag (tests share one Redis): a flag one test sets never blocks another's writes.
            ["DemoReset:MaintenanceKey"] = keyPrefix + "maintenance",
            ["RateLimiting:PermitLimit"] = "100000",
            // Tests sign in and switch many times from the one test client address; DemoEntryRateLimitTests use the real limit.
            ["RateLimiting:DemoEntry:PermitLimit"] = "100000",
            ["RateLimiting:DemoEntry:GlobalPermitLimit"] = "100000",
            ["ReverseProxy:Clusters:requests:Destinations:d1:Address"] = stub.Url,
            ["ReverseProxy:Clusters:budgets:Destinations:d1:Address"] = stub.Url,
            ["ReverseProxy:Clusters:approvals:Destinations:d1:Address"] = stub.Url,
            ["ReverseProxy:Clusters:audit:Destinations:d1:Address"] = stub.Url,
            ["ReverseProxy:Clusters:suppliers:Destinations:d1:Address"] = stub.Url,
            ["ReverseProxy:Clusters:purchasing:Destinations:d1:Address"] = stub.Url,
            ["ReverseProxy:Clusters:invoicing:Destinations:d1:Address"] = stub.Url,
            ["ReverseProxy:Clusters:notifications:Destinations:d1:Address"] = stub.Url,
            ["ReverseProxy:Clusters:assistant:Destinations:d1:Address"] = stub.Url,
            ["ReverseProxy:Clusters:web:Destinations:d1:Address"] = stub.Url,
        };
        foreach (var (key, value) in access.Settings)
        {
            settings[key] = value;
        }

        // The routes to Access (the system switch) reach the stub Access too.
        settings["ReverseProxy:Clusters:access:Destinations:d1:Address"] = access.Url;

        // A null value removes a test default, so the gateway's own configuration (appsettings.json) applies.
        foreach (var (key, value) in extra ?? [])
        {
            if (value is null)
            {
                settings.Remove(key);
            }
            else
            {
                settings[key] = value;
            }
        }

        var app = GatewayApp.Build([], builder =>
        {
            if (kestrel)
            {
                builder.WebHost.UseKestrel(kestrelOptions => kestrelOptions.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps()));
            }
            else
            {
                builder.WebHost.UseTestServer();
            }

            builder.Configuration.AddInMemoryCollection(settings);
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.AddSingleton<IStartupFilter, ClientAddressFilter>();
            configureServices?.Invoke(builder.Services);
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

        return new GatewayHost(app, stub, access, clock, keyPrefix, kestrel);
    }

    /// <summary>A browser: its own cookie jar, does not follow redirects.</summary>
    public HttpClient NewBrowser() =>
        new(new CookieJarHandler(_server?.CreateHandler() ?? KestrelHandler(), stripSecure: false)) { BaseAddress = Address };

    /// <summary>A browser whose cookie jar the test can read (to send the session cookie on a WebSocket upgrade).</summary>
    public (HttpClient Browser, CookieJarHandler Cookies) NewBrowserWithCookies()
    {
        var cookies = new CookieJarHandler(_server?.CreateHandler() ?? KestrelHandler(), stripSecure: false);
        return (new HttpClient(cookies) { BaseAddress = Address }, cookies);
    }

    /// <summary>The development certificate is not necessarily trusted on the machine running the tests.</summary>
    private static HttpClientHandler KestrelHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    };

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
