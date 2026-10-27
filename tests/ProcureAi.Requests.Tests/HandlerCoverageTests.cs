using ProcureAi.Requests.Application.Events;
using Shouldly;

namespace ProcureAi.Requests.Tests;

public sealed class HandlerCoverageTests
{
    [Fact]
    public void Requests_handles_every_approvals_event() =>
        HandlerCoverage.AssertEveryEventHandled(typeof(RequestOutcomeHandler).Assembly, "ProcureAi.Contracts.Approvals");

    [Fact]
    public void The_check_names_the_events_nobody_handles() =>
        Should.Throw<InvalidOperationException>(
                () => HandlerCoverage.AssertEveryEventHandled(typeof(RequestOutcomeHandler).Assembly, "ProcureAi.Contracts.Budgets"))
            .Message.ShouldContain("ProcureAi.Contracts.Budgets.BudgetReserved");
}
