using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcureAi.Approvals.Application.Abstractions;
using ProcureAi.Approvals.Application.Mapping;
using ProcureAi.Approvals.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts.Approvals;
using ProcureAi.Contracts.Budgets;
using ProcureAi.Contracts.Invoicing;
using ProcureAi.Contracts.Purchasing;
using ProcureAi.Contracts.Requests;
using Wolverine;
using Wolverine.Persistence.Sagas;

namespace ProcureAi.Approvals.Application.Sagas;

public enum ApprovalState
{
    AwaitingBudget,
    AwaitingApproval,

    /// <summary>Every step approved and the purchase order requested from Purchasing (not created yet).</summary>
    Approved,

    /// <summary>Purchasing created the order (<c>RequestOrdered</c>); the saga waits for payment and settlement.</summary>
    Ordered,

    /// <summary>A cancellation was sent to Purchasing, which alone decides whether it succeeds; its answer ends or restores the saga.</summary>
    Cancelling,

    /// <summary>Every order line is paid or closed short (<c>PurchaseOrderSettled</c>). Never stored: the saga ends here.</summary>
    Completed,
    Rejected,
    Cancelled,
}

/// <summary>
/// The approval of one purchase request, from submission to approval, rejection or cancellation. Its ID is the request's ID.
/// Wolverine persists it with EF Core in the Approvals database, in one transaction with the approval steps and the outbox,
/// so a state change and the messages it sends commit together. Rejection and cancellation complete (delete) it; a message
/// for a completed or unknown saga goes to the <c>NotFound</c> handlers. The exception is a cancellation that finds no saga:
/// it leaves a cancelled marker (a saga in state Cancelled that is never completed), so a submission arriving after it
/// reserves nothing.
/// <para>
/// Flow: <see cref="RequestSubmitted"/> asks Budgets to reserve; <see cref="BudgetReserved"/> resolves the chain from the Access
/// service's view of the cost center and opens step 1; each <see cref="ApproveStep"/> opens the next step until the last, which
/// approves the request. A reject, a refused reservation or a cancellation ends it, and every end after a reservation releases
/// the budget. Each open step has a reminder and an escalation timeout, scheduled durably.
/// </para>
/// <para>
/// Every handler that changes what the API shows (the steps, or the saga's state) evicts the cached inbox of the tenant and the
/// cached chain of the request after its commit (<see cref="ICacheInvalidator.RemoveAfterCommitAsync"/>, tags in
/// <see cref="ApprovalCacheTag"/>). A reminder changes neither: its count lives here, on the saga, not on the step, so it does
/// not change the step's version (the ETag an approver holds).
/// </para>
/// </summary>
public sealed class ApprovalSaga : Saga
{
    public const string NoEligibleApprover = "No eligible approver";

    /// <summary>Sent to Purchasing when the request was cancelled without a reason (Purchasing refuses a blank one).</summary>
    public const string DefaultCancelReason = "Request cancelled";

    /// <summary>The rejection reason when an approved request has nothing to order (no lines or no supplier).</summary>
    public const string NoCatalogLines = "Request has no catalog lines";

    /// <summary>The refusal reason when the order settled (all paid) while a cancellation was pending.</summary>
    public const string OrderSettled = "The order was settled";

    /// <summary>The rejection reason when the supplier rejects the order.</summary>
    public static string SupplierRejected(string reason) => $"Supplier rejected the order: {reason}";

    /// <summary>The rejection reason when Access does not know the request's cost center.</summary>
    public static string UnknownCostCenter(string code) => $"Unknown cost center {code}";

    /// <summary>The alert reason when a step is overdue and nobody is above it to escalate to (usually finance).</summary>
    public static string StepOverdue(ApprovalLevel level) => level switch
    {
        ApprovalLevel.Manager => "Manager step overdue",
        ApprovalLevel.DepartmentHead => "Department head step overdue",
        _ => "Finance step overdue",
    };

    public Guid Id { get; set; }

    public string TenantId { get; set; } = "";

    public string RequestNumber { get; set; } = "";

    public string RequestTitle { get; set; } = "";

    public string RequesterId { get; set; } = "";

    public string CostCenterCode { get; set; } = "";

    /// <summary>Rand, including VAT.</summary>
    public decimal TotalInclVat { get; set; }

    public ApprovalState State { get; set; }

    /// <summary>The supplier the request orders from (from <see cref="RequestSubmitted"/>, sent on in <see cref="CreatePurchaseOrder"/>).</summary>
    public string SupplierId { get; set; } = "";

    /// <summary>The request's lines, frozen at submission (stored as jsonb).</summary>
    public List<RequestLineItem> Lines { get; set; } = [];

    public Guid? OrderId { get; set; }

    public string? OrderNumber { get; set; }

    /// <summary>The state to return to when Purchasing refuses a cancellation (Approved or Ordered).</summary>
    public ApprovalState? StateBeforeCancel { get; set; }

    /// <summary>The step number that is open (0 before the chain exists).</summary>
    public int CurrentStepNo { get; set; }

    /// <summary>The request was cancelled while the reservation was in flight; whatever Budgets answers ends the saga.</summary>
    public bool CancelRequested { get; set; }

    /// <summary>The open step that could not escalate any higher and raised the overdue alert (it is reminded from then on).</summary>
    public Guid? OverdueAlertStepId { get; set; }

    /// <summary>
    /// The last finance reminder of the overdue step that went out (the <c>Attempt</c> of its <see cref="StepTimeout"/>). The
    /// chain counts on its own, not by <see cref="RemindersSent"/>: the first reminder and the escalation can be due at once and
    /// run in either order, and the chain must not depend on which came first.
    /// </summary>
    public int OverdueRemindersSent { get; set; }

    /// <summary>
    /// The reminders sent for the open step (numbered 1, 2, ... whichever timeout sent them; 0 when a step opens). Kept on the
    /// saga, not on the step row, so a reminder never changes the step's version: an approver who loaded the step before a
    /// reminder can still approve it with the ETag they hold.
    /// </summary>
    public int RemindersSent { get; set; }

    private Money Total => Money.Zar(TotalInclVat);

    // ---- RequestSubmitted -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts the saga (Wolverine creates it when none exists for the request) and asks Budgets to reserve. The same submission
    /// delivered again finds the saga started and changes nothing, and so does a submission that arrives after its own
    /// cancellation (the cancelled marker that <see cref="StartOrHandle(RequestCancelled, IApprovalsDbContext, IMessageContext, ICacheInvalidator, CancellationToken)"/>
    /// left): nothing is reserved for a request that is already cancelled.
    /// </summary>
    public async Task StartOrHandle(
        [SagaIdentityFrom(nameof(RequestSubmitted.RequestId))] RequestSubmitted submitted, IMessageContext context,
        ICacheInvalidator cache)
    {
        if (TenantId.Length > 0)
        {
            return;
        }

        Id = submitted.RequestId;
        TenantId = submitted.TenantId;
        RequestNumber = submitted.Number;
        RequestTitle = submitted.Title;
        RequesterId = submitted.RequesterId;
        CostCenterCode = submitted.CostCenterCode;
        TotalInclVat = submitted.TotalInclVat;
        SupplierId = submitted.SupplierId;
        Lines = [.. submitted.Lines];
        State = ApprovalState.AwaitingBudget;
        await context.SendAsync(new ReserveBudget(
            submitted.RequestId, submitted.TenantId, submitted.CostCenterCode, submitted.TotalInclVat, submitted.SubmittedAt));
        await EvictAsync(cache);
    }

    // ---- Budgets' answer --------------------------------------------------------------------------------------------------

    public async Task Handle(
        [SagaIdentityFrom(nameof(BudgetReservationFailed.RequestId))] BudgetReservationFailed failed, IMessageContext context,
        TimeProvider time, ICacheInvalidator cache)
    {
        if (State != ApprovalState.AwaitingBudget)
        {
            return;
        }

        await EvictAsync(cache);
        if (CancelRequested || failed.Reason == BudgetReservationFailed.CancelledReason)
        {
            // Nothing was reserved and the requester has already cancelled (Budgets says so when the request's release reached
            // it first): no rejection to report.
            End(ApprovalState.Cancelled);
            return;
        }

        await context.PublishAsync(new RequestRejected(Id, TenantId, RequestNumber, failed.Reason, null, time.GetUtcNow(), RequesterId));
        End(ApprovalState.Rejected);
    }

    public static void NotFound([SagaIdentityFrom(nameof(BudgetReservationFailed.RequestId))] BudgetReservationFailed failed)
    {
    }

    public async Task Handle(
        [SagaIdentityFrom(nameof(BudgetReserved.RequestId))] BudgetReserved reserved, IApprovalsDbContext db,
        IAccessDirectory access, IOptions<ApprovalsOptions> options, TimeProvider time, IMessageContext context,
        ICacheInvalidator cache, CancellationToken cancellationToken)
    {
        if (State == ApprovalState.Cancelled)
        {
            // A cancelled marker (the cancellation came before the submission, or after the saga had ended): a reservation
            // delivered now belongs to no live approval, so it is released like one for an unknown saga.
            await context.SendAsync(Release());
            return;
        }

        if (State != ApprovalState.AwaitingBudget)
        {
            // A repeated reservation: the chain exists already.
            return;
        }

        await EvictAsync(cache);

        if (CancelRequested)
        {
            await context.SendAsync(Release());
            End(ApprovalState.Cancelled);
            return;
        }

        ApprovalChain chain;
        try
        {
            var org = await OrganisationAsync(access, cancellationToken);
            chain = ApprovalRouting.Resolve(Total, RequesterId, org, options.Value.ToThresholds());
        }
        catch (Exception exception) when (exception is NoEligibleApproverException or NotFoundException)
        {
            // Answers, not failures (a failed call to Access throws something else and is retried: Approvals:DependencyRetry).
            var reason = exception is NotFoundException ? UnknownCostCenter(CostCenterCode) : NoEligibleApprover;
            await context.SendAsync(Release());
            await context.PublishAsync(new RequestRejected(Id, TenantId, RequestNumber, reason, null, time.GetUtcNow(), RequesterId));
            End(ApprovalState.Rejected);
            return;
        }

        var steps = chain.Steps.Select(NewStep).ToList();
        db.Steps.AddRange(steps);
        State = ApprovalState.AwaitingApproval;
        await OpenAsync(steps[0], options.Value, time.GetUtcNow(), context);
    }

    /// <summary>
    /// A reservation for a request that is no longer (or never was) waiting for one, e.g. redelivered after the saga was
    /// cancelled or rejected. Released, so no reservation is ever left behind (the release is idempotent in Budgets).
    /// </summary>
    public static Task NotFound(
        [SagaIdentityFrom(nameof(BudgetReserved.RequestId))] BudgetReserved reserved, IMessageContext context) =>
        context.SendAsync(new ReleaseBudget(reserved.RequestId, reserved.TenantId)).AsTask();

    // ---- Approvers --------------------------------------------------------------------------------------------------------

    public async Task Handle(
        [SagaIdentityFrom(nameof(ApproveStep.RequestId))] ApproveStep command, IApprovalsDbContext db, ICallerContext caller,
        IOptions<ApprovalsOptions> options, TimeProvider time, IMessageContext context, ICacheInvalidator cache,
        CancellationToken cancellationToken)
    {
        var steps = await StepsAsync(db, cancellationToken);
        var step = Actionable(steps, command.StepId, command.ExpectedVersion, caller);
        await EvictAsync(cache);
        var now = time.GetUtcNow();
        step.Approve(caller.UserId, now, command.Comment);
        await context.PublishAsync(new ApprovalStepApproved(Id, TenantId, step.Id, caller.UserId, now, step.Comment));

        var next = steps.Where(s => s.IsPending && s.StepNo > step.StepNo).MinBy(s => s.StepNo);
        if (next is not null)
        {
            await OpenAsync(next, options.Value, now, context);
            return;
        }

        if (Lines.Count == 0 || string.IsNullOrWhiteSpace(SupplierId))
        {
            // A saga started before plan 03 (its migration gave old rows no lines and no supplier): Purchasing cannot order
            // that, so the request is rejected and the budget released here, instead of a CreatePurchaseOrder that dead-letters
            // and leaves the reservation held.
            await context.SendAsync(Release());
            await context.PublishAsync(new RequestRejected(Id, TenantId, RequestNumber, NoCatalogLines, null, now, RequesterId));
            End(ApprovalState.Rejected);
            return;
        }

        State = ApprovalState.Approved;
        var approvers = steps.Where(s => s.Status == ApprovalStepStatus.Approved).OrderBy(s => s.StepNo).Select(s => s.ActedBy!).ToList();
        await context.PublishAsync(new RequestApproved(Id, TenantId, RequestNumber, TotalInclVat, approvers, now, RequesterId));
        await context.SendAsync(new CreatePurchaseOrder(Id, TenantId, RequestNumber, RequesterId, CostCenterCode, SupplierId, Lines));
    }

    public static Task NotFound(
        [SagaIdentityFrom(nameof(ApproveStep.RequestId))] ApproveStep command, IApprovalsDbContext db,
        CancellationToken cancellationToken) =>
        StepGoneAsync(db, command.RequestId, command.StepId, cancellationToken);

    public async Task Handle(
        [SagaIdentityFrom(nameof(RejectStep.RequestId))] RejectStep command, IApprovalsDbContext db, ICallerContext caller,
        TimeProvider time, IMessageContext context, ICacheInvalidator cache, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            throw new DomainException("A rejection needs a reason.");
        }

        var steps = await StepsAsync(db, cancellationToken);
        var step = Actionable(steps, command.StepId, command.ExpectedVersion, caller);
        await EvictAsync(cache);
        var now = time.GetUtcNow();
        step.Reject(caller.UserId, now);
        CancelPending(steps);
        await context.SendAsync(Release());
        await context.PublishAsync(new RequestRejected(Id, TenantId, RequestNumber, command.Reason, caller.UserId, now, RequesterId));
        End(ApprovalState.Rejected);
    }

    public static Task NotFound(
        [SagaIdentityFrom(nameof(RejectStep.RequestId))] RejectStep command, IApprovalsDbContext db,
        CancellationToken cancellationToken) =>
        StepGoneAsync(db, command.RequestId, command.StepId, cancellationToken);

    // ---- Timeouts ---------------------------------------------------------------------------------------------------------

    public async Task Handle(
        [SagaIdentityFrom(nameof(StepTimeout.RequestId))] StepTimeout timeout, IApprovalsDbContext db, IAccessDirectory access,
        IOptions<ApprovalsOptions> options, TimeProvider time, IMessageContext context, ICacheInvalidator cache,
        CancellationToken cancellationToken)
    {
        if (State != ApprovalState.AwaitingApproval)
        {
            return;
        }

        var steps = await StepsAsync(db, cancellationToken);
        var step = steps.SingleOrDefault(s => s.Id == timeout.StepId);
        if (step is not { IsPending: true } || step.StepNo != CurrentStepNo)
        {
            // Acted on, escalated or cancelled since the timeout was scheduled.
            return;
        }

        var now = time.GetUtcNow();
        switch (timeout.Kind)
        {
            // The step's one reminder; skipped when a finance reminder already went out (it ran late, after the escalation).
            case StepTimeoutKind.Reminder when timeout.Attempt == 1 && RemindersSent == 0:
                await RemindAsync(step, now, context);
                break;
            case StepTimeoutKind.FinanceReminder when OverdueAlertStepId == step.Id && timeout.Attempt == OverdueRemindersSent + 1:
                OverdueRemindersSent = timeout.Attempt;
                await RemindAsync(step, now, context);
                await context.ScheduleAsync(timeout with { Attempt = timeout.Attempt + 1 }, options.Value.Timeouts.FinanceReminderEvery);
                break;
            case StepTimeoutKind.Escalate when timeout.Attempt == 1 && OverdueAlertStepId != step.Id:
                await EscalateAsync(step, steps, db, access, options.Value, now, context, cancellationToken);
                // An alert changes nothing the API shows, a replacement does; evicting in both cases is cheap.
                await EvictAsync(cache);
                break;
        }
    }

    public static void NotFound([SagaIdentityFrom(nameof(StepTimeout.RequestId))] StepTimeout timeout)
    {
    }

    // ---- Cancellation -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Ends the approval of a cancelled request. With no saga for the request (Wolverine creates one), the cancellation came
    /// before the submission: approvals-api was down while the requester submitted and cancelled, and the outbox or the
    /// parallel listener delivered the two out of order. The saga is then stored as a cancelled marker, which is not completed
    /// (deleted), so the submission that follows finds it and reserves nothing. Two first messages handled at once both insert
    /// the saga row: one insert fails on the key and is retried (<c>Approvals:ConcurrencyRetry</c>), and then finds the other's
    /// saga, so either order ends cancelled. The marker also appears when a cancellation arrives after the saga ended (it was
    /// rejected or cancelled already; the release was sent then); it holds no steps and costs one row.
    /// </summary>
    public async Task StartOrHandle(
        [SagaIdentityFrom(nameof(RequestCancelled.RequestId))] RequestCancelled cancelled, IApprovalsDbContext db,
        IMessageContext context, ICacheInvalidator cache, CancellationToken cancellationToken)
    {
        if (TenantId.Length == 0)
        {
            Id = cancelled.RequestId;
            TenantId = cancelled.TenantId;
            RequestNumber = cancelled.Number;
            State = ApprovalState.Cancelled;
            await EvictAsync(cache);
            return;
        }

        if (State == ApprovalState.Cancelled)
        {
            // The cancellation delivered again to the cancelled marker.
            return;
        }

        if (State == ApprovalState.Cancelling)
        {
            // Purchasing is already deciding on the cancellation we sent.
            return;
        }

        await EvictAsync(cache);
        if (State == ApprovalState.AwaitingBudget)
        {
            // The reservation is in flight: Budgets' answer ends the saga (and releases what it reserved).
            CancelRequested = true;
            return;
        }

        if (State is ApprovalState.Approved or ApprovalState.Ordered)
        {
            // Purchasing alone decides whether the order can still be cancelled (goods received or not). It answers
            // PurchaseOrderCancelled (also when it never saw the order: it keeps a tombstone) or PurchaseOrderCancellationRefused.
            StateBeforeCancel = State;
            State = ApprovalState.Cancelling;
            await context.SendAsync(new CancelPurchaseOrder(
                Id, TenantId, string.IsNullOrWhiteSpace(cancelled.Reason) ? DefaultCancelReason : cancelled.Reason));
            return;
        }

        CancelPending(await StepsAsync(db, cancellationToken));
        await context.SendAsync(Release());
        End(ApprovalState.Cancelled);
    }

    // ---- Purchasing's answers ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The order exists. While the saga is Cancelling nothing is announced (the order is about to be cancelled, or the
    /// cancellation refused, which restores Ordered); the order is stored all the same.
    /// </summary>
    public async Task Handle(
        [SagaIdentityFrom(nameof(PurchaseOrderCreated.RequestId))] PurchaseOrderCreated created, TimeProvider time,
        IMessageContext context, ICacheInvalidator cache)
    {
        if (State is not (ApprovalState.Approved or ApprovalState.Cancelling))
        {
            // Ordered already (a repeat), or no order is wanted any more.
            return;
        }

        OrderId = created.OrderId;
        OrderNumber = created.OrderNumber;
        if (State == ApprovalState.Cancelling)
        {
            return;
        }

        State = ApprovalState.Ordered;
        await EvictAsync(cache);
        await context.PublishAsync(new RequestOrdered(Id, TenantId, RequestNumber, created.OrderId, created.OrderNumber, time.GetUtcNow()));
    }

    public static void NotFound([SagaIdentityFrom(nameof(PurchaseOrderCreated.RequestId))] PurchaseOrderCreated created)
    {
    }

    /// <summary>The supplier rejected the order: the request ends Rejected (Cancelled when it was being cancelled), and the budget is released.</summary>
    public async Task Handle(
        [SagaIdentityFrom(nameof(PurchaseOrderRejected.RequestId))] PurchaseOrderRejected rejected, TimeProvider time,
        IMessageContext context, ICacheInvalidator cache)
    {
        if (State is not (ApprovalState.Approved or ApprovalState.Ordered or ApprovalState.Cancelling))
        {
            return;
        }

        await EvictAsync(cache);
        await context.SendAsync(Release());
        if (State == ApprovalState.Cancelling)
        {
            await context.PublishAsync(new RequestCancellationConfirmed(Id, TenantId, RequestNumber, time.GetUtcNow()));
            End(ApprovalState.Cancelled);
            return;
        }

        await context.PublishAsync(new RequestRejected(
            Id, TenantId, RequestNumber, SupplierRejected(rejected.Reason), null, time.GetUtcNow(), RequesterId));
        End(ApprovalState.Rejected);
    }

    public static void NotFound([SagaIdentityFrom(nameof(PurchaseOrderRejected.RequestId))] PurchaseOrderRejected rejected)
    {
    }

    /// <summary>Purchasing cancelled the order (or, having never seen it, stored a tombstone): the request is cancelled and the budget released.</summary>
    public async Task Handle(
        [SagaIdentityFrom(nameof(PurchaseOrderCancelled.RequestId))] PurchaseOrderCancelled cancelled, TimeProvider time,
        IMessageContext context, ICacheInvalidator cache)
    {
        if (State != ApprovalState.Cancelling)
        {
            return;
        }

        await EvictAsync(cache);
        await context.SendAsync(Release());
        await context.PublishAsync(new RequestCancellationConfirmed(Id, TenantId, RequestNumber, time.GetUtcNow()));
        End(ApprovalState.Cancelled);
    }

    public static void NotFound([SagaIdentityFrom(nameof(PurchaseOrderCancelled.RequestId))] PurchaseOrderCancelled cancelled)
    {
    }

    /// <summary>Purchasing refused the cancellation (goods were received): back to where we were, and the requester is told why.</summary>
    public async Task Handle(
        [SagaIdentityFrom(nameof(PurchaseOrderCancellationRefused.RequestId))] PurchaseOrderCancellationRefused refused,
        TimeProvider time, IMessageContext context, ICacheInvalidator cache)
    {
        if (State != ApprovalState.Cancelling)
        {
            return;
        }

        // A refusal means the order exists. If the order was created while cancelling, nobody was told of it yet (Requests is
        // back to Approved after the refusal): the saga is Ordered, and RequestOrdered goes out after the refusal.
        var announce = OrderUnannounced;
        State = OrderId is not null ? ApprovalState.Ordered : StateBeforeCancel ?? ApprovalState.Ordered;
        StateBeforeCancel = null;
        await EvictAsync(cache);
        var now = time.GetUtcNow();
        await context.PublishAsync(new RequestCancellationRefused(Id, TenantId, RequestNumber, refused.Reason, now, RequesterId));
        if (announce && OrderId is { } orderId)
        {
            await context.PublishAsync(new RequestOrdered(Id, TenantId, RequestNumber, orderId, OrderNumber ?? "", now));
        }
    }

    /// <summary>
    /// The order was created (or settled) and <see cref="RequestOrdered"/> was never published: while Approved the order is not
    /// recorded yet, and an order created while Cancelling from Approved is stored but not announced.
    /// </summary>
    private bool OrderUnannounced =>
        State == ApprovalState.Approved || (State == ApprovalState.Cancelling && StateBeforeCancel == ApprovalState.Approved);

    public static void NotFound(
        [SagaIdentityFrom(nameof(PurchaseOrderCancellationRefused.RequestId))] PurchaseOrderCancellationRefused refused)
    {
    }

    // ---- Invoicing's events -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Records the spend of a payment in Budgets (idempotent there by payment ID). Sent whatever the saga's state, and by
    /// <c>NotFound</c> too: the saga may have completed first, and Budgets gives the same final numbers in either order.
    /// </summary>
    public Task Handle(
        [SagaIdentityFrom(nameof(PaymentReleased.RequestId))] PaymentReleased released, IMessageContext context) =>
        context.SendAsync(Spend(released)).AsTask();

    public static Task NotFound(
        [SagaIdentityFrom(nameof(PaymentReleased.RequestId))] PaymentReleased released, IMessageContext context) =>
        context.SendAsync(Spend(released)).AsTask();

    private static RecordSpend Spend(PaymentReleased released) =>
        new(released.RequestId, released.TenantId, released.PaymentId, released.AmountInclVat);

    /// <summary>Every order line is paid or closed short: release what is left of the reservation and complete the request.</summary>
    public async Task Handle(
        [SagaIdentityFrom(nameof(PurchaseOrderSettled.RequestId))] PurchaseOrderSettled settled, TimeProvider time,
        IMessageContext context, ICacheInvalidator cache)
    {
        if (State is not (ApprovalState.Approved or ApprovalState.Ordered or ApprovalState.Cancelling))
        {
            return;
        }

        await EvictAsync(cache);
        await context.SendAsync(Release());
        var now = time.GetUtcNow();
        if (State == ApprovalState.Cancelling)
        {
            // Purchasing's refusal (it will come, goods were received) would find no saga: tell Requests now, before it completes.
            await context.PublishAsync(new RequestCancellationRefused(Id, TenantId, RequestNumber, OrderSettled, now, RequesterId));
        }

        if (OrderUnannounced)
        {
            // Requests never heard of the order (the settlement overtook it): it must pass through Ordered to complete.
            await context.PublishAsync(new RequestOrdered(
                Id, TenantId, RequestNumber, OrderId ?? settled.OrderId, OrderNumber ?? "", now));
        }

        await context.PublishAsync(new RequestCompleted(Id, TenantId, RequestNumber, settled.PaidInclVat, now, RequesterId));
        End(ApprovalState.Completed);
    }

    public static void NotFound([SagaIdentityFrom(nameof(PurchaseOrderSettled.RequestId))] PurchaseOrderSettled settled)
    {
    }

    // ---- Steps ------------------------------------------------------------------------------------------------------------

    private async Task EscalateAsync(
        ApprovalStep step, List<ApprovalStep> steps, IApprovalsDbContext db, IAccessDirectory access, ApprovalsOptions options,
        DateTimeOffset now, IMessageContext context, CancellationToken cancellationToken)
    {
        PlannedStep? replacement;
        string? alert;
        try
        {
            var org = await OrganisationAsync(access, cancellationToken);
            var approved = steps.Where(s => s.Status == ApprovalStepStatus.Approved).Select(s => s.ActedBy!).ToList();
            replacement = ApprovalRouting.Escalate(step.ToPlanned(), Total, RequesterId, org, approved);
            alert = replacement is null ? StepOverdue(step.Level) : null;
        }
        catch (NotFoundException)
        {
            // The cost center was removed while the request waited: nobody to escalate to; a person has to sort it out.
            replacement = null;
            alert = UnknownCostCenter(CostCenterCode);
        }

        if (replacement is null)
        {
            // Nobody above: raise the alert once and keep reminding the step's approvers.
            OverdueAlertStepId = step.Id;
            OverdueRemindersSent = 0;
            await context.PublishAsync(new ApprovalAlertRaised(Id, TenantId, step.Id, alert!, now));
            await context.ScheduleAsync(
                new StepTimeout(Id, step.Id, StepTimeoutKind.FinanceReminder, 1), options.Timeouts.FinanceReminderEvery);
            return;
        }

        // The replacement takes over the step; a following step it reached (same level or below) is merged into it.
        step.Escalate();
        var merged = steps.Where(s => s.IsPending && s.StepNo > step.StepNo && s.Level <= replacement.Level).ToList();
        foreach (var follower in merged)
        {
            follower.Cancel();
        }

        var covers = replacement.CoversLevels.Concat(merged.SelectMany(s => s.CoversLevels)).Distinct().Order().ToList();
        var next = NewStep(replacement with { CoversLevels = covers });
        db.Steps.Add(next);
        await context.PublishAsync(new ApprovalEscalated(
            Id, TenantId, next.Id, step.Level.ToString(), next.Level.ToString(), next.ApproverIds, now, RequestNumber));
        await OpenAsync(next, options, now, context);
    }

    /// <summary>Reminds the step's approvers; reminders of a step are numbered 1, 2, ... whichever timeout sent them.</summary>
    private async Task RemindAsync(ApprovalStep step, DateTimeOffset now, IMessageContext context)
    {
        var reminderNo = ++RemindersSent;
        await context.PublishAsync(new ApprovalReminderDue(Id, TenantId, step.Id, step.ApproverIds, reminderNo, now, RequestNumber));
    }

    /// <summary>Makes <paramref name="step"/> the open one: announces it and schedules its reminder and escalation.</summary>
    private async Task OpenAsync(ApprovalStep step, ApprovalsOptions options, DateTimeOffset now, IMessageContext context)
    {
        CurrentStepNo = step.StepNo;
        RemindersSent = 0;
        var dueAt = now + options.Timeouts.EscalateAfter;
        step.Open(dueAt, now);
        await context.PublishAsync(new ApprovalRequested(
            Id, TenantId, RequestNumber, step.Id, step.StepNo, step.Level.ToString(), step.ApproverIds, dueAt, now));
        await context.ScheduleAsync(new StepTimeout(Id, step.Id, StepTimeoutKind.Reminder, 1), options.Timeouts.ReminderAfter);
        await context.ScheduleAsync(new StepTimeout(Id, step.Id, StepTimeoutKind.Escalate, 1), options.Timeouts.EscalateAfter);
    }

    /// <summary>
    /// The step the caller wants to act on, if they may: it exists in this request of the caller's tenant (else 404), it is the
    /// open step of a request awaiting approval (else 409), the caller is one of its approvers and did not raise the request
    /// (else 403), and the caller saw its current version (else 412).
    /// </summary>
    private ApprovalStep Actionable(List<ApprovalStep> steps, Guid stepId, uint expectedVersion, ICallerContext caller)
    {
        var step = caller.TenantId == TenantId ? steps.SingleOrDefault(s => s.Id == stepId) : null;
        if (step is null)
        {
            throw new NotFoundException($"Approval step {stepId} was not found.");
        }

        if (State != ApprovalState.AwaitingApproval || !step.IsPending || step.StepNo != CurrentStepNo)
        {
            throw new ConflictException($"Approval step {step.StepNo} of {RequestNumber} is not open (it is {step.Status}).");
        }

        if (caller.UserId == RequesterId)
        {
            throw new ForbiddenException("You raised this request, so you cannot approve or reject it.");
        }

        if (!step.MayBeActedOnBy(caller.UserId))
        {
            throw new ForbiddenException($"You are not an approver of step {step.StepNo} of {RequestNumber}.");
        }

        // Last: a caller who may not act at all hears 403, not "reload" (412) for a copy they could never use.
        OptimisticConcurrency.EnsureVersion($"Approval step {step.StepNo} of {RequestNumber}", step.Version, expectedVersion);
        return step;
    }

    private static async Task StepGoneAsync(IApprovalsDbContext db, Guid requestId, Guid stepId, CancellationToken cancellationToken)
    {
        // The saga ended (rejected or cancelled): the step is still visible in the caller's tenant, but closed.
        var exists = await db.Steps.AnyAsync(s => s.RequestId == requestId && s.Id == stepId, cancellationToken);
        throw exists
            ? new ConflictException($"Approval step {stepId} is not open any more.")
            : new NotFoundException($"Approval step {stepId} was not found.");
    }

    private async Task<OrganisationSnapshot> OrganisationAsync(IAccessDirectory access, CancellationToken cancellationToken) =>
        OrganisationMapper.ToSnapshot(await access.GetApproversAsync(CostCenterCode, TenantId, cancellationToken));

    /// <summary>The request's steps. The tenant is named, not taken from the caller: a timeout runs with nobody calling.</summary>
    private Task<List<ApprovalStep>> StepsAsync(IApprovalsDbContext db, CancellationToken cancellationToken) =>
        db.Steps.IgnoreQueryFilters()
            .Where(s => s.TenantId == TenantId && s.RequestId == Id)
            .OrderBy(s => s.StepNo)
            .ToListAsync(cancellationToken);

    private ApprovalStep NewStep(PlannedStep planned) =>
        ApprovalStep.Plan(TenantId, Id, RequestNumber, RequestTitle, RequesterId, CostCenterCode, Total, planned);

    private static void CancelPending(IEnumerable<ApprovalStep> steps)
    {
        foreach (var step in steps.Where(s => s.IsPending))
        {
            step.Cancel();
        }
    }

    private ReleaseBudget Release() => new(Id, TenantId);

    /// <summary>Evicts the cached inbox of the tenant and the cached chain of this request once the handler's transaction commits.</summary>
    private Task EvictAsync(ICacheInvalidator cache) => cache.RemoveAfterCommitAsync(ApprovalCacheTag.Of(TenantId, Id));

    private void End(ApprovalState state)
    {
        State = state;
        MarkCompleted();
    }
}
