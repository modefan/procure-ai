using Microsoft.Extensions.DependencyInjection;
using ProcureAi.Approvals.Application.Sagas;
using ProcureAi.Contracts;
using ProcureAi.BuildingBlocks.Application.Status;
using Shouldly;
using static ProcureAi.Approvals.IntegrationTests.ApprovalsApp;

namespace ProcureAi.Approvals.IntegrationTests;

/// <summary>Approvals' part of the system status snapshot (plan 06 ruling 10): running sagas, timeouts due within the hour, open finance alerts.</summary>
[Collection(ApprovalsCollection.Name)]
public sealed class StatusContributorTests(ApprovalsApp app)
{
    /// <summary>The reminder is due within the hour, the escalation and the finance reminders are not.</summary>
    private static readonly IReadOnlyDictionary<string, string?> Timeouts = new Dictionary<string, string?>
    {
        ["Approvals:Timeouts:ReminderAfter"] = "00:30:00",
        ["Approvals:Timeouts:EscalateAfter"] = "02:00:00",
        ["Approvals:Timeouts:FinanceReminderEvery"] = "05:00:00",
    };

    [Fact]
    public async Task Approvals_counts_running_sagas_and_due_timeouts()
    {
        var tenant = NewTenant();
        var other = NewTenant();
        app.UseAcme(tenant);
        app.UseAcme(other);
        await using var instance = await app.StartFastInstanceAsync(Timeouts);

        // Waiting for approval: a reminder in 30 minutes, an escalation in 2 hours.
        var waiting = Submitted(tenant, DemoUsers.Tom, 30_000m);
        await instance.InvokeAsync(waiting, tenant, DemoUsers.Tom);
        await instance.InvokeAsync(Reserved(waiting), tenant, DemoUsers.Tom);
        // Overdue with nobody above (finance's own step): the alert is raised, and its finance reminders come every 5 hours.
        var overdue = Submitted(tenant, DemoUsers.Tom, 600_000m);
        await instance.InvokeAsync(overdue, tenant, DemoUsers.Tom);
        await instance.InvokeAsync(Reserved(overdue), tenant, DemoUsers.Tom);
        var step = (await instance.StepsAsync(overdue.RequestId)).Single();
        await instance.InvokeAsync(new StepTimeout(overdue.RequestId, step.Id, StepTimeoutKind.Escalate, 1), tenant, DemoUsers.Tom);
        (await instance.SagaAsync(overdue.RequestId))!.OverdueAlertStepId.ShouldBe(step.Id);
        // Waiting for the budget: running, nothing scheduled.
        await instance.InvokeAsync(Submitted(tenant, DemoUsers.Tom, 1_000m), tenant, DemoUsers.Tom);
        // Rejected: ended, not running.
        var rejected = Submitted(tenant, DemoUsers.Tom, 2_000m);
        await instance.InvokeAsync(rejected, tenant, DemoUsers.Tom);
        await instance.InvokeAsync(
            new ProcureAi.Contracts.Budgets.BudgetReservationFailed(rejected.RequestId, tenant, CostCenter, "Insufficient budget", 2_000m, 0m, DateTimeOffset.UtcNow),
            tenant, DemoUsers.Tom);
        // Another tenant's saga is counted under its own tenant.
        var theirs = Submitted(other, DemoUsers.Tom, 30_000m);
        await instance.InvokeAsync(theirs, other, DemoUsers.Tom);
        await instance.InvokeAsync(Reserved(theirs), other, DemoUsers.Tom);

        await using var scope = instance.Services.CreateAsyncScope();
        var contributor = scope.ServiceProvider.GetServices<IStatusContributor>().Single(c => c.Name == "approvals");
        var section = await contributor.CollectAsync(TestContext.Current.CancellationToken);

        section.Counters[StatusCounters.Key(tenant, StatusCounters.SagasRunning)].ShouldBe(3);
        section.Counters[StatusCounters.Key(tenant, StatusCounters.TimeoutsNextHour)].ShouldBe(2, "the two reminders; escalations and finance reminders come later");
        section.Counters[StatusCounters.Key(tenant, StatusCounters.FinanceAlertsOpen)].ShouldBe(1);
        section.Counters[StatusCounters.Key(other, StatusCounters.SagasRunning)].ShouldBe(1);
        section.Counters[StatusCounters.Key(other, StatusCounters.TimeoutsNextHour)].ShouldBe(1);
        section.Counters.ShouldNotContainKey(StatusCounters.Key(other, StatusCounters.FinanceAlertsOpen));

        // Approving the overdue step closes the alert.
        await instance.InvokeAsync(new ApproveStep(overdue.RequestId, step.Id, (await instance.StepsAsync(overdue.RequestId)).Single(s => s.Id == step.Id).Version), tenant, DemoUsers.Thandi);
        var after = await contributor.CollectAsync(TestContext.Current.CancellationToken);
        after.Counters.ShouldNotContainKey(StatusCounters.Key(tenant, StatusCounters.FinanceAlertsOpen));
    }
}
