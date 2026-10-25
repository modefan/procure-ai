using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Caching;
using Serilog.Core;
using Serilog.Events;
using Wolverine;

namespace ProcureAi.Requests.IntegrationTests;

/// <summary>Captures Serilog events in memory (registered in DI, so <c>ReadFrom.Services</c> picks it up).</summary>
public sealed class CapturingSink : ILogEventSink
{
    /// <summary>
    /// Every factory on the same infrastructure writes here: a locally sent message may be handled by any running
    /// instance (they share the durable inbox), so a per-factory sink would miss the handler's log event.
    /// </summary>
    public static CapturingSink Shared { get; } = new();

    private readonly List<LogEvent> _events = [];

    public IReadOnlyList<LogEvent> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public void Emit(LogEvent logEvent)
    {
        lock (_events)
        {
            _events.Add(logEvent);
        }
    }
}

/// <summary>
/// The real Requests API (its Program) on a test server, against the shared Postgres, RabbitMQ and Redis.
/// Several factories on the same infrastructure stand in for several instances of the service.
/// </summary>
public sealed class ApiFactory(InfraFixture infra, CapturingSink? sink = null) : WebApplicationFactory<Program>
{
    public const string OpenApiAuthorizationUrl = "https://login.test/tenant/oauth2/v2.0/authorize";
    public const string OpenApiTokenUrl = "https://login.test/tenant/oauth2/v2.0/token";

    /// <summary>Lets a test cancel the client request right after the write committed, while the controller invalidates the cache.</summary>
    public CancelAfterCommit Hook { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // UseSetting, not ConfigureAppConfiguration: the composition root reads some settings while it builds.
        builder.UseSetting("ConnectionStrings:requestsdb", infra.RequestsDbConnectionString);
        builder.UseSetting("ConnectionStrings:rabbitmq", infra.RabbitConnectionString);
        builder.UseSetting("ConnectionStrings:Redis", infra.RedisConnectionString);
        builder.UseSetting("Auth:Authority", TestAuth.Issuer);
        builder.UseSetting("Auth:Audience", TestAuth.Audience);
        builder.UseSetting("Auth:RequireHttpsMetadata", "false");
        builder.UseSetting("Auth:AdminApiKeySha256", TestAuth.ApiKeySha256);
        builder.UseSetting("Auth:OpenApi:AuthorizationUrl", OpenApiAuthorizationUrl);
        builder.UseSetting("Auth:OpenApi:TokenUrl", OpenApiTokenUrl);
        builder.UseSetting("Auth:OpenApi:Scopes:openid", "OpenID Connect");
        builder.UseSetting("Auth:OpenApi:Scopes:email", "Email address");
        builder.UseSetting("Wolverine:AutoBuildMessageStorageOnStartup", "CreateOrUpdate");
        builder.UseSetting("Idempotency:WaitTimeout", "00:00:20");

        builder.ConfigureTestServices(services =>
        {
            // Validate tokens against the local test key, without discovery documents or any network call.
            services.PostConfigure<JwtBearerOptions>(ApiKeyDefaults.BearerScheme, options =>
            {
                options.Authority = null;
                options.TokenValidationParameters.IssuerSigningKey = TestAuth.SigningKey;
                options.TokenValidationParameters.ValidIssuer = TestAuth.Issuer;
            });
            // Wrap the real invalidator: it is the first step after the commit, so the hook can abort the client there.
            var original = services.Single(d => d.ServiceType == typeof(ICacheInvalidator));
            services.Remove(original);
            services.AddSingleton<ICacheInvalidator>(sp => new HookedInvalidator(
                (ICacheInvalidator)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), Hook));
            services.AddSingleton<ILogEventSink>(sink ?? CapturingSink.Shared);

            // The test assembly's own message handlers (see TestHandlers.cs).
            services.ConfigureWolverine(options => options.Discovery.IncludeAssembly(typeof(ApiFactory).Assembly));
        });
    }
}

/// <summary>When armed, cancels the client request and gives the abort time to reach the server.</summary>
public sealed class CancelAfterCommit
{
    public CancellationTokenSource? Client { get; set; }
}

internal sealed class HookedInvalidator(ICacheInvalidator inner, CancelAfterCommit hook) : ICacheInvalidator
{
    public async Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        if (hook.Client is { } client)
        {
            hook.Client = null;
            await client.CancelAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None);
        }

        await inner.RemoveByTagAsync(tags, cancellationToken);
    }
}
