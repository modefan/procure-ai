using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureAi.Requests.Application.Abstractions;
using ProcureAi.Requests.Application.Mapping;
using ProcureAi.Requests.Domain;
using Wolverine;
using Wolverine.Runtime;

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

    /// <summary>Which Wolverine runtime (host) handled the message (diagnostics for the log test).</summary>
    public static ConcurrentDictionary<Guid, int> HandledBy { get; } = new();

    public static void Handle(WhoAmI message, ICallerContext caller, ILogger<WhoAmI> logger, IWolverineRuntime runtime)
    {
        HandledBy[message.Marker] = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(runtime);
        Seen[message.Marker] = (caller.UserId, caller.TenantId);
        logger.LogWarning("WhoAmI handled {Marker}", message.Marker);
    }
}
