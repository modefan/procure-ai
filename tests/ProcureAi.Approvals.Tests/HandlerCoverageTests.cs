using ProcureAi.Approvals.Application.Sagas;

namespace ProcureAi.Approvals.Tests;

public sealed class HandlerCoverageTests
{
    [Fact]
    public void Approvals_handles_every_requests_and_budgets_event() =>
        HandlerCoverage.AssertEveryEventHandled(
            typeof(ApprovalSaga).Assembly, "ProcureAi.Contracts.Requests", "ProcureAi.Contracts.Budgets");

    [Fact]
    public void Every_purchasing_and_invoicing_event_has_a_handler() =>
        HandlerCoverage.AssertEveryEventHandled(
            typeof(ApprovalSaga).Assembly, "ProcureAi.Contracts.Purchasing", "ProcureAi.Contracts.Invoicing");
}
