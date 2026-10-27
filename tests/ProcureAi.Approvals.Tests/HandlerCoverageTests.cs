using ProcureAi.Approvals.Application.Sagas;

namespace ProcureAi.Approvals.Tests;

public sealed class HandlerCoverageTests
{
    [Fact]
    public void Approvals_handles_every_requests_and_budgets_event() =>
        HandlerCoverage.AssertEveryEventHandled(
            typeof(ApprovalSaga).Assembly, "ProcureAi.Contracts.Requests", "ProcureAi.Contracts.Budgets");
}
