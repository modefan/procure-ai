using ProcureAi.Invoicing.Application.Commands;

namespace ProcureAi.Invoicing.Tests;

public sealed class HandlerCoverageTests
{
    /// <summary>Invoicing's queue is bound to the whole <c>purchasing</c> exchange: every event published there needs a handler here.</summary>
    [Fact]
    public void Invoicing_handles_every_purchasing_event() =>
        HandlerCoverage.AssertEveryEventHandled(typeof(PurchaseOrderCreatedHandler).Assembly, "ProcureAi.Contracts.Purchasing");
}
