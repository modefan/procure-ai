using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.BuildingBlocks.Web.Auth;
using Serilog.Core;
using Serilog.Events;
using Wolverine;

namespace ProcureAi.Requests.IntegrationTests;

/// <summary>Captures Serilog events in memory (registered in DI, so <c>ReadFrom.Services</c> picks it up).</summary>
public sealed class CapturingSink : ILogEventSink
{
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
            if (sink is not null)
            {
                services.AddSingleton<ILogEventSink>(sink);
            }

            // The test assembly's own message handlers (see TestHandlers.cs).
            services.ConfigureWolverine(options => options.Discovery.IncludeAssembly(typeof(ApiFactory).Assembly));
        });
    }
}
