using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Web.Access;
using ProcureAi.BuildingBlocks.Web.Options;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Approvals;
using ProcureAi.Contracts.Notifications;
using ProcureAi.Contracts.Requests;
using ProcureAi.Notifications.Application.Email;
using ProcureAi.Notifications.Application.Inbox;
using ProcureAi.Notifications.Application.Live;
using ProcureAi.Notifications.Application.Presence;
using ProcureAi.Notifications.Infrastructure;
using Shouldly;
using StackExchange.Redis;
using Wolverine;
using ZiggyCreatures.Caching.Fusion;
using Microsoft.Extensions.Caching.Memory;

namespace ProcureAi.Notifications.IntegrationTests;

/// <summary>
/// The error rule of the live queue (CLAUDE.md learning 2026-10-27: Wolverine 6.53 dead-letters on the first failure when no rule
/// matches). The real <c>ConfigureNotifications</c> on a host of its own (its own queue, bound to the same exchanges as the API
/// instances), with an <see cref="ILivePushes"/> that fails like the Redis backplane does. Buffered listener: the retries are inline.
/// </summary>
[Collection(NotificationsCollection.Name)]
public sealed class PushRetryTests(NotificationsApp app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_push_that_fails_on_redis_is_retried_and_the_message_is_handled()
    {
        var pushes = new FailingPushes(failures: 2, () => new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "backplane down"));
        var tenant = NewTenant();
        var before = await DeadLetters.CountAsync(null, app.Rabbit.ConnectionString, Ct);

        using var host = await StartListenerAsync(pushes);
        await app.PublishAsync(Submitted(tenant));

        await WaitUntilAsync(() => pushes.Delivered(tenant));
        pushes.Attempts(tenant).ShouldBe(3, "two failures on Redis, then the third attempt succeeded");
        (await DeadLetters.CountAsync(null, app.Rabbit.ConnectionString, Ct)).ShouldBe(before, "nothing was dead-lettered");
        await host.StopAsync(Ct);
    }

    [Fact]
    public async Task A_failure_the_rule_does_not_match_goes_to_the_error_queue_at_once()
    {
        var pushes = new FailingPushes(failures: int.MaxValue, () => new InvalidOperationException("a bug, not Redis"));
        var tenant = NewTenant();
        var before = await DeadLetters.CountAsync(null, app.Rabbit.ConnectionString, Ct);

        using var host = await StartListenerAsync(pushes);
        await app.PublishAsync(Submitted(tenant));

        await WaitUntilAsync(async () =>
            pushes.Attempts(tenant) > 0 && await DeadLetters.CountAsync(null, app.Rabbit.ConnectionString, Ct) > before);
        // Give a retry (if the rule wrongly matched) time to happen before counting attempts.
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        pushes.Attempts(tenant).ShouldBe(1, "no retry for an exception the rule does not name");
        await host.StopAsync(Ct);
    }

    [Fact]
    public async Task An_email_that_could_not_be_sent_is_retried_and_the_message_is_handled()
    {
        var pushes = new FailingPushes(failures: 0, () => new InvalidOperationException("never"));
        var notifier = new FailingNotifier(failures: 2);
        var tenant = NewTenant();
        var before = await DeadLetters.CountAsync(null, app.Rabbit.ConnectionString, Ct);

        using var host = await StartListenerAsync(pushes, notifier);
        await app.PublishAsync(new ApprovalRequested(
            Guid.NewGuid(), tenant, "PR-1", Guid.NewGuid(), 1, "Manager", ["maria"], DateTimeOffset.UtcNow.AddDays(2), DateTimeOffset.UtcNow));

        await WaitUntilAsync(() => notifier.Delivered(tenant));
        notifier.Attempts(tenant).ShouldBe(3, "two failed emails, then the third attempt sent it");
        (await DeadLetters.CountAsync(null, app.Rabbit.ConnectionString, Ct)).ShouldBe(before, "nothing was dead-lettered");
        await host.StopAsync(Ct);
    }

    private async Task<IHost> StartListenerAsync(FailingPushes pushes, IPersonalNotifier? notifier = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:rabbitmq"] = app.Rabbit.ConnectionString,
                // Fast retries for the test: three, 50 ms apart.
                [$"{NotificationsOptions.PushRetrySection}:Attempts"] = "3",
                [$"{NotificationsOptions.PushRetrySection}:InitialDelay"] = "00:00:00.050",
                [$"{NotificationsOptions.PushRetrySection}:Multiplier"] = "1",
                [$"{NotificationsOptions.PushRetrySection}:MaxDelay"] = "00:00:00.050",
            })
            .Build();
        // A queue of its own (<name>-live), so the API instances on the shared queue do not take the message.
        var service = $"retry-{Guid.NewGuid():N}"[..14];
        return await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<ILivePushes>(pushes);
                services.AddSingleton<IPersonalNotifier>(notifier ?? new FailingNotifier(failures: 0));
                // The caller model and IMessageMetadata the handlers take, as ServiceHost registers them.
                services.AddProcureAiCaller(service);
                // What the shared AccessChanged handler of UseProcureAiAccessEviction needs (never called here).
                services.AddSingleton<IFusionCache>(new FusionCache(new FusionCacheOptions(), new MemoryCache(new MemoryCacheOptions())));
                services.AddSingleton(Options.Create(new AccessClientOptions()));
                services.AddSingleton(Options.Create(new CachingOptions()));
                services.AddSingleton<AccessCacheGeneration>();
                services.AddSingleton<ICacheInvalidator, NoInvalidator>();
            })
            .UseWolverine(options => options.ConfigureNotifications(configuration, service))
            .StartAsync(Ct);
    }

    private static string NewTenant() => $"t-{Guid.NewGuid():N}"[..16];

    private static RequestSubmitted Submitted(string tenant) =>
        new(Guid.NewGuid(), tenant, "PR-1", "Laptops", "tom", "CC-100", 100m, DateTimeOffset.UtcNow, "sup-1", []);

    private static async Task WaitUntilAsync(Func<bool> condition) => await WaitUntilAsync(() => Task.FromResult(condition()));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met within 20 s.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        }
    }

    /// <summary>Fails the first <c>failures</c> pushes of each tenant with the given exception, then delivers.</summary>
    private sealed class FailingPushes(int failures, Func<Exception> failure) : ILivePushes
    {
        private readonly Dictionary<string, int> _attempts = [];
        private readonly HashSet<string> _delivered = [];
        private readonly Lock _lock = new();

        public int Attempts(string tenant)
        {
            lock (_lock)
            {
                return _attempts.GetValueOrDefault(tenant);
            }
        }

        public bool Delivered(string tenant)
        {
            lock (_lock)
            {
                return _delivered.Contains(tenant);
            }
        }

        public Task PushToTenantAsync(string tenantId, IReadOnlyList<EntityChanged> changes, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                var attempt = _attempts[tenantId] = _attempts.GetValueOrDefault(tenantId) + 1;
                if (attempt <= failures)
                {
                    throw failure();
                }

                _delivered.Add(tenantId);
            }

            return Task.CompletedTask;
        }

        public Task PushViewersAsync(string tenantId, PresenceTarget target, IReadOnlyList<Viewer> viewers, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PushOnlineAsync(string tenantId, IReadOnlyList<Viewer> online, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PushToUserAsync(string tenantId, string userId, BellItem item, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Fails the first <c>failures</c> notifications of each tenant as an email that could not be sent, then succeeds.</summary>
    private sealed class FailingNotifier(int failures) : IPersonalNotifier
    {
        private readonly Dictionary<string, int> _attempts = [];
        private readonly HashSet<string> _delivered = [];
        private readonly Lock _lock = new();

        public int Attempts(string tenant)
        {
            lock (_lock)
            {
                return _attempts.GetValueOrDefault(tenant);
            }
        }

        public bool Delivered(string tenant)
        {
            lock (_lock)
            {
                return _delivered.Contains(tenant);
            }
        }

        public Task NotifyAsync(IIntegrationEvent integrationEvent, Guid? messageId, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                var attempt = _attempts[integrationEvent.TenantId] = _attempts.GetValueOrDefault(integrationEvent.TenantId) + 1;
                if (attempt <= failures)
                {
                    throw new EmailDeliveryException("SMTP down", new IOException("connection refused"));
                }

                _delivered.Add(integrationEvent.TenantId);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class NoInvalidator : ICacheInvalidator
    {
        public Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveAfterCommitAsync(IEnumerable<string> tags) => Task.CompletedTask;
    }
}
