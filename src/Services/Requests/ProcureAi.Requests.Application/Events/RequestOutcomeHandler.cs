using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.Contracts.Approvals;
using ProcureAi.Requests.Application.Abstractions;
using ProcureAi.Requests.Domain;

namespace ProcureAi.Requests.Application.Events;

/// <summary>
/// The approval saga's outcome for a request: <c>RequestApproved</c> and <c>RequestRejected</c> (queue <c>requests-approvals</c>,
/// bound to the <c>approvals</c> exchange). The request of the event's tenant moves to Approved or Rejected, and once that has
/// committed its cached reads are evicted. A duplicate, or an outcome that lost the race with a cancel, finds the request no
/// longer submitted and changes nothing. The handler acts as <c>service:requests-api</c> for the user in the message headers
/// (the approver), which is what the data-change audit records.
/// </summary>
public static class RequestOutcomeHandler
{
    public static Task Handle(
        RequestApproved approved, IRequestsDbContext db, ICacheInvalidator cache, ILogger<RequestApproved> logger,
        CancellationToken cancellationToken) =>
        ApplyAsync(
            approved.RequestId, approved.TenantId, "approved", r => r.Approve(approved.OccurredAt), db, cache, logger, cancellationToken);

    public static Task Handle(
        RequestRejected rejected, IRequestsDbContext db, ICacheInvalidator cache, ILogger<RequestRejected> logger,
        CancellationToken cancellationToken) =>
        ApplyAsync(
            rejected.RequestId, rejected.TenantId, "rejected", r => r.Reject(rejected.Reason, rejected.OccurredAt), db, cache, logger,
            cancellationToken);

    private static async Task ApplyAsync(
        Guid requestId, string tenantId, string outcome, Action<PurchaseRequest> apply, IRequestsDbContext db,
        ICacheInvalidator cache, ILogger logger, CancellationToken cancellationToken)
    {
        // The tenant of the event, not of the caller: the query filter is bypassed and the tenant named explicitly.
        var request = await db.PurchaseRequests.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == requestId && r.TenantId == tenantId, cancellationToken);
        if (request is null)
        {
            logger.LogWarning("Outcome {Outcome} ignored for request {RequestId}: not found in tenant {TenantId}", outcome, requestId, tenantId);
            return;
        }

        if (request.Status != RequestStatus.Submitted)
        {
            // A redelivery, or the saga's outcome after the requester cancelled: the saga handles the cancel itself.
            logger.LogInformation("Outcome {Outcome} ignored for request {RequestId}: it is {Status}", outcome, requestId, request.Status);
            return;
        }

        apply(request);
        // A concurrent cancel of the same row makes this throw DbUpdateConcurrencyException: retried (Requests:ConcurrencyRetry),
        // and the retry finds the request cancelled.
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
