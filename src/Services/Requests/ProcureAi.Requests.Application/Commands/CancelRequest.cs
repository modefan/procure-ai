using Microsoft.EntityFrameworkCore;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.Requests.Application.Abstractions;
using ProcureAi.Requests.Application.Dtos;
using ProcureAi.Requests.Application.Mapping;
using ProcureAi.Requests.Domain;
using Wolverine;

namespace ProcureAi.Requests.Application.Commands;

/// <summary>The requester cancels their request. <c>ExpectedVersion</c> is the version the caller last saw.</summary>
public sealed record CancelRequest(Guid Id, uint ExpectedVersion, string? Reason);

public static class CancelRequestHandler
{
    /// <summary>
    /// Only the requester may cancel (for the admin key: the user it acts on behalf of); anyone else gets 403, decided before
    /// the version is checked, so a stale <c>If-Match</c> does not hide the refusal. A submitted or approved request publishes
    /// <c>RequestCancelled</c> (the approval saga stops and has the budget released, once); a draft is cancelled silently.
    /// The change and the message commit together (transactional outbox).
    /// </summary>
    public static async Task<RequestDto> Handle(
        CancelRequest command,
        IRequestsDbContext db,
        ICallerContext caller,
        IMessageBus bus,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var request = await db.PurchaseRequests.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken)
            ?? throw new NotFoundException($"Request {command.Id} was not found.");
        if (!string.Equals(request.RequesterId, caller.UserId, StringComparison.Ordinal))
        {
            throw new ForbiddenException("Only the requester can cancel a request.");
        }

        OptimisticConcurrency.EnsureVersion($"Request {command.Id}", request.Version, command.ExpectedVersion);
        request.Cancel(caller.UserId, command.Reason, time.GetUtcNow());
        await db.SaveOrConflictAsync(cancellationToken);

        foreach (var cancelled in request.DomainEvents.OfType<PurchaseRequestCancelled>())
        {
            await bus.PublishAsync(RequestMapper.ToIntegrationEvent(cancelled));
        }

        request.ClearDomainEvents();
        return RequestMapper.ToDto(request);
    }
}
