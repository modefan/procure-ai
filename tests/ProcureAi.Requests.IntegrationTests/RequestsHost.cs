using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProcureAi.Requests.Application.Abstractions;
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

    public IMessageBus Bus => Host.Services.GetRequiredService<IMessageBus>();

    public static async Task<RequestsHost> StartAsync(string postgres, string rabbit)
    {
        var caller = new TestCaller();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:requestsdb"] = postgres,
            ["ConnectionStrings:rabbitmq"] = rabbit,
        }).Build();
        var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddConfiguration(configuration))
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton(caller);
                services.AddSingleton<ICallerContext>(caller);
                services.AddRequestsInfrastructure(context.Configuration);
            })
            .UseWolverine(options => options.ConfigureRequests(configuration))
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
