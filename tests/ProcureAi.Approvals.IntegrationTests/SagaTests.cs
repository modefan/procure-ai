using ProcureAi.Approvals.Application.Sagas;
using ProcureAi.Approvals.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Budgets;
using Shouldly;
using static ProcureAi.Approvals.IntegrationTests.ApprovalsApp;

namespace ProcureAi.Approvals.IntegrationTests;

/// <summary>
/// The approval saga end to end on real Postgres and RabbitMQ: events come in through the broker as the Requests and Budgets
/// services would send them (or in-process on a fast instance for the timeout tests), and what the saga sends is recorded.
/// </summary>
[Collection(ApprovalsCollection.Name)]
public sealed class SagaTests(ApprovalsApp app)
{
    /// <summary>How long a test waits before asserting that something did NOT happen.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    /// <summary>A short Access outage: outlasts the HTTP client's own retries (well under a second), not the message's retries.</summary>
    private static readonly TimeSpan AccessOutage = TimeSpan.FromSeconds(3);

    /// <summary>Waits for a message retried after the outage (first retry after Approvals:DependencyRetry:InitialDelay).</summary>
    private static readonly TimeSpan RetryTimeout = TimeSpan.FromSeconds(60);

    private ApprovalsInstance Main => app.Main;

    private RabbitRecorder Recorder => app.Recorder;

    [Fact]
    public async Task Happy_path_R30000_needs_manager_then_head_then_RequestApproved()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        var submitted = Submitted(tenant, DemoUsers.Tom, 30_000m);
        var id = submitted.RequestId;

        await Main.PublishAsync(submitted, Exchanges.Requests, tenant);
        var reserve = (await Recorder.WaitForAsync(id, nameof(ReserveBudget), EventTimeout)).Single(m => m.Kind == nameof(ReserveBudget));
        reserve.Body.GetProperty("amount").GetDecimal().ShouldBe(30_000m);
        reserve.Text("costCenterCode").ShouldBe(CostCenter);
        reserve.Text("tenantId").ShouldBe(tenant);

        await Main.PublishAsync(Reserved(submitted), Exchanges.Budgets, tenant);
        await Recorder.WaitForAsync(id, nameof(ProcureAi.Contracts.Approvals.ApprovalRequested), EventTimeout);
        var steps = await Main.StepsAsync(id);
        steps.Select(s => $"{s.StepNo}:{s.Level}:{string.Join(",", s.ApproverIds)}:{s.Status}")
            .ShouldBe([$"1:Manager:{DemoUsers.Maria}:Pending", $"2:DepartmentHead:{DemoUsers.Dept}:Pending"]);
        steps[0].DueAt.ShouldNotBeNull();
        steps[1].DueAt.ShouldBeNull("only the open step has a due date");
        steps.ShouldAllBe(s => s.TotalInclVat.Amount == 30_000m && s.RequesterId == DemoUsers.Tom && s.TenantId == tenant);
        // The saga called Access for the request's tenant (system work: the tenant is named, not taken from a user).
        app.Access.Calls.ShouldContain(c => c.Path == $"/api/v1/access/internal/cost-centers/{CostCenter}/approvers" && c.Tenant == tenant);

        await Main.InvokeAsync(new ApproveStep(id, steps[0].Id, steps[0].Version), tenant, DemoUsers.Maria);
        await Recorder.WaitForAsync(id, nameof(ProcureAi.Contracts.Approvals.ApprovalRequested), EventTimeout, count: 2);
        steps = await Main.StepsAsync(id);
        steps[0].Status.ShouldBe(ApprovalStepStatus.Approved);
        steps[0].ActedBy.ShouldBe(DemoUsers.Maria);
        steps[1].DueAt.ShouldNotBeNull();

        await Main.InvokeAsync(new ApproveStep(id, steps[1].Id, steps[1].Version), tenant, DemoUsers.Dept);
        var seen = await Recorder.WaitForAsync(id, nameof(ProcureAi.Contracts.Approvals.RequestApproved), EventTimeout);

        var approved = seen.Single(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.RequestApproved));
        approved.Body.GetProperty("approverIds").EnumerateArray().Select(e => e.GetString()).ShouldBe([DemoUsers.Maria, DemoUsers.Dept]);
        approved.Body.GetProperty("totalInclVat").GetDecimal().ShouldBe(30_000m);
        // Who raised the request, for the requester's bell and email (plan 04 ruling 8).
        approved.Text("requesterId").ShouldBe(DemoUsers.Tom);
        seen.Count(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.ApprovalStepApproved)).ShouldBe(2);
        seen.ShouldNotContain(m => m.Kind == nameof(ReleaseBudget));
        // An approved saga stays (plan 03 continues it).
        (await Main.SagaAsync(id))!.State.ShouldBe(ApprovalState.Approved);
    }

    [Fact]
    public async Task Over_budget_rejects_with_reason()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        var submitted = Submitted(tenant, DemoUsers.Tom, 950_000m);
        var id = submitted.RequestId;
        await Main.PublishAsync(submitted, Exchanges.Requests, tenant);
        await Recorder.WaitForAsync(id, nameof(ReserveBudget), EventTimeout);

        const string reason = "Insufficient budget: R 900 000,00 available, R 950 000,00 requested.";
        await Main.PublishAsync(
            new BudgetReservationFailed(id, tenant, CostCenter, reason, 950_000m, 900_000m, DateTimeOffset.UtcNow), Exchanges.Budgets, tenant);
        var seen = await Recorder.WaitForAsync(id, nameof(ProcureAi.Contracts.Approvals.RequestRejected), EventTimeout);

        var rejected = seen.Single(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.RequestRejected));
        rejected.Text("reason").ShouldBe(reason);
        rejected.Text("rejectedBy").ShouldBeNull();
        rejected.Text("number").ShouldBe(submitted.Number);
        rejected.Text("requesterId").ShouldBe(DemoUsers.Tom);
        await Main.WaitForSagaAsync(id, saga => saga is null, EventTimeout);
        (await Main.StepsAsync(id)).ShouldBeEmpty();
        seen.ShouldNotContain(m => m.Kind == nameof(ReleaseBudget), "nothing was reserved");
    }

    [Fact]
    public async Task Reject_releases_budget_and_publishes_RequestRejected()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        var (submitted, steps) = await AwaitingApprovalAsync(tenant, 30_000m);
        var id = submitted.RequestId;

        await Main.InvokeAsync(new RejectStep(id, steps[0].Id, steps[0].Version, "Too expensive"), tenant, DemoUsers.Maria);
        var seen = await Recorder.WaitForAsync(
            id, s => s.Any(m => m.Kind == nameof(ReleaseBudget)) && s.Any(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.RequestRejected)), EventTimeout);

        var rejected = seen.Single(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.RequestRejected));
        rejected.Text("reason").ShouldBe("Too expensive");
        rejected.Text("rejectedBy").ShouldBe(DemoUsers.Maria);
        rejected.Text("requesterId").ShouldBe(DemoUsers.Tom);
        seen.Single(m => m.Kind == nameof(ReleaseBudget)).Text("tenantId").ShouldBe(tenant);
        (await Main.StepsAsync(id)).Select(s => s.Status).ShouldBe([ApprovalStepStatus.Rejected, ApprovalStepStatus.Cancelled]);
        (await Main.SagaAsync(id)).ShouldBeNull("a rejected saga is complete");
    }

    [Fact]
    public async Task Reminder_after_timeout_then_escalation_to_department_head()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        // The timeouts are driven by hand, in their scheduled order (on their own, both can be due at once and run either way).
        await using var fast = await app.StartManualInstanceAsync();
        var submitted = Submitted(tenant, DemoUsers.Tom, 30_000m);
        var id = submitted.RequestId;
        await fast.InvokeAsync(submitted, tenant, DemoUsers.Tom);
        await fast.InvokeAsync(Reserved(submitted), tenant, DemoUsers.Tom);
        var first = (await fast.StepsAsync(id))[0];

        await fast.InvokeAsync(new StepTimeout(id, first.Id, StepTimeoutKind.Reminder, 1), tenant, DemoUsers.Tom);
        await Recorder.WaitForAsync(id, nameof(ProcureAi.Contracts.Approvals.ApprovalReminderDue), EventTimeout);
        await fast.InvokeAsync(new StepTimeout(id, first.Id, StepTimeoutKind.Escalate, 1), tenant, DemoUsers.Tom);
        var seen = await Recorder.WaitForAsync(id, nameof(ProcureAi.Contracts.Approvals.ApprovalEscalated), EventTimeout);

        var reminder = seen.Single(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.ApprovalReminderDue));
        reminder.Text("stepId").ShouldBe(first.Id.ToString());
        reminder.Body.GetProperty("reminderNo").GetInt32().ShouldBe(1);
        reminder.Body.GetProperty("approverIds").EnumerateArray().Select(e => e.GetString()).ShouldBe([DemoUsers.Maria]);
        reminder.Text("number").ShouldBe(submitted.Number);
        var escalated = seen.Single(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.ApprovalEscalated));
        seen.ToList().IndexOf(reminder).ShouldBeLessThan(seen.ToList().IndexOf(escalated), "the reminder comes before the escalation");
        escalated.Text("fromLevel").ShouldBe("Manager");
        escalated.Text("toLevel").ShouldBe("DepartmentHead");
        escalated.Body.GetProperty("approverIds").EnumerateArray().Select(e => e.GetString()).ShouldBe([DemoUsers.Dept]);
        escalated.Text("number").ShouldBe(submitted.Number);

        // The replacement takes step 1 over and absorbs the department-head step that followed it.
        var steps = await fast.StepsAsync(id);
        var replacement = steps.Single(s => s.IsPending);
        replacement.Id.ToString().ShouldBe(escalated.Text("stepId"));
        replacement.StepNo.ShouldBe(1);
        replacement.Level.ShouldBe(ApprovalLevel.DepartmentHead);
        replacement.CoversLevels.ShouldBe([ApprovalLevel.Manager, ApprovalLevel.DepartmentHead]);
        steps.Single(s => s.Id == first.Id).Status.ShouldBe(ApprovalStepStatus.Escalated);
        steps.Single(s => s.StepNo == 2).Status.ShouldBe(ApprovalStepStatus.Cancelled);
        (await Recorder.WaitForAsync(id, nameof(ProcureAi.Contracts.Approvals.ApprovalRequested), EventTimeout, count: 2))
            .Last(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.ApprovalRequested)).Text("stepId").ShouldBe(replacement.Id.ToString());

        // Maria's escalated step is closed to her; the department head approves the whole request.
        await Should.ThrowAsync<ConflictException>(() =>
            fast.InvokeAsync(new ApproveStep(id, first.Id, steps.Single(s => s.Id == first.Id).Version), tenant, DemoUsers.Maria));
        await fast.InvokeAsync(new ApproveStep(id, replacement.Id, replacement.Version), tenant, DemoUsers.Dept);
        var approved = (await Recorder.WaitForAsync(id, nameof(ProcureAi.Contracts.Approvals.RequestApproved), EventTimeout))
            .Single(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.RequestApproved));
        approved.Body.GetProperty("approverIds").EnumerateArray().Select(e => e.GetString()).ShouldBe([DemoUsers.Dept]);
    }

    [Fact]
    public async Task Finance_step_overdue_raises_alert_and_keeps_reminding()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        await using var fast = await app.StartFastInstanceAsync();
        // R 600 000 is above the department head's limit: one finance step, and nobody above finance.
        var submitted = Submitted(tenant, DemoUsers.Tom, 600_000m);
        var id = submitted.RequestId;
        await fast.InvokeAsync(submitted, tenant, DemoUsers.Tom);
        await fast.InvokeAsync(Reserved(submitted), tenant, DemoUsers.Tom);

        var seen = await Recorder.WaitForAsync(id, nameof(ProcureAi.Contracts.Approvals.ApprovalReminderDue), EventTimeout, count: 3);

        var alert = seen.Single(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.ApprovalAlertRaised));
        alert.Text("reason").ShouldBe("Finance step overdue");
        seen.Where(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.ApprovalReminderDue))
            .Select(m => m.Body.GetProperty("reminderNo").GetInt32()).Take(3).ShouldBe([1, 2, 3]);
        seen.ShouldNotContain(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.ApprovalEscalated));
        var step = (await fast.StepsAsync(id)).Single();
        step.Level.ShouldBe(ApprovalLevel.Finance);
        step.Status.ShouldBe(ApprovalStepStatus.Pending);
        step.ApproverIds.ShouldBe([DemoUsers.Thandi]);
        // Still approvable: the alert does not close the step.
        await fast.InvokeAsync(new ApproveStep(id, step.Id, step.Version), tenant, DemoUsers.Thandi);
        await Recorder.WaitForAsync(id, nameof(ProcureAi.Contracts.Approvals.RequestApproved), EventTimeout);
    }

    [Fact]
    public async Task Finance_reminders_keep_going_when_the_escalation_runs_before_the_first_reminder()
    {
        // Both timeouts can be due at once (e.g. after an outage, or before the scheduler of a new node starts) and then run
        // in either order. The reminder and escalation are driven by hand here (their own schedule is hours away); only the
        // finance reminders that follow the alert run on their own.
        var tenant = NewTenant();
        app.UseAcme(tenant);
        await using var instance = await app.StartManualInstanceAsync();
        var submitted = Submitted(tenant, DemoUsers.Tom, 600_000m);
        var id = submitted.RequestId;
        await instance.InvokeAsync(submitted, tenant, DemoUsers.Tom);
        await instance.InvokeAsync(Reserved(submitted), tenant, DemoUsers.Tom);
        var step = (await instance.StepsAsync(id)).Single();

        await instance.InvokeAsync(new StepTimeout(id, step.Id, StepTimeoutKind.Escalate, 1), tenant, DemoUsers.Tom);
        await instance.InvokeAsync(new StepTimeout(id, step.Id, StepTimeoutKind.Reminder, 1), tenant, DemoUsers.Tom);

        var seen = await Recorder.WaitForAsync(id, nameof(ProcureAi.Contracts.Approvals.ApprovalReminderDue), EventTimeout, count: 3);
        seen.Count(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.ApprovalAlertRaised)).ShouldBe(1);
        seen.Where(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.ApprovalReminderDue))
            .Select(m => m.Body.GetProperty("reminderNo").GetInt32()).Take(3).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task Escalation_to_a_higher_level_absorbs_a_lower_following_step()
    {
        // Ruling 1 (amended): a following step at the replacement's level OR LOWER is merged into it. The chain is manager then
        // department head; by the time step 1 escalates, the department head may no longer approve, so it lands on finance,
        // which also stands for the department-head step that followed.
        var tenant = NewTenant();
        app.UseAcme(tenant);
        await using var instance = await app.StartManualInstanceAsync();
        var submitted = Submitted(tenant, DemoUsers.Tom, 30_000m);
        var id = submitted.RequestId;
        await instance.InvokeAsync(submitted, tenant, DemoUsers.Tom);
        await instance.InvokeAsync(Reserved(submitted), tenant, DemoUsers.Tom);
        var first = (await instance.StepsAsync(id))[0];

        app.UseAcme(tenant, deptCanApprove: false);
        await instance.ForgetAccessAsync(tenant);
        await instance.InvokeAsync(new StepTimeout(id, first.Id, StepTimeoutKind.Escalate, 1), tenant, DemoUsers.Tom);

        var escalated = (await Recorder.WaitForAsync(id, nameof(ProcureAi.Contracts.Approvals.ApprovalEscalated), EventTimeout))
            .Single(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.ApprovalEscalated));
        escalated.Text("fromLevel").ShouldBe("Manager");
        escalated.Text("toLevel").ShouldBe("Finance");
        var steps = await instance.StepsAsync(id);
        steps.Single(s => s.Id == first.Id).Status.ShouldBe(ApprovalStepStatus.Escalated);
        steps.Single(s => s.Level == ApprovalLevel.DepartmentHead).Status.ShouldBe(ApprovalStepStatus.Cancelled);
        var replacement = steps.Single(s => s.IsPending);
        replacement.StepNo.ShouldBe(1);
        replacement.Level.ShouldBe(ApprovalLevel.Finance);
        replacement.CoversLevels.ShouldBe([ApprovalLevel.Manager, ApprovalLevel.DepartmentHead, ApprovalLevel.Finance]);

        await instance.InvokeAsync(new ApproveStep(id, replacement.Id, replacement.Version), tenant, DemoUsers.Thandi);
        (await Recorder.WaitForAsync(id, nameof(ProcureAi.Contracts.Approvals.RequestApproved), EventTimeout))
            .Single(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.RequestApproved))
            .Body.GetProperty("approverIds").EnumerateArray().Select(e => e.GetString()).ShouldBe([DemoUsers.Thandi]);
    }

    [Fact]
    public async Task Overdue_alert_names_the_level_that_has_nobody_above()
    {
        // No finance approvers: maria's own R 30 000 request needs the department head, and nobody is above that step.
        var tenant = NewTenant();
        app.UseAcme(tenant, withFinance: false);
        await using var instance = await app.StartManualInstanceAsync();
        var submitted = Submitted(tenant, DemoUsers.Maria, 30_000m);
        var id = submitted.RequestId;
        await instance.InvokeAsync(submitted, tenant, DemoUsers.Maria);
        await instance.InvokeAsync(Reserved(submitted), tenant, DemoUsers.Maria);
        var step = (await instance.StepsAsync(id)).Single();
        step.Level.ShouldBe(ApprovalLevel.DepartmentHead);

        await instance.InvokeAsync(new StepTimeout(id, step.Id, StepTimeoutKind.Escalate, 1), tenant, DemoUsers.Maria);

        var alert = (await Recorder.WaitForAsync(id, nameof(ProcureAi.Contracts.Approvals.ApprovalAlertRaised), EventTimeout))
            .Single(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.ApprovalAlertRaised));
        alert.Text("reason").ShouldBe("Department head step overdue");
        // And it keeps reminding the department head.
        await Recorder.WaitForAsync(id, nameof(ProcureAi.Contracts.Approvals.ApprovalReminderDue), EventTimeout);
    }

    [Fact]
    public async Task Escalation_of_a_request_whose_cost_center_was_removed_raises_an_alert()
    {
        var tenant = NewTenant();
        var code = $"GONE-{Guid.NewGuid():N}"[..13];
        app.Access.SetApprovers(tenant, new BuildingBlocks.Application.Access.CostCenterApprovers(
            code, "IT", new BuildingBlocks.Application.Access.Approver(DemoUsers.Maria, "Maria", 200_000m, true), null, []));
        await using var instance = await app.StartManualInstanceAsync();
        var submitted = Submitted(tenant, DemoUsers.Tom, 5_000m) with { CostCenterCode = code };
        var id = submitted.RequestId;
        await instance.InvokeAsync(submitted, tenant, DemoUsers.Tom);
        await instance.InvokeAsync(Reserved(submitted), tenant, DemoUsers.Tom);
        var step = (await instance.StepsAsync(id)).Single();

        app.Access.MissingCostCenters[code] = true;
        await instance.ForgetAccessAsync(tenant);
        await instance.InvokeAsync(new StepTimeout(id, step.Id, StepTimeoutKind.Escalate, 1), tenant, DemoUsers.Tom);

        var alert = (await Recorder.WaitForAsync(id, nameof(ProcureAi.Contracts.Approvals.ApprovalAlertRaised), EventTimeout))
            .Single(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.ApprovalAlertRaised));
        alert.Text("reason").ShouldBe($"Unknown cost center {code}");
        (await instance.StepsAsync(id)).Single().Status.ShouldBe(ApprovalStepStatus.Pending, "still approvable by its approver");
    }

    [Fact]
    public async Task Transient_Access_failure_on_BudgetReserved_is_retried_until_the_chain_exists()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        var submitted = Submitted(tenant, DemoUsers.Tom, 30_000m);
        var id = submitted.RequestId;
        await Main.PublishAsync(submitted, Exchanges.Requests, tenant);
        await Recorder.WaitForAsync(id, nameof(ReserveBudget), EventTimeout);

        // Access answers 503 for a few seconds: longer than the HTTP client's own retries, shorter than the message's.
        app.Access.FailApprovers(tenant, AccessOutage);
        await Main.PublishAsync(Reserved(submitted), Exchanges.Budgets, tenant);

        await Recorder.WaitForAsync(id, nameof(ProcureAi.Contracts.Approvals.ApprovalRequested), RetryTimeout);
        (await Main.StepsAsync(id)).Count.ShouldBe(2);
        (await Main.SagaAsync(id))!.State.ShouldBe(ApprovalState.AwaitingApproval);
        Recorder.Of(id).ShouldNotContain(m => m.Kind == nameof(ReleaseBudget));
    }

    [Fact]
    public async Task Transient_Access_failure_on_escalation_is_retried()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        await using var instance = await app.StartManualInstanceAsync();
        var submitted = Submitted(tenant, DemoUsers.Tom, 30_000m);
        var id = submitted.RequestId;
        await instance.InvokeAsync(submitted, tenant, DemoUsers.Tom);
        await instance.InvokeAsync(Reserved(submitted), tenant, DemoUsers.Tom);
        var first = (await instance.StepsAsync(id))[0];

        // Access changed meanwhile (no cached answer to fall back on) and is briefly down when the escalation is due.
        await instance.ForgetAccessAsync(tenant);
        app.Access.FailApprovers(tenant, AccessOutage);
        await instance.SendAsync(new StepTimeout(id, first.Id, StepTimeoutKind.Escalate, 1), tenant, DemoUsers.Tom);

        var escalated = (await Recorder.WaitForAsync(id, nameof(ProcureAi.Contracts.Approvals.ApprovalEscalated), RetryTimeout))
            .Single(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.ApprovalEscalated));
        escalated.Text("toLevel").ShouldBe("DepartmentHead");
        (await instance.StepsAsync(id)).Single(s => s.Id == first.Id).Status.ShouldBe(ApprovalStepStatus.Escalated);
    }

    [Fact]
    public async Task Reservation_failure_after_cancel_ends_cancelled_without_rejection()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        var submitted = Submitted(tenant, DemoUsers.Tom, 950_000m);
        var id = submitted.RequestId;
        await Main.PublishAsync(submitted, Exchanges.Requests, tenant);
        await Recorder.WaitForAsync(id, nameof(ReserveBudget), EventTimeout);
        await Main.PublishAsync(Cancelled(submitted), Exchanges.Requests, tenant);
        await Main.WaitForSagaAsync(id, saga => saga is { CancelRequested: true }, EventTimeout);

        await Main.PublishAsync(
            new BudgetReservationFailed(id, tenant, CostCenter, "Insufficient budget", 950_000m, 900_000m, DateTimeOffset.UtcNow),
            Exchanges.Budgets, tenant);

        await Main.WaitForSagaAsync(id, saga => saga is null, EventTimeout);
        await Task.Delay(Settle);
        var seen = Recorder.Of(id);
        seen.ShouldNotContain(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.RequestRejected), "the requester cancelled first");
        seen.ShouldNotContain(m => m.Kind == nameof(ReleaseBudget), "nothing was reserved");
    }

    [Fact]
    public async Task Unknown_cost_center_rejects_with_its_own_reason_and_releases()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        var code = $"GONE-{Guid.NewGuid():N}"[..13];
        app.Access.MissingCostCenters[code] = true;
        var submitted = Submitted(tenant, DemoUsers.Tom, 5_000m) with { CostCenterCode = code };
        var id = submitted.RequestId;
        await Main.PublishAsync(submitted, Exchanges.Requests, tenant);
        await Recorder.WaitForAsync(id, nameof(ReserveBudget), EventTimeout);
        await Main.PublishAsync(Reserved(submitted), Exchanges.Budgets, tenant);

        var seen = await Recorder.WaitForAsync(
            id, s => s.Any(m => m.Kind == nameof(ReleaseBudget)) && s.Any(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.RequestRejected)), EventTimeout);
        seen.Single(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.RequestRejected)).Text("reason")
            .ShouldBe($"Unknown cost center {code}");
        await Main.WaitForSagaAsync(id, saga => saga is null, EventTimeout);
    }

    [Fact]
    public async Task Cancel_while_awaiting_budget_releases_after_reservation_arrives()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        var submitted = Submitted(tenant, DemoUsers.Tom, 30_000m);
        var id = submitted.RequestId;
        await Main.PublishAsync(submitted, Exchanges.Requests, tenant);
        await Recorder.WaitForAsync(id, nameof(ReserveBudget), EventTimeout);

        // The cancellation overtakes the reservation, which Budgets then confirms.
        await Main.PublishAsync(Cancelled(submitted), Exchanges.Requests, tenant);
        await Main.WaitForSagaAsync(id, saga => saga is { CancelRequested: true }, EventTimeout);
        await Main.PublishAsync(Reserved(submitted), Exchanges.Budgets, tenant);

        await Recorder.WaitForAsync(id, nameof(ReleaseBudget), EventTimeout);
        await Main.WaitForSagaAsync(id, saga => saga is null, EventTimeout);
        await Task.Delay(Settle);
        var seen = Recorder.Of(id);
        seen.Count(m => m.Kind == nameof(ReleaseBudget)).ShouldBe(1);
        seen.ShouldNotContain(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.ApprovalRequested));
        seen.ShouldNotContain(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.RequestRejected));
        (await Main.StepsAsync(id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancel_after_approval_releases_once()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        var (submitted, steps) = await AwaitingApprovalAsync(tenant, 5_000m);
        var id = submitted.RequestId;
        await Main.InvokeAsync(new ApproveStep(id, steps[0].Id, steps[0].Version), tenant, DemoUsers.Maria);
        await Recorder.WaitForAsync(id, nameof(ProcureAi.Contracts.Approvals.RequestApproved), EventTimeout);

        await Main.PublishAsync(Cancelled(submitted), Exchanges.Requests, tenant);
        // Approved requests are cancelled through Purchasing (plan 03): the saga asks, and Purchasing's answer ends it.
        await Recorder.WaitForAsync(id, nameof(ProcureAi.Contracts.Purchasing.CancelPurchaseOrder), EventTimeout);
        await Main.PublishAsync(
            new ProcureAi.Contracts.Purchasing.PurchaseOrderCancelled(null, id, tenant, "Cancelled", DateTimeOffset.UtcNow),
            Exchanges.Purchasing, tenant);
        await Recorder.WaitForAsync(id, nameof(ReleaseBudget), EventTimeout);
        await Main.WaitForSagaAsync(id, saga => saga is null, EventTimeout);
        // The cancellation delivered again finds no saga: nothing more is released.
        await Main.PublishAsync(Cancelled(submitted), Exchanges.Requests, tenant);
        await Task.Delay(Settle);

        Recorder.Of(id).Count(m => m.Kind == nameof(ReleaseBudget)).ShouldBe(1);
        (await Main.StepsAsync(id)).Single().Status.ShouldBe(ApprovalStepStatus.Approved);
    }

    [Fact]
    public async Task Duplicate_BudgetReserved_creates_steps_once()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        var submitted = Submitted(tenant, DemoUsers.Tom, 30_000m);
        var id = submitted.RequestId;
        await Main.PublishAsync(submitted, Exchanges.Requests, tenant);
        await Recorder.WaitForAsync(id, nameof(ReserveBudget), EventTimeout);

        // Two deliveries of the same answer at once (different envelopes, so the inbox does not drop the second).
        await Task.WhenAll(
            Main.PublishAsync(Reserved(submitted), Exchanges.Budgets, tenant),
            Main.PublishAsync(Reserved(submitted), Exchanges.Budgets, tenant),
            Main.PublishAsync(Reserved(submitted), Exchanges.Budgets, tenant));
        await Recorder.WaitForAsync(id, nameof(ProcureAi.Contracts.Approvals.ApprovalRequested), EventTimeout);
        await Task.Delay(Settle);

        (await Main.StepsAsync(id)).Count.ShouldBe(2);
        var seen = Recorder.Of(id);
        seen.Count(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.ApprovalRequested)).ShouldBe(1);
        seen.ShouldNotContain(m => m.Kind == nameof(ReleaseBudget), "a repeated reservation of a live saga is ignored, not released");
    }

    [Fact]
    public async Task Requester_cannot_approve_own_step_even_after_escalation()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        await using var fast = await app.StartFastInstanceAsync();
        // The department head raises R 30 000: the head step moves up to finance (1: maria, 2: thandi). When maria's step
        // escalates, the department-head level is skipped (it is the requester) and it lands on finance.
        var submitted = Submitted(tenant, DemoUsers.Dept, 30_000m);
        var id = submitted.RequestId;
        await fast.InvokeAsync(submitted, tenant, DemoUsers.Dept);
        await fast.InvokeAsync(Reserved(submitted), tenant, DemoUsers.Dept);
        (await fast.StepsAsync(id)).Select(s => $"{s.StepNo}:{s.Level}:{string.Join(",", s.ApproverIds)}")
            .ShouldBe([$"1:Manager:{DemoUsers.Maria}", $"2:Finance:{DemoUsers.Thandi}"]);

        var escalated = (await Recorder.WaitForAsync(id, nameof(ProcureAi.Contracts.Approvals.ApprovalEscalated), EventTimeout))
            .Single(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.ApprovalEscalated));
        escalated.Text("toLevel").ShouldBe("Finance");
        var steps = await fast.StepsAsync(id);
        steps.ShouldAllBe(s => !s.ApproverIds.Contains(DemoUsers.Dept));
        var open = steps.Single(s => s.IsPending);
        open.ApproverIds.ShouldBe([DemoUsers.Thandi]);

        var forbidden = await Should.ThrowAsync<ForbiddenException>(() =>
            fast.InvokeAsync(new ApproveStep(id, open.Id, open.Version), tenant, DemoUsers.Dept));
        forbidden.Message.ShouldContain("You raised this request");
        await Should.ThrowAsync<ForbiddenException>(() =>
            fast.InvokeAsync(new RejectStep(id, open.Id, open.Version, "Mine"), tenant, DemoUsers.Dept));
        // A stranger to the step is refused too; its approver may act.
        await Should.ThrowAsync<ForbiddenException>(() =>
            fast.InvokeAsync(new ApproveStep(id, open.Id, open.Version), tenant, DemoUsers.Maria));
        await fast.InvokeAsync(new ApproveStep(id, open.Id, open.Version), tenant, DemoUsers.Thandi);
        await Recorder.WaitForAsync(id, nameof(ProcureAi.Contracts.Approvals.RequestApproved), EventTimeout);
    }

    [Fact]
    public async Task Stale_version_conflicts()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        var (submitted, steps) = await AwaitingApprovalAsync(tenant, 30_000m);
        var id = submitted.RequestId;
        var step = steps[0];

        await Should.ThrowAsync<ConcurrencyConflictException>(() =>
            Main.InvokeAsync(new ApproveStep(id, step.Id, step.Version + 1), tenant, DemoUsers.Maria));
        await Should.ThrowAsync<ConcurrencyConflictException>(() =>
            Main.InvokeAsync(new RejectStep(id, step.Id, step.Version - 1, "No"), tenant, DemoUsers.Maria));
        (await Main.StepsAsync(id))[0].Status.ShouldBe(ApprovalStepStatus.Pending);

        await Main.InvokeAsync(new ApproveStep(id, step.Id, step.Version), tenant, DemoUsers.Maria);
        // The same approval again: the step is not open any more (409), whatever version is sent.
        await Should.ThrowAsync<ConflictException>(() =>
            Main.InvokeAsync(new ApproveStep(id, step.Id, step.Version), tenant, DemoUsers.Maria));
        // Another tenant cannot see the request's steps at all.
        var next = (await Main.StepsAsync(id))[1];
        await Should.ThrowAsync<NotFoundException>(() =>
            Main.InvokeAsync(new ApproveStep(id, next.Id, next.Version), NewTenant(), DemoUsers.Dept));
    }

    [Fact]
    public async Task Late_BudgetReserved_for_an_unknown_or_completed_saga_is_released()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        // Unknown: e.g. redelivered long after the saga was cancelled and deleted.
        var unknown = Submitted(tenant, DemoUsers.Tom, 1_000m);
        await Main.PublishAsync(Reserved(unknown), Exchanges.Budgets, tenant);
        var release = (await Recorder.WaitForAsync(unknown.RequestId, nameof(ReleaseBudget), EventTimeout))
            .Single(m => m.Kind == nameof(ReleaseBudget));
        release.Text("tenantId").ShouldBe(tenant);

        // Completed: rejected by the approver, then the reservation is delivered again.
        var (submitted, steps) = await AwaitingApprovalAsync(tenant, 5_000m);
        await Main.InvokeAsync(new RejectStep(submitted.RequestId, steps[0].Id, steps[0].Version, "No"), tenant, DemoUsers.Maria);
        await Main.WaitForSagaAsync(submitted.RequestId, saga => saga is null, EventTimeout);
        await Main.PublishAsync(Reserved(submitted), Exchanges.Budgets, tenant);
        await Recorder.WaitForAsync(submitted.RequestId, nameof(ReleaseBudget), EventTimeout, count: 2);
    }

    [Fact]
    public async Task Saga_state_steps_and_outbox_commit_together()
    {
        // Own RabbitMQ and own database, so stopping the broker cannot disturb other tests.
        await using var rabbit = await RabbitContainer.StartAsync();
        var database = await app.Infra.CreateEmptyDatabaseAsync($"outage_{Guid.NewGuid():N}");
        await app.Infra.MigrateAsync(database, rabbit.ConnectionString);
        await using var instance = ApprovalsInstance.Start(database, rabbit.ConnectionString, app.Infra.RedisConnectionString, app.Access);
        await rabbit.BindQueueAsync("outage-approvals", Exchanges.Approvals);
        var tenant = NewTenant();
        app.UseAcme(tenant);
        var submitted = Submitted(tenant, DemoUsers.Tom, 30_000m);
        var id = submitted.RequestId;
        await instance.InvokeAsync(submitted, tenant, DemoUsers.Tom);
        (await rabbit.WaitForMessageAsync(Queues.BudgetCommands, EventTimeout)).ShouldContain(id.ToString());

        await rabbit.StopAsync();
        await instance.InvokeAsync(Reserved(submitted), tenant, DemoUsers.Tom);

        // The saga's new state, its steps and the ApprovalRequested waiting in the outbox were committed in one transaction.
        (await instance.SagaAsync(id))!.State.ShouldBe(ApprovalState.AwaitingApproval);
        (await instance.StepsAsync(id)).Count.ShouldBe(2);
        (await instance.OutgoingAsync()).ShouldBeGreaterThan(0);

        await rabbit.ResumeAsync();
        var delivered = await rabbit.WaitForMessageAsync("outage-approvals", EventTimeout);
        delivered.ShouldContain(id.ToString());
        delivered.ShouldContain((await instance.StepsAsync(id))[0].Id.ToString());
    }

    [Fact]
    public async Task Cancel_before_the_submission_ends_cancelled_and_reserves_nothing()
    {
        // approvals-api was down while Tom submitted and cancelled; the outbox delivers the cancellation first.
        var tenant = NewTenant();
        app.UseAcme(tenant);
        var submitted = Submitted(tenant, DemoUsers.Tom, 30_000m);
        var id = submitted.RequestId;

        await Main.PublishAsync(Cancelled(submitted), Exchanges.Requests, tenant);
        await Task.Delay(Settle);
        await Main.PublishAsync(submitted, Exchanges.Requests, tenant);
        await Task.Delay(Settle);

        var seen = Recorder.Of(id);
        seen.ShouldNotContain(m => m.Kind == nameof(ReserveBudget), "the request was cancelled before its submission arrived");
        seen.ShouldNotContain(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.ApprovalRequested));
        seen.ShouldNotContain(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.RequestRejected));
        (await Main.SagaAsync(id))!.State.ShouldBe(ApprovalState.Cancelled);
        (await Main.StepsAsync(id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancel_and_submission_handled_at_once_never_leave_a_reservation_or_a_chain()
    {
        // Both messages wait in approvals-requests and the listener handles them in parallel, in either order.
        var tenant = NewTenant();
        app.UseAcme(tenant);
        var requests = Enumerable.Range(0, 5).Select(_ => Submitted(tenant, DemoUsers.Tom, 30_000m)).ToList();
        await Task.WhenAll(requests.SelectMany(submitted => new[]
        {
            Main.PublishAsync(submitted, Exchanges.Requests, tenant),
            Main.PublishAsync(Cancelled(submitted), Exchanges.Requests, tenant),
        }));

        foreach (var submitted in requests)
        {
            var id = submitted.RequestId;
            // Either the cancellation came first (the saga is a cancelled marker) or second (it waits for Budgets' answer).
            var saga = await Main.WaitForSagaAsync(
                id, saga => saga is { State: ApprovalState.Cancelled } or { State: ApprovalState.AwaitingBudget, CancelRequested: true },
                EventTimeout);
            // The saga's state says what happened (the recorder may not have received the committed ReserveBudget yet).
            if (saga!.State == ApprovalState.AwaitingBudget)
            {
                await Recorder.WaitForAsync(id, nameof(ReserveBudget), EventTimeout);
                await Main.PublishAsync(Reserved(submitted), Exchanges.Budgets, tenant);
                await Recorder.WaitForAsync(id, nameof(ReleaseBudget), EventTimeout);
                await Main.WaitForSagaAsync(id, saga => saga is null, EventTimeout);
            }
        }

        await Task.Delay(Settle);
        foreach (var submitted in requests)
        {
            var seen = Recorder.Of(submitted.RequestId);
            seen.Count(m => m.Kind == nameof(ReleaseBudget)).ShouldBe(seen.Count(m => m.Kind == nameof(ReserveBudget)));
            seen.ShouldNotContain(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.ApprovalRequested));
            seen.ShouldNotContain(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.RequestRejected));
            (await Main.StepsAsync(submitted.RequestId)).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task Reservation_refused_as_cancelled_ends_cancelled_without_rejection()
    {
        // Budgets refuses with "Cancelled" when the request's release reached it before the reservation.
        var tenant = NewTenant();
        app.UseAcme(tenant);
        var submitted = Submitted(tenant, DemoUsers.Tom, 30_000m);
        var id = submitted.RequestId;
        await Main.PublishAsync(submitted, Exchanges.Requests, tenant);
        await Recorder.WaitForAsync(id, nameof(ReserveBudget), EventTimeout);

        await Main.PublishAsync(
            new BudgetReservationFailed(
                id, tenant, CostCenter, BudgetReservationFailed.CancelledReason, 30_000m, 0m, DateTimeOffset.UtcNow),
            Exchanges.Budgets, tenant);

        await Main.WaitForSagaAsync(id, saga => saga is null, EventTimeout);
        await Task.Delay(Settle);
        var seen = Recorder.Of(id);
        seen.ShouldNotContain(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.RequestRejected), "the request was cancelled");
        seen.ShouldNotContain(m => m.Kind == nameof(ReleaseBudget), "nothing was reserved");
    }

    [Fact]
    public async Task Cancel_racing_the_final_approval_releases_exactly_once()
    {
        // Several requests, each approval 0.2 s later than the one before, against cancellations through the broker, so both
        // orders can occur. Which ones do depends on the machine (a run may see only cancel-first); the approve-first order is
        // also pinned deterministically by Cancel_after_approval_releases_once. Not asserted, so the test cannot flake on it.
        var tenant = NewTenant();
        app.UseAcme(tenant);
        var waiting = new List<(ProcureAi.Contracts.Requests.RequestSubmitted Submitted, ApprovalStep Step)>();
        for (var i = 0; i < 6; i++)
        {
            var (submitted, steps) = await AwaitingApprovalAsync(tenant, 5_000m);
            waiting.Add((submitted, steps.Single()));
        }

        var approvedIds = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        await Task.WhenAll(waiting.SelectMany((w, i) => new[]
        {
            Main.PublishAsync(Cancelled(w.Submitted), Exchanges.Requests, tenant),
            ApproveOrConflictAsync(w.Submitted.RequestId, w.Step, tenant, TimeSpan.FromMilliseconds(i * 200), approvedIds),
        }));

        foreach (var (submitted, step) in waiting.Where(w => approvedIds.Contains(w.Submitted.RequestId)))
        {
            // Approved first: the cancellation went to Purchasing, whose answer (played here) ends the saga.
            var id = submitted.RequestId;
            await Main.WaitForSagaAsync(id, saga => saga?.State == ApprovalState.Cancelling, EventTimeout);
            await Main.PublishAsync(
                new ProcureAi.Contracts.Purchasing.PurchaseOrderCancelled(null, id, tenant, "Cancelled", DateTimeOffset.UtcNow),
                Exchanges.Purchasing, tenant);
        }

        foreach (var (submitted, step) in waiting)
        {
            var id = submitted.RequestId;
            await Recorder.WaitForAsync(id, nameof(ReleaseBudget), EventTimeout);
            await Main.WaitForSagaAsync(id, saga => saga is null, EventTimeout);
        }

        await Task.Delay(Settle);
        foreach (var (submitted, step) in waiting)
        {
            var id = submitted.RequestId;
            var seen = Recorder.Of(id);
            seen.Count(m => m.Kind == nameof(ReleaseBudget)).ShouldBe(1, "the budget is released exactly once, whichever came first");
            var stored = (await Main.StepsAsync(id)).Single();
            if (approvedIds.Contains(id))
            {
                seen.Count(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.RequestApproved)).ShouldBe(1);
                stored.Status.ShouldBe(ApprovalStepStatus.Approved);
            }
            else
            {
                seen.ShouldNotContain(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.RequestApproved));
                stored.Status.ShouldBe(ApprovalStepStatus.Cancelled);
            }
        }
    }

    /// <summary>Approves the step; a cancellation that got there first answers 409, which is an expected outcome of the race.</summary>
    private async Task ApproveOrConflictAsync(
        Guid id, ApprovalStep step, string tenant, TimeSpan delay, System.Collections.Concurrent.ConcurrentBag<Guid> approved)
    {
        await Task.Delay(delay);
        try
        {
            await Main.InvokeAsync(new ApproveStep(id, step.Id, step.Version), tenant, DemoUsers.Maria);
            approved.Add(id);
        }
        catch (ConflictException)
        {
        }
    }

    /// <summary>Submits through the broker, confirms the reservation, and waits until step 1 is open.</summary>
    private async Task<(ProcureAi.Contracts.Requests.RequestSubmitted Submitted, List<ApprovalStep> Steps)> AwaitingApprovalAsync(
        string tenant, decimal total)
    {
        var submitted = Submitted(tenant, DemoUsers.Tom, total);
        await Main.PublishAsync(submitted, Exchanges.Requests, tenant);
        await Recorder.WaitForAsync(submitted.RequestId, nameof(ReserveBudget), EventTimeout);
        await Main.PublishAsync(Reserved(submitted), Exchanges.Budgets, tenant);
        await Recorder.WaitForAsync(submitted.RequestId, nameof(ProcureAi.Contracts.Approvals.ApprovalRequested), EventTimeout);
        return (submitted, await Main.StepsAsync(submitted.RequestId));
    }
}
