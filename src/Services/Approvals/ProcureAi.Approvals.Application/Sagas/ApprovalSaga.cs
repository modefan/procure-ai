using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcureAi.Approvals.Application.Abstractions;
using ProcureAi.Approvals.Application.Mapping;
using ProcureAi.Approvals.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts.Approvals;
using ProcureAi.Contracts.Budgets;
using ProcureAi.Contracts.Requests;
using Wolverine;
using Wolverine.Persistence.Sagas;

namespace ProcureAi.Approvals.Application.Sagas;

public enum ApprovalState
{
    AwaitingBudget,
    AwaitingApproval,

    /// <summary>Every step approved. The saga stays (plan 03 continues it through ordering and payment).</summary>
    Approved,
    Rejected,
    Cancelled,
}

/// <summary>
/// The approval of one purchase request, from submission to approval, rejection or cancellation. Its ID is the request's ID.
/// Wolverine persists it with EF Core in the Approvals database, in one transaction with the approval steps and the outbox,
/// so a state change and the messages it sends commit together. Rejection and cancellation complete (delete) it; a message
/// for a completed or unknown saga goes to the <c>NotFound</c> handlers.
/// <para>
/// Flow: <see cref="RequestSubmitted"/> asks Budgets to reserve; <see cref="BudgetReserved"/> resolves the chain from the Access
/// service's view of the cost center and opens step 1; each <see cref="ApproveStep"/> opens the next step until the last, which
/// approves the request. A reject, a refused reservation or a cancellation ends it, and every end after a reservation releases
/// the budget. Each open step has a reminder and an escalation timeout, scheduled durably.
/// </para>
/// </summary>
public sealed class ApprovalSaga : Saga
{
    public const string NoEligibleApprover = "No eligible approver";
    public const string FinanceStepOverdue = "Finance step overdue";

    public Guid Id { get; set; }

    public string TenantId { get; set; } = "";

    public string RequestNumber { get; set; } = "";

    public string RequestTitle { get; set; } = "";

    public string RequesterId { get; set; } = "";

    public string CostCenterCode { get; set; } = "";

    /// <summary>Rand, including VAT.</summary>
    public decimal TotalInclVat { get; set; }

    public ApprovalState State { get; set; }

    /// <summary>The step number that is open (0 before the chain exists).</summary>
    public int CurrentStepNo { get; set; }

    /// <summary>The request was cancelled while the reservation was in flight; whatever Budgets answers ends the saga.</summary>
    public bool CancelRequested { get; set; }

    /// <summary>The open step that could not escalate any higher and raised the overdue alert (it is reminded from then on).</summary>
    public Guid? OverdueAlertStepId { get; set; }

    private Money Total => Money.Zar(TotalInclVat);

    // ---- RequestSubmitted -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts the saga (Wolverine creates it when none exists for the request) and asks Budgets to reserve. The same submission
    /// delivered again finds the saga started and changes nothing.
    /// </summary>
    public async Task StartOrHandle(
        [SagaIdentityFrom(nameof(RequestSubmitted.RequestId))] RequestSubmitted submitted, IMessageContext context)
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
        State = ApprovalState.AwaitingBudget;
        await context.SendAsync(new ReserveBudget(
            submitted.RequestId, submitted.TenantId, submitted.CostCenterCode, submitted.TotalInclVat, submitted.SubmittedAt));
    }

    // ---- Budgets' answer --------------------------------------------------------------------------------------------------

    public async Task Handle(
        [SagaIdentityFrom(nameof(BudgetReservationFailed.RequestId))] BudgetReservationFailed failed, IMessageContext context,
        TimeProvider time)
    {
        if (State != ApprovalState.AwaitingBudget)
        {
            return;
        }

        if (CancelRequested)
        {
            // Nothing was reserved and the requester has already cancelled: no rejection to report.
            End(ApprovalState.Cancelled);
            return;
        }

        await context.PublishAsync(new RequestRejected(Id, TenantId, RequestNumber, failed.Reason, null, time.GetUtcNow()));
        End(ApprovalState.Rejected);
    }

    public static void NotFound([SagaIdentityFrom(nameof(BudgetReservationFailed.RequestId))] BudgetReservationFailed failed)
    {
    }

    public async Task Handle(
        [SagaIdentityFrom(nameof(BudgetReserved.RequestId))] BudgetReserved reserved, IApprovalsDbContext db,
        IAccessDirectory access, IOptions<ApprovalsOptions> options, TimeProvider time, IMessageContext context,
        CancellationToken cancellationToken)
    {
        if (State != ApprovalState.AwaitingBudget)
        {
            // A repeated reservation: the chain exists already (or the saga ended and released).
            return;
        }

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
            await context.SendAsync(Release());
            await context.PublishAsync(new RequestRejected(Id, TenantId, RequestNumber, NoEligibleApprover, null, time.GetUtcNow()));
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
        IOptions<ApprovalsOptions> options, TimeProvider time, IMessageContext context, CancellationToken cancellationToken)
    {
        var steps = await StepsAsync(db, cancellationToken);
        var step = Actionable(steps, command.StepId, command.ExpectedVersion, caller);
        var now = time.GetUtcNow();
        step.Approve(caller.UserId, now);
        await context.PublishAsync(new ApprovalStepApproved(Id, TenantId, step.Id, caller.UserId, now));

        var next = steps.Where(s => s.IsPending && s.StepNo > step.StepNo).MinBy(s => s.StepNo);
        if (next is not null)
        {
            await OpenAsync(next, options.Value, now, context);
            return;
        }

        State = ApprovalState.Approved;
        var approvers = steps.Where(s => s.Status == ApprovalStepStatus.Approved).OrderBy(s => s.StepNo).Select(s => s.ActedBy!).ToList();
        await context.PublishAsync(new RequestApproved(Id, TenantId, RequestNumber, TotalInclVat, approvers, now));
    }

    public static Task NotFound(
        [SagaIdentityFrom(nameof(ApproveStep.RequestId))] ApproveStep command, IApprovalsDbContext db,
        CancellationToken cancellationToken) =>
        StepGoneAsync(db, command.RequestId, command.StepId, cancellationToken);

    public async Task Handle(
        [SagaIdentityFrom(nameof(RejectStep.RequestId))] RejectStep command, IApprovalsDbContext db, ICallerContext caller,
        TimeProvider time, IMessageContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            throw new DomainException("A rejection needs a reason.");
        }

        var steps = await StepsAsync(db, cancellationToken);
        var step = Actionable(steps, command.StepId, command.ExpectedVersion, caller);
        var now = time.GetUtcNow();
        step.Reject(caller.UserId, now);
        CancelPending(steps);
        await context.SendAsync(Release());
        await context.PublishAsync(new RequestRejected(Id, TenantId, RequestNumber, command.Reason, caller.UserId, now));
        End(ApprovalState.Rejected);
    }

    public static Task NotFound(
        [SagaIdentityFrom(nameof(RejectStep.RequestId))] RejectStep command, IApprovalsDbContext db,
        CancellationToken cancellationToken) =>
        StepGoneAsync(db, command.RequestId, command.StepId, cancellationToken);

    // ---- Timeouts ---------------------------------------------------------------------------------------------------------

    public async Task Handle(
        [SagaIdentityFrom(nameof(StepTimeout.RequestId))] StepTimeout timeout, IApprovalsDbContext db, IAccessDirectory access,
        IOptions<ApprovalsOptions> options, TimeProvider time, IMessageContext context, CancellationToken cancellationToken)
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
            case StepTimeoutKind.Reminder or StepTimeoutKind.FinanceReminder when timeout.Attempt == step.RemindersSent + 1:
                step.RecordReminder(timeout.Attempt);
                await context.PublishAsync(new ApprovalReminderDue(Id, TenantId, step.Id, step.ApproverIds, timeout.Attempt, now));
                if (timeout.Kind == StepTimeoutKind.FinanceReminder)
                {
                    await context.ScheduleAsync(
                        timeout with { Attempt = timeout.Attempt + 1 }, options.Value.Timeouts.FinanceReminderEvery);
                }

                break;
            case StepTimeoutKind.Escalate when timeout.Attempt == 1 && OverdueAlertStepId != step.Id:
                await EscalateAsync(step, steps, db, access, options.Value, now, context, cancellationToken);
                break;
        }
    }

    public static void NotFound([SagaIdentityFrom(nameof(StepTimeout.RequestId))] StepTimeout timeout)
    {
    }

    // ---- Cancellation -----------------------------------------------------------------------------------------------------

    public async Task Handle(
        [SagaIdentityFrom(nameof(RequestCancelled.RequestId))] RequestCancelled cancelled, IApprovalsDbContext db,
        IMessageContext context, CancellationToken cancellationToken)
    {
        if (State == ApprovalState.AwaitingBudget)
        {
            // The reservation is in flight: Budgets' answer ends the saga (and releases what it reserved).
            CancelRequested = true;
            return;
        }

        CancelPending(await StepsAsync(db, cancellationToken));
        await context.SendAsync(Release());
        End(ApprovalState.Cancelled);
    }

    public static void NotFound([SagaIdentityFrom(nameof(RequestCancelled.RequestId))] RequestCancelled cancelled)
    {
    }

    // ---- Steps ------------------------------------------------------------------------------------------------------------

    private async Task EscalateAsync(
        ApprovalStep step, List<ApprovalStep> steps, IApprovalsDbContext db, IAccessDirectory access, ApprovalsOptions options,
        DateTimeOffset now, IMessageContext context, CancellationToken cancellationToken)
    {
        var org = await OrganisationAsync(access, cancellationToken);
        var approved = steps.Where(s => s.Status == ApprovalStepStatus.Approved).Select(s => s.ActedBy!).ToList();
        var replacement = ApprovalRouting.Escalate(step.ToPlanned(), Total, RequesterId, org, approved);
        if (replacement is null)
        {
            // Nobody above: raise the alert once and keep reminding the step's approvers.
            OverdueAlertStepId = step.Id;
            await context.PublishAsync(new ApprovalAlertRaised(Id, TenantId, step.Id, FinanceStepOverdue, now));
            await context.ScheduleAsync(
                new StepTimeout(Id, step.Id, StepTimeoutKind.FinanceReminder, step.RemindersSent + 1),
                options.Timeouts.FinanceReminderEvery);
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
            Id, TenantId, next.Id, step.Level.ToString(), next.Level.ToString(), next.ApproverIds, now));
        await OpenAsync(next, options, now, context);
    }

    /// <summary>Makes <paramref name="step"/> the open one: announces it and schedules its reminder and escalation.</summary>
    private async Task OpenAsync(ApprovalStep step, ApprovalsOptions options, DateTimeOffset now, IMessageContext context)
    {
        CurrentStepNo = step.StepNo;
        var dueAt = now + options.Timeouts.EscalateAfter;
        step.Open(dueAt);
        await context.PublishAsync(new ApprovalRequested(
            Id, TenantId, RequestNumber, step.Id, step.StepNo, step.Level.ToString(), step.ApproverIds, dueAt, now));
        await context.ScheduleAsync(new StepTimeout(Id, step.Id, StepTimeoutKind.Reminder, 1), options.Timeouts.ReminderAfter);
        await context.ScheduleAsync(new StepTimeout(Id, step.Id, StepTimeoutKind.Escalate, 1), options.Timeouts.EscalateAfter);
    }

    /// <summary>
    /// The step the caller wants to act on, if they may: it exists in this request of the caller's tenant (else 404), it is the
    /// open step of a request awaiting approval (else 409), the caller saw its current version (else 412), and the caller is
    /// one of its approvers and did not raise the request (else 403).
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

        OptimisticConcurrency.EnsureVersion($"Approval step {step.StepNo} of {RequestNumber}", step.Version, expectedVersion);
        if (caller.UserId == RequesterId)
        {
            throw new ForbiddenException("You raised this request, so you cannot approve or reject it.");
        }

        if (!step.MayBeActedOnBy(caller.UserId))
        {
            throw new ForbiddenException($"You are not an approver of step {step.StepNo} of {RequestNumber}.");
        }

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
        ApprovalStep.Plan(TenantId, Id, RequestNumber, RequestTitle, RequesterId, Total, planned);

    private static void CancelPending(IEnumerable<ApprovalStep> steps)
    {
        foreach (var step in steps.Where(s => s.IsPending))
        {
            step.Cancel();
        }
    }

    private ReleaseBudget Release() => new(Id, TenantId);

    private void End(ApprovalState state)
    {
        State = state;
        MarkCompleted();
    }
}
