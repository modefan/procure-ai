using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.Contracts;
using ProcureAi.Notifications.Application.Handlers;
using ProcureAi.Notifications.Application.Live;
using StackExchange.Redis;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.RabbitMQ;

namespace ProcureAi.Notifications.Infrastructure;

public static class DependencyInjection
{
    /// <summary>The exchanges whose events become live updates.</summary>
    public static IReadOnlyList<string> LiveExchanges { get; } =
        [Exchanges.Requests, Exchanges.Approvals, Exchanges.Budgets, Exchanges.Purchasing, Exchanges.Invoicing];

    /// <summary>The queue of <paramref name="serviceName"/> that receives the events of <see cref="LiveExchanges"/>.</summary>
    public static string LiveQueue(string serviceName) => $"{serviceName}-live";

    /// <summary>
    /// The SignalR Redis backplane: a push from any instance reaches the clients connected to every instance. The channels are prefixed
    /// with <c>Notifications:SignalR:ChannelPrefix</c>. Uses <c>ConnectionStrings:Redis</c>, like the cache.
    /// </summary>
    public static ISignalRServerBuilder AddNotificationsBackplane(this ISignalRServerBuilder signalR, IConfiguration configuration)
    {
        var options = configuration.GetSection(NotificationsOptions.SectionName).Get<NotificationsOptions>() ?? new NotificationsOptions();
        var redis = configuration.GetConnectionString("Redis")
            ?? throw new InvalidOperationException("Connection string 'Redis' is required for the SignalR backplane.");
        return signalR.AddStackExchangeRedis(redis, redisOptions =>
        {
            redisOptions.Configuration.ChannelPrefix = RedisChannel.Literal(options.SignalR.ChannelPrefix);
            redisOptions.Configuration.AbortOnConnectFail = false;
        });
    }

    /// <summary>
    /// Wolverine without a message store (the service has no database, plan 04 ruling 1): the shared <c>AccessChanged</c> eviction queue,
    /// and one <b>buffered</b> queue <c>&lt;serviceName&gt;-live</c> bound to the five exchanges of <see cref="LiveExchanges"/>. No durable
    /// inbox: a message redelivered after a crash only pushes twice, and clients just refetch. A push that failed on Redis is retried
    /// inline (<c>Notifications:PushRetryDelays</c>), then the message goes to the error queue (Wolverine 6.53 dead-letters on the first
    /// failure when no rule matches).
    /// </summary>
    public static void ConfigureNotifications(this WolverineOptions options, IConfiguration configuration, string serviceName)
    {
        options.UseProcureAiAccessEviction(configuration, serviceName, typeof(RequestEventsHandler).Assembly);
        options.ListenToRabbitQueue(LiveQueue(serviceName), queue =>
        {
            foreach (var exchange in LiveExchanges)
            {
                queue.BindExchange(exchange);
            }
        });

        // The hub context (behind ILivePushes) is resolved from the container rather than built by generated code.
        options.CodeGeneration.AlwaysUseServiceLocationFor<ILivePushes>();

        var settings = configuration.GetSection(NotificationsOptions.SectionName).Get<NotificationsOptions>() ?? new NotificationsOptions();
        options.OnException<RedisException>().Or<RedisTimeoutException>()
            .RetryWithCooldown(settings.PushRetryDelays);
    }
}
