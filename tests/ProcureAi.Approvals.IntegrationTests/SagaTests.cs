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
        seen.Single(m => m.Kind == nameof(ReleaseBudget)).Text("tenantId").ShouldBe(tenant);
        (await Main.StepsAsync(id)).Select(s => s.Status).ShouldBe([ApprovalStepStatus.Rejected, ApprovalStepStatus.Cancelled]);
        (await Main.SagaAsync(id)).ShouldBeNull("a rejected saga is complete");
    }

    [Fact]
    public async Task Reminder_after_timeout_then_escalation_to_department_head()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        await using var fast = await app.StartFastInstanceAsync();
        var submitted = Submitted(tenant, DemoUsers.Tom, 30_000m);
        var id = submitted.RequestId;
        await fast.InvokeAsync(submitted, tenant, DemoUsers.Tom);
        await fast.InvokeAsync(Reserved(submitted), tenant, DemoUsers.Tom);
        var first = (await fast.StepsAsync(id))[0];

        var seen = await Recorder.WaitForAsync(id, nameof(ProcureAi.Contracts.Approvals.ApprovalEscalated), EventTimeout);

        var reminder = seen.Single(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.ApprovalReminderDue));
        reminder.Text("stepId").ShouldBe(first.Id.ToString());
        reminder.Body.GetProperty("reminderNo").GetInt32().ShouldBe(1);
        reminder.Body.GetProperty("approverIds").EnumerateArray().Select(e => e.GetString()).ShouldBe([DemoUsers.Maria]);
        var escalated = seen.Single(m => m.Kind == nameof(ProcureAi.Contracts.Approvals.ApprovalEscalated));
        seen.ToList().IndexOf(reminder).ShouldBeLessThan(seen.ToList().IndexOf(escalated), "the reminder comes before the escalation");
        escalated.Text("fromLevel").ShouldBe("Manager");
        escalated.Text("toLevel").ShouldBe("DepartmentHead");
        escalated.Body.GetProperty("approverIds").EnumerateArray().Select(e => e.GetString()).ShouldBe([DemoUsers.Dept]);

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
        alert.Text("reason").ShouldBe(ApprovalSaga.FinanceStepOverdue);
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
        await using var instance = await app.StartFastInstanceAsync(new Dictionary<string, string?>
        {
            ["Approvals:Timeouts:ReminderAfter"] = "01:00:00",
            ["Approvals:Timeouts:EscalateAfter"] = "02:00:00",
            ["Approvals:Timeouts:FinanceReminderEvery"] = "00:00:02",
        });
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
