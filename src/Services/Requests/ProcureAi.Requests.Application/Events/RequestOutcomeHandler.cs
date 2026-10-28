using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts.Approvals;
using ProcureAi.Requests.Application.Abstractions;
using ProcureAi.Requests.Domain;

namespace ProcureAi.Requests.Application.Events;

/// <summary>
/// The approval saga's outcomes for a request (queue <c>requests-approvals</c>, bound to the <c>approvals</c> exchange):
/// approved, rejected, ordered, completed, and the cancellation confirmed or refused. The request of the event's tenant moves
/// on, and once that has committed its cached reads are evicted. A message that finds the request in a status it does not apply
/// to (a duplicate, an outcome that lost the race with a cancel, a message that arrived out of order) changes nothing and is
/// logged. The handler acts as <c>service:requests-api</c> for the user in the message headers, which is what the data-change
/// audit records.
/// </summary>
public static class RequestOutcomeHandler
{
    private static readonly RequestStatus[] Submitted = [RequestStatus.Submitted];

    // A supplier rejection (the saga's PurchaseOrderRejected) reaches a request that was already approved or ordered.
    private static readonly RequestStatus[] Rejectable = [RequestStatus.Submitted, RequestStatus.Approved, RequestStatus.Ordered];

    // Completed too: RequestOrdered may arrive after RequestCompleted, and then only fills in the order.
    private static readonly RequestStatus[] Orderable =
        [RequestStatus.Approved, RequestStatus.Ordered, RequestStatus.Completed, RequestStatus.Cancelling];

    // Approved: RequestCompleted overtook RequestOrdered. Cancelling: the settlement overtook the cancel.
    private static readonly RequestStatus[] Completable = [RequestStatus.Ordered, RequestStatus.Approved, RequestStatus.Cancelling];

    private static readonly RequestStatus[] Cancelling = [RequestStatus.Cancelling, RequestStatus.Cancelled];

    public static Task Handle(
        RequestApproved approved, IRequestsDbContext db, ICacheInvalidator cache, ILogger<RequestApproved> logger,
        CancellationToken cancellationToken) =>
        ApplyAsync(
            approved.RequestId, approved.TenantId, "approved", Submitted, r => r.Approve(approved.OccurredAt), db, cache, logger,
            cancellationToken);

    public static Task Handle(
        RequestRejected rejected, IRequestsDbContext db, ICacheInvalidator cache, ILogger<RequestRejected> logger,
        CancellationToken cancellationToken) =>
        ApplyAsync(
            rejected.RequestId, rejected.TenantId, "rejected", Rejectable, r => r.Reject(rejected.Reason, rejected.OccurredAt), db,
            cache, logger, cancellationToken);

    public static Task Handle(
        RequestOrdered ordered, IRequestsDbContext db, ICacheInvalidator cache, ILogger<RequestOrdered> logger,
        CancellationToken cancellationToken) =>
        ApplyAsync(
            ordered.RequestId, ordered.TenantId, "ordered", Orderable,
            r => r.MarkOrdered(ordered.OrderId, ordered.OrderNumber, ordered.OccurredAt), db, cache, logger, cancellationToken);

    public static Task Handle(
        RequestCompleted completed, IRequestsDbContext db, ICacheInvalidator cache, ILogger<RequestCompleted> logger,
        CancellationToken cancellationToken) =>
        ApplyAsync(
            completed.RequestId, completed.TenantId, "completed", Completable,
            r => r.Complete(Money.Zar(completed.SpentInclVat), completed.OccurredAt), db, cache, logger, cancellationToken);

    public static Task Handle(
        RequestCancellationConfirmed confirmed, IRequestsDbContext db, ICacheInvalidator cache,
        ILogger<RequestCancellationConfirmed> logger, CancellationToken cancellationToken) =>
        ApplyAsync(
            confirmed.RequestId, confirmed.TenantId, "cancellation confirmed", Cancelling,
            r => r.ConfirmCancellation(confirmed.OccurredAt), db, cache, logger, cancellationToken);

    public static Task Handle(
        RequestCancellationRefused refused, IRequestsDbContext db, ICacheInvalidator cache,
        ILogger<RequestCancellationRefused> logger, CancellationToken cancellationToken) =>
        ApplyAsync(
            refused.RequestId, refused.TenantId, "cancellation refused", Cancelling,
            r => r.RefuseCancellation(refused.Reason, refused.OccurredAt), db, cache, logger, cancellationToken);

    private static async Task ApplyAsync(
        Guid requestId, string tenantId, string outcome, RequestStatus[] applicableTo, Action<PurchaseRequest> apply,
        IRequestsDbContext db, ICacheInvalidator cache, ILogger logger, CancellationToken cancellationToken)
    {
        // The tenant of the event, not of the caller: the query filter is bypassed and the tenant named explicitly.
        var request = await db.PurchaseRequests.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == requestId && r.TenantId == tenantId, cancellationToken);
        if (request is null)
        {
            logger.LogWarning("Outcome {Outcome} ignored for request {RequestId}: not found in tenant {TenantId}", outcome, requestId, tenantId);
            return;
        }

        if (!applicableTo.Contains(request.Status))
        {
            // A redelivery, or the saga's outcome after the requester cancelled or the request moved on: nothing to do.
            logger.LogInformation("Outcome {Outcome} ignored for request {RequestId}: it is {Status}", outcome, requestId, request.Status);
            return;
        }

        apply(request);
        // A concurrent change of the same row makes this throw DbUpdateConcurrencyException: retried (Requests:ConcurrencyRetry),
        // and the retry sees the winner's state.
        await db.SaveChangesAsync(cancellationToken);
        await cache.RemoveAfterCommitAsync(RequestCacheTag.Of(tenantId, requestId));
        logger.LogInformation("Outcome {Outcome} applied to request {RequestId}", outcome, requestId);
    }
}

/// <summary>
/// Events of the <c>approvals</c> exchange that reach the Requests queue (every event of an exchange does) but need nothing
/// here. Handled explicitly so they are consumed, not reported as messages without a handler.
/// </summary>
public static class IgnoredApprovalEventsHandler
{
    public static void Handle(ApprovalRequested message)
    {
    }

    public static void Handle(ApprovalStepApproved message)
    {
    }

    public static void Handle(ApprovalReminderDue message)
    {
    }

    public static void Handle(ApprovalEscalated message)
    {
    }

    public static void Handle(ApprovalAlertRaised message)
    {
    }
}
