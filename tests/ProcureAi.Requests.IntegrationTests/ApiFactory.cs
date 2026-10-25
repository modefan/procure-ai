using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Messaging.Caching;
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
public sealed class ApiFactory(InfraFixture infra, CapturingSink? sink = null)
    : ServiceApiFactory<Program>("requestsdb", infra.RequestsDbConnectionString, infra.RabbitConnectionString, infra.RedisConnectionString)
{
    /// <summary>Lets a test cancel the client request right after the write committed, while the controller invalidates the cache.</summary>
    public CancelAfterCommit Hook { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            // Wrap the real invalidator: it is the first step after the commit, so the hook can abort the client there.
            // Registered by type (no factory lambdas) so that Wolverine can still build message handlers' dependencies inline.
            services.Remove(services.Single(d => d.ServiceType == typeof(ICacheInvalidator)));
            services.AddSingleton(Hook);
            services.AddScoped<MessageCacheInvalidator>();
            services.AddScoped<ICacheInvalidator, HookedInvalidator>();
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

    /// <summary>Every tag that reached <c>RemoveByTagAsync</c> (what was actually evicted).</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> Evicted { get; } = new();
}

public sealed class HookedInvalidator(MessageCacheInvalidator inner, CancelAfterCommit hook) : ICacheInvalidator
{
    public async Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        tags = [.. tags];
        foreach (var tag in tags)
        {
            hook.Evicted.Enqueue(tag);
        }

        if (hook.Client is { } client)
        {
            hook.Client = null;
            await client.CancelAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None);
        }

        await inner.RemoveByTagAsync(tags, cancellationToken);
    }

    public Task RemoveAfterCommitAsync(IEnumerable<string> tags) => inner.RemoveAfterCommitAsync(tags);
}
