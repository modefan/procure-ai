using ProcureAi.Notifications.Application.Handlers;

namespace ProcureAi.Notifications.Tests;

public sealed class HandlerCoverageTests
{
    /// <summary>The live queue is bound to these five exchanges, so every event of them arrives and needs a handler of its own.</summary>
    [Fact]
    public void Every_event_of_the_live_exchanges_has_a_handler() =>
        HandlerCoverage.AssertEveryEventHandled(
            typeof(RequestEventsHandler).Assembly,
            "ProcureAi.Contracts.Requests",
            "ProcureAi.Contracts.Approvals",
            "ProcureAi.Contracts.Budgets",
            "ProcureAi.Contracts.Purchasing",
            "ProcureAi.Contracts.Invoicing");
}
