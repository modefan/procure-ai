using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Requests.Application.Abstractions;
using ProcureAi.Requests.Application.Mapping;
using ProcureAi.Requests.Domain;
using Wolverine;

namespace ProcureAi.Requests.Application.Commands;

public sealed record SubmitRequest(Guid Id, uint ExpectedVersion);

public static class SubmitRequestHandler
{
    /// <summary>
    /// Asks Approvals whether the request can be approved at all (an eligible approver at every level it needs); if not, the
    /// submission is refused with Approvals' reason (422), so a request never enters a saga that cannot finish. A failed or
    /// refused call (Approvals or Access down) throws and is answered 503 with <c>Retry-After</c>; the request stays a draft.
    /// Only the requester may submit, and they must act for the request's cost center (403 otherwise). Then submits the draft and publishes
    /// <c>RequestSubmitted</c>. Wolverine's EF Core transaction middleware commits the change and the outgoing message together
    /// (transactional outbox).
    /// </summary>
    public static async Task Handle(
        SubmitRequest command,
        ICallerContext caller,
        IAccessDirectory access,
        IRequestsDbContext db,
        IApprovalPreview approvals,
        IMessageBus bus,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var request = await db.LoadAsync(command.Id, command.ExpectedVersion, caller, cancellationToken);
        // The request's own rules first: a request that cannot be submitted anyway needs no call to Approvals.
        request.EnsureCanSubmit();
        await CostCenterOwnership.EnsureAsync(caller, access, request.TenantId, request.CostCenterCode, cancellationToken);
        var preview = await approvals.CheckAsync(
            request.TenantId, request.CostCenterCode, request.TotalInclVat.Amount, request.RequesterId, cancellationToken);
        if (!preview.Possible)
        {
            throw new DomainException(preview.Reason ?? "Nobody is eligible to approve this request.");
        }

        request.Submit(time.GetUtcNow());
        await db.SaveOrConflictAsync(cancellationToken);

        foreach (var submitted in request.DomainEvents.OfType<PurchaseRequestSubmitted>())
        {
            await bus.PublishAsync(RequestMapper.ToIntegrationEvent(submitted));
        }

        request.ClearDomainEvents();
    }
}
