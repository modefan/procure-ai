using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace ProcureAi.BuildingBlocks.Web.Http;

/// <summary>
/// Retries and timeouts of one service client, from its own configuration section (e.g. <c>AccessClient</c>,
/// <c>ApprovalsClient</c>). The circuit breaker and the rest of the standard pipeline keep Microsoft's defaults.
/// </summary>
public class ServiceClientResilienceOptions
{
    /// <summary>Retries of a failed call, on top of the first attempt (the resilience pipeline needs at least one).</summary>
    [Range(1, 5)]
    public int RetryCount { get; set; } = 1;

    [Range(typeof(TimeSpan), "00:00:00.010", "00:00:30")]
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>One attempt gives up after this long.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:01:00")]
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>The whole call, retries included, gives up after this long.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:05:00")]
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(8);
}

public static class ServiceClientResilience
{
    /// <summary>
    /// Replaces the one shared standard resilience pipeline the service defaults give every client (retries of seconds, 30 s
    /// timeouts) with a standard pipeline of the client's own, whose retries and timeouts come from <typeparamref name="TOptions"/>
    /// (registered and validated by the caller). For calls that sit in front of a user's request and must fail fast.
    /// </summary>
    public static IHttpClientBuilder WithResilienceFrom<TOptions>(this IHttpClientBuilder builder)
        where TOptions : ServiceClientResilienceOptions
    {
        // RemoveAllResilienceHandlers is marked experimental by Microsoft; it is the supported way to replace the defaults' pipeline.
#pragma warning disable EXTEXP0001
        var pipeline = builder.RemoveAllResilienceHandlers().AddStandardResilienceHandler();
#pragma warning restore EXTEXP0001
        builder.Services.AddOptions<HttpStandardResilienceOptions>(pipeline.PipelineName)
            .Configure<IOptions<TOptions>>((resilience, client) =>
            {
                var settings = client.Value;
                resilience.Retry.MaxRetryAttempts = settings.RetryCount;
                resilience.Retry.Delay = settings.RetryDelay;
                resilience.AttemptTimeout.Timeout = settings.AttemptTimeout;
                resilience.TotalRequestTimeout.Timeout = settings.TotalTimeout;
            });
        return builder;
    }
}
