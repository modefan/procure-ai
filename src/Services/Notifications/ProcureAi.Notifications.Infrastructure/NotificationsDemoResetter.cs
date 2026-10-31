using Microsoft.Extensions.Logging;
using ProcureAi.BuildingBlocks.Web.Demo;
using ProcureAi.Notifications.Infrastructure.Inbox;
using StackExchange.Redis;

namespace ProcureAi.Notifications.Infrastructure;

/// <summary>
/// The Notifications part of the nightly demo reset: every bell item of every user (Redis), so nobody is told about a request that no
/// longer exists. Presence and the email deduplication keys expire by themselves. No database and no message store.
/// </summary>
public sealed class NotificationsDemoResetter(IConnectionMultiplexer redis, ILogger<NotificationsDemoResetter> logger) : IDemoResetter
{
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        var deleted = await RedisDemoReset.DeleteKeysAsync(redis, RedisBellStore.KeyPattern);
        logger.LogInformation("Demo reset deleted {Count} bell keys", deleted);
    }
}
