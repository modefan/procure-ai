using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.Contracts;
using ProcureAi.Notifications.Application.Email;
using ProcureAi.Notifications.Application.Handlers;
using ProcureAi.Notifications.Application.Inbox;
using ProcureAi.Notifications.Application.Live;
using ProcureAi.Notifications.Application.Presence;
using ProcureAi.Notifications.Infrastructure.Email;
using ProcureAi.Notifications.Infrastructure.Inbox;
using ProcureAi.Notifications.Infrastructure.Presence;
using StackExchange.Redis;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.RabbitMQ;

namespace ProcureAi.Notifications.Infrastructure;

public static class DependencyInjection
{
    /// <summary>The exchanges whose events become live updates.</summary>
    public static IReadOnlyList<string> LiveExchanges { get; } =
        [Exchanges.Requests, Exchanges.Approvals, Exchanges.Budgets, Exchanges.Purchasing, Exchanges.Invoicing, Exchanges.Assistant];

    /// <summary>The queue of <paramref name="serviceName"/> that receives the events of <see cref="LiveExchanges"/>.</summary>
    public static string LiveQueue(string serviceName) => $"{serviceName}-live";

    /// <summary>
    /// Presence (Redis store, the service and the sweeper on every instance), the bell (Redis), email (MailKit SMTP, deduplicated in
    /// Redis) and the notifier that ties bell and email to the events. Uses the <see cref="IConnectionMultiplexer"/> and the
    /// DistributedLock provider the shared caching setup registers. The presence clock is a keyed <see cref="TimeProvider"/>
    /// (<see cref="PresenceClock.Key"/>), the system clock unless a test registers another first.
    /// </summary>
    public static IServiceCollection AddNotificationsInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddKeyedSingleton(PresenceClock.Key, TimeProvider.System);
        services.AddSingleton<IPresenceStore, RedisPresenceStore>();
        services.AddSingleton<PresenceService>();
        services.AddSingleton<PresenceSweeper>();
        services.AddHostedService(provider => provider.GetRequiredService<PresenceSweeper>());

        services.AddSingleton<IBellStore, RedisBellStore>();
        services.AddSingleton<BellInbox>();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddSingleton<IEmailOnce, RedisEmailOnce>();
        services.AddScoped<IPersonalNotifier, PersonalNotifier>();
        return services;
    }

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
    /// and one <b>buffered</b> queue <c>&lt;serviceName&gt;-live</c> bound to the six exchanges of <see cref="LiveExchanges"/>. No durable
    /// inbox: a message redelivered after a crash only pushes twice, and clients just refetch. A push that failed on Redis is retried
    /// inline (<c>Notifications:PushRetry</c>, <see cref="RetryPushFailures"/>), then the message goes to the error queue (Wolverine 6.53 dead-letters on the first
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

        // The hub context (behind ILivePushes) is resolved from the container rather than built by generated code; so is the notifier,
        // which depends on it and on the Access client.
        options.CodeGeneration.AlwaysUseServiceLocationFor<ILivePushes>();
        options.CodeGeneration.AlwaysUseServiceLocationFor<IPersonalNotifier>();

        var delays = NotificationsOptions.PushRetryDelays(configuration);
        options.RetryPushFailures(delays);
        options.RetryEmailFailures(delays);
    }

    /// <summary>
    /// An email that could not be sent (<see cref="EmailDeliveryException"/>: the SMTP server or the address lookup failed) is retried
    /// inline after each of <paramref name="delays"/> (<c>Notifications:PushRetry</c>), then the message goes to the error queue. A retry
    /// adds no second bell item (same ID) and sends only the emails that were not sent (dedup key released on failure).
    /// </summary>
    public static WolverineOptions RetryEmailFailures(this WolverineOptions options, TimeSpan[] delays)
    {
        options.OnException<EmailDeliveryException>().RetryWithCooldown(delays);
        return options;
    }

    /// <summary>
    /// A push that failed on Redis (the backplane: <see cref="RedisException"/>, including <see cref="RedisConnectionException"/>, or
    /// <see cref="RedisTimeoutException"/>) is retried inline after each of <paramref name="delays"/>, then moved to the error queue.
    /// Without the rule Wolverine 6.53 dead-letters on the first failure. Any other exception still goes to the error queue at once.
    /// </summary>
    public static WolverineOptions RetryPushFailures(this WolverineOptions options, TimeSpan[] delays)
    {
        options.OnException<RedisException>().Or<RedisTimeoutException>()
            .RetryWithCooldown(delays);
        return options;
    }
}
