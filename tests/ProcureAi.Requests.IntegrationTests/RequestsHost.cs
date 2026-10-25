using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.Requests.Infrastructure;
using Wolverine;

namespace ProcureAi.Requests.IntegrationTests;

/// <summary>A Wolverine host wired the way the Requests API will be, minus HTTP.</summary>
public sealed class RequestsHost : IAsyncDisposable
{
    private RequestsHost(IHost host, TestCaller caller)
    {
        Host = host;
        Caller = caller;
    }

    public IHost Host { get; }

    public TestCaller Caller { get; }

    /// <summary>A new DI scope with its own caller, like one HTTP request.</summary>
    public (IServiceScope Scope, ScopedTestCaller Caller, RequestsDbContext Db) NewScope(string userId, string tenantId)
    {
        var scope = Host.Services.CreateScope();
        var caller = (ScopedTestCaller)scope.ServiceProvider.GetRequiredService<ICallerContext>();
        caller.UserId = userId;
        caller.TenantId = tenantId;
        return (scope, caller, scope.ServiceProvider.GetRequiredService<RequestsDbContext>());
    }

    public IMessageBus Bus => Host.MessageBus();

    /// <param name="useRealCaller">Registers the API's own <c>CallerContext</c> (user and tenant from message headers) instead of the settable test caller.</param>
    public static async Task<RequestsHost> StartAsync(
        string postgres, string rabbit, Action<WolverineOptions>? configure = null, bool useRealCaller = false)
    {
        var caller = new TestCaller();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:requestsdb"] = postgres,
            ["ConnectionStrings:rabbitmq"] = rabbit,
            ["Wolverine:AutoBuildMessageStorageOnStartup"] = "CreateOrUpdate",
        }).Build();
        var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddConfiguration(configuration))
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton(caller);
                if (useRealCaller)
                {
                    services.AddProcureAiCaller("requests-api");
                }
                else
                {
                    // Scoped, as in the API: a singleton here hid a captive-dependency bug in the audit interceptor.
                    services.AddScoped<ICallerContext, ScopedTestCaller>();
                }

                services.AddRequestsInfrastructure(context.Configuration);
            })
            .UseDefaultServiceProvider(o =>
            {
                o.ValidateScopes = true;
                o.ValidateOnBuild = true;
            })
            .UseWolverine(options =>
            {
                options.ConfigureRequests(configuration);
                configure?.Invoke(options);
            })
            .Build();
        await host.StartAsync();
        return new RequestsHost(host, caller);
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}
