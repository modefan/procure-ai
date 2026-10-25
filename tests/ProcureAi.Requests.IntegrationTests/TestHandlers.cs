using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.Requests.Application.Abstractions;
using ProcureAi.Requests.Application.Mapping;
using ProcureAi.Requests.Domain;
using Wolverine;

namespace ProcureAi.Requests.IntegrationTests;

/// <summary>Submits, saves and publishes like the real handler, then fails when asked: for the outbox atomicity tests.</summary>
public sealed record PublishThenMaybeFail(Guid Id, bool Fail);

public static class PublishThenMaybeFailHandler
{
    public static async Task Handle(
        PublishThenMaybeFail command, IRequestsDbContext db, IMessageBus bus, TimeProvider time, CancellationToken cancellationToken)
    {
        var request = await db.PurchaseRequests.Include(r => r.Lines).FirstAsync(r => r.Id == command.Id, cancellationToken);
        request.Submit(time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        foreach (var submitted in request.DomainEvents.OfType<PurchaseRequestSubmitted>())
        {
            await bus.PublishAsync(RequestMapper.ToIntegrationEvent(submitted));
        }

        if (command.Fail)
        {
            throw new InvalidOperationException("The handler failed after it published.");
        }
    }
}

/// <summary>Records who the handler thinks the caller is, for the header restore tests.</summary>
public sealed record WhoAmI(Guid Marker);

public static class WhoAmIHandler
{
    public static ConcurrentDictionary<Guid, (string UserId, string? TenantId)> Seen { get; } = new();

    public static void Handle(WhoAmI message, ICallerContext caller, ILogger<WhoAmI> logger)
    {
        Seen[message.Marker] = (caller.UserId, caller.TenantId);
        logger.LogWarning("WhoAmI handled {Marker}", message.Marker);
    }
}

/// <summary>Renames a request from a message, the way a later service's handler changes data (no HTTP request, no controller to evict the cache).</summary>
public sealed record RenameViaMessage(Guid Id, string Title, bool FailAfterEviction = false);

public static class RenameViaMessageHandler
{
    /// <summary>Runs after the handler asked for the eviction and before its transaction commits (a concurrent reader).</summary>
    public static Func<Task>? BeforeCommit { get; set; }

    public static async Task Handle(
        RenameViaMessage command, IRequestsDbContext db, ICallerContext caller, ICacheInvalidator cache, CancellationToken cancellationToken)
    {
        var request = await db.PurchaseRequests.FirstAsync(r => r.Id == command.Id, cancellationToken);
        request.UpdateTitle(command.Title);
        await db.SaveChangesAsync(cancellationToken);

        var tenant = CacheTag.Tenant(caller.TenantId);
        await cache.RemoveAfterCommitAsync(
        [
            CacheTag.Format("requests:{tenant}:{id}", tenant, ("id", command.Id))!,
            CacheTag.Format("requests:{tenant}:list", tenant)!,
        ]);
        if (BeforeCommit is { } concurrentReader)
        {
            await concurrentReader();
        }

        if (command.FailAfterEviction)
        {
            throw new InvalidOperationException("The handler failed after it asked for the eviction.");
        }
    }
}
